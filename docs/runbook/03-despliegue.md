# Runbook 03 — Despliegue

> SPEC 13 y SPEC 14. No hay registry. En cada push a `main`, el CI (`.github/workflows/ci.yml`) construye las imágenes
> en el runner y, tras la aprobación de un revisor del environment `produccion`, las envía al VPS por SSH junto con
> `deploy/`. En el VPS, `halsa-recibir-despliegue` las carga y llama a `desplegar.sh`. Nadie entra al VPS para un
> despliegue normal.

## Flujo normal

1. Mergear a `main` con el CI en verde: build, tests con cobertura, `nginx -t` sobre `deploy/npm` y `validar-despliegue`.
2. En GitHub → *Actions* → el run de `main` → job **Desplegar en producción** → *Review deployments* → aprobar
   `produccion`. Sin aprobación no se abre ninguna conexión al VPS.
3. El job:
   1. construye `localhost/halsa-backend:v1.0.<run_number>` y `localhost/halsa-bff-paciente:v1.0.<run_number>`
      (y `localhost/halsa-postgres:17` / `17-v1.0.<n>` solo si el push tocó `deploy/postgres/`, o si se lanzó a mano con
      `incluir_postgres`);
   2. arma `paquete.tar` (imágenes, `git archive` de `deploy/` y sus sumas sha256);
   3. ejecuta `ssh halsa-deploy@<vps> "desplegar v1.0.<n>" < paquete.tar`.
4. En el VPS, `halsa-recibir-despliegue` (`deploy/scripts/recibir-despliegue.sh`), como `edhunter` (`sudo -u`):
   1. verifica el comando, el tamaño y las sumas; si algo falla, sale con `1` **sin tocar nada**;
   2. `podman load` de las imágenes, en el almacén de `edhunter`;
   3. sincroniza `~/halsa/deploy` con `rsync --delete`, sin tocar `.env`;
   4. ejecuta `desplegar.sh v1.0.<n>`:
      1. verifica que las imágenes del tag estén cargadas;
      2. fija `HALSA_TAG` en `.env` y `systemctl --user start halsa-migrador`, que sale con el código del Migrador
         (`0` o `1`). Si falla, vuelve el tag y no toca nada más;
      3. `systemctl --user restart halsa-api halsa-worker halsa-bff`;
      4. espera hasta 120 s a que `/health` de `api` y `worker` esté en `Healthy`;
      5. sincroniza la configuración de borde con NPM (`sincronizar-npm.sh`, que valida con `nginx -t` antes de recargar);
      6. si no llega a `Healthy`, **vuelve al tag anterior**. Termina con `1` en cualquier caso de fallo;
   5. si todo terminó bien: conserva las 5 versiones más recientes (más la actual y la anterior) y borra la entrega.
5. Revisar Seq: ningún `Error` nuevo desde el despliegue.

Si el job falla, su log tiene la salida completa de los dos scripts (y las últimas líneas de
`journalctl --user -u halsa-migrador` si falló la migración). Una entrega fallida queda en
`~/.local/state/halsa/entregas/<tag>/` para diagnóstico; se puede borrar a mano.

Dos despliegues nunca corren a la vez: el job tiene su grupo de concurrencia (`despliegue-produccion`) y el script
toma `/run/user/<uid de edhunter>/halsa-despliegue.lock`. Si quedan varios esperando aprobación, basta con aprobar el más reciente y
rechazar los anteriores.

## Rollback manual

`desplegar.sh` vuelve a cualquier tag que siga cargado en el VPS. Como `edhunter` (nunca como root: el root no ve
las imágenes ni las unidades de `edhunter`):

```bash
podman image ls localhost/halsa-backend                 # tags disponibles (los 5 más recientes)
~/halsa/deploy/scripts/desplegar.sh v1.0.41
```

Solo las migraciones, sin cambiar de versión: `bash ~/halsa/deploy-quadlets/migrar.sh`.

Un tag que ya no está cargado falla con `1` sin cambiar nada (`FALLO: falta la imagen ...`). Para volver a él,
relanza en GitHub el run de `main` que lo creó (*Re-run all jobs*): un re-run conserva el `run_number`, así que
construye y envía otra vez el mismo `v1.0.<n>`.

El rollback vuelve a la imagen anterior con el `deploy/` **nuevo** (scripts y `arrancar-dotnet.sh`). Por eso, igual
que las migraciones, los cambios en `deploy/` deben ser compatibles con la versión anterior, y el PR lo declara. Las
unidades de `deploy-quadlets/` no cambian con un despliegue: solo con `instalar.sh` (deploy-quadlets/README).

## PostgreSQL: imagen nueva

El CI solo **carga** `localhost/halsa-postgres:17` cuando cambia `deploy/postgres/`; `desplegar.sh` nunca reinicia
`halsa-postgres`. Para aplicarla, en una ventana de mantenimiento. Corta las conexiones y el archivado unos segundos,
y por `Requires=` reinicia también api, worker y bff:

```bash
podman image ls localhost/halsa-postgres                # 17 y 17-v1.0.<n>
systemctl --user restart halsa-postgres                 # espera a healthy (Notify=healthy)
systemctl --user start halsa-api halsa-worker halsa-bff
podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa check
```

## Transición a rootless (una sola vez)

Para un VPS que corría Halsa con `podman compose` **como root** (`/opt/halsa`, `/etc/halsa`). El root y `edhunter`
tienen almacenes, redes y volúmenes de Podman distintos: se copian los secretos, la PKI, las imágenes y los datos, y
se apaga lo de root. En una ventana de mantenimiento, con el responsable de la plataforma. Halsa responde `502`
mientras dura.

```bash
# Como edhunter. R = el compose de root, que sigue disponible para volver atrás.
R="sudo podman compose -f /opt/halsa/deploy/docker-compose.prod.yml --env-file /opt/halsa/deploy/.env"

# 1. Un backup completo del postgres de root y apagarlo, con sus cron: dos postgres archivarían en la misma stanza.
$R exec -T -u postgres postgres pgbackrest --stanza=halsa --type=full backup
$R down                                                  # SIN -v: los volúmenes de root son la vuelta atrás
sudo mv /etc/cron.d/halsa-backups /etc/cron.d/halsa-crl /root/   # que no corran; se borran en el paso 7

# 2. Código, .env, secretos y PKI, de edhunter (runbook 01, paso 2, para ~/halsa).
sudo install -o edhunter -g edhunter -m 0600 /opt/halsa/deploy/.env ~/halsa/deploy/.env
$EDITOR ~/halsa/deploy/.env      # HALSA_SECRETOS, HALSA_PKI_PUBLICADA y HALSA_NPM_DATA: las de .env.ejemplo
install -d -m 0700 ~/.config/halsa ~/.config/halsa/secretos ~/.config/halsa/pki
sudo cp -a /etc/halsa/secretos/. ~/.config/halsa/secretos/
sudo cp -a /etc/halsa/pki/. ~/.config/halsa/pki/          # openssl.cnf se regenera con la ruta nueva en cada uso
sudo chown -R edhunter:edhunter ~/.config/halsa && chmod 0600 ~/.config/halsa/secretos/*

# 3. Las imágenes en uso, al almacén de edhunter.
set -a; source ~/halsa/deploy/.env; set +a
for i in "localhost/halsa-backend:$HALSA_TAG" "localhost/halsa-bff-paciente:$HALSA_TAG" \
         "localhost/halsa-postgres:$HALSA_POSTGRES_TAG"; do
  sudo podman save "$i" | podman load
done

# 4. Los datos, copiados tal cual con el servidor detenido (sin timeline nuevo, la misma stanza). podman unshare
#    escribe dentro del user namespace de edhunter: el UID 999 de postgres y el 1654 de app quedan como los ve el
#    contenedor rootless. ClamAV regenera sus firmas solo.
for v in halsa_postgres-datos halsa_archivos halsa_seq-datos halsa_redis-datos; do
  podman volume create --ignore "$v" >/dev/null
  origen="$(sudo podman volume inspect "$v" --format '{{.Mountpoint}}')"
  destino="$(podman volume inspect "$v" --format '{{.Mountpoint}}')"
  sudo tar -C "$origen" --numeric-owner -cf - . | podman unshare tar -C "$destino" --numeric-owner -xpf -
done

# 5. Las unidades y la versión que corría.
bash ~/halsa/deploy-quadlets/instalar.sh
podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa check     # la stanza de siempre: sin stanza-create
~/halsa/deploy/scripts/desplegar.sh "$HALSA_TAG"
```

6. El CI y los cron: runbook 01, paso 10 (el sudoers pasa a `(edhunter)`, el `command=` a `sudo -u edhunter`, y se
   reinstala `halsa-recibir-despliegue` versión 2), el crontab de `edhunter` (runbooks 04 y 06) y la verificación del
   runbook 01, paso 9. Un `full backup` nuevo (runbook 04).
7. **Unos días después**, si todo sigue bien: `sudo podman volume rm` de los `halsa_*` de root,
   `sudo rm -rf /etc/halsa /opt/halsa /var/lib/halsa /root/halsa-backups /root/halsa-crl` y `sudo podman image rm` de las
   `localhost/halsa-*` de root. Antes de borrar `/etc/halsa`, comprobar que `~/.config/halsa` está completo: la
   custodia (runbook 02) solo tiene la KEK, la llave de sello y la raíz.

**Volver atrás** antes del paso 7: `systemctl --user stop 'halsa-*'`, mover los cron de vuelta a `/etc/cron.d/` y
`$R up -d postgres redis antivirus seq api worker bff`. Lo escrito en la base rootless desde el paso 5 se pierde.

## Transición desde GHCR (una sola vez)

Para un VPS que ya corría con imágenes de GHCR (SPEC 13), antes de la transición a rootless. Hacerlo **antes** del
primer despliegue del CI, para que el primer rollback tenga a dónde volver. Como root:

```bash
cd /opt/halsa/deploy
actual="$(grep -E '^HALSA_TAG=' .env | cut -d= -f2)"
org="$(grep -E '^HALSA_GHCR_ORG=' .env | cut -d= -f2)"

# 1. Las imágenes en ejecución, con el nombre nuevo.
podman tag "ghcr.io/$org/halsa-backend:$actual"      "localhost/halsa-backend:$actual"
podman tag "ghcr.io/$org/halsa-bff-paciente:$actual" "localhost/halsa-bff-paciente:$actual"
podman tag "ghcr.io/$org/halsa-postgres:17"          "localhost/halsa-postgres:17"

# 2. Sin registry.
podman logout ghcr.io
sed -i '/^HALSA_GHCR_ORG=/d' .env

# 3. Sin clon de git: el CI sincroniza deploy/ desde ahora (.env queda donde está).
rm -rf /opt/halsa/.git
```

Después, el runbook 01, paso 10 (usuario `halsa-deploy`, sudoers, llave y environment `produccion`), y el primer
despliegue desde GitHub. Cuando `localhost/halsa-*` esté en uso, las imágenes `ghcr.io/...` se pueden borrar
(`podman image rm`), y los paquetes de GHCR desde GitHub (*Packages*), a mano.

## Migraciones: solo hacia adelante

El rollback vuelve a la **imagen** anterior, no al **esquema** anterior. Por eso toda migración debe ser compatible
con la versión que corre (patrón *expand/contract*):

- **expand** (versión N): agregar columnas como *nullable* o con valor por defecto, tablas nuevas y vistas nuevas;
- **migrate**: la versión N escribe en los dos lados;
- **contract** (versión N+1 o posterior): borrar lo viejo, cuando ninguna versión desplegable lo usa.

El PR que trae una migración dice en su descripción en qué fase está. El revisor lo verifica. Si una migración rompió la
versión anterior y el rollback no levanta, el último recurso es el **restore PITR** a un instante previo al
despliegue (runbook 04), con la pérdida de los datos posteriores que eso implica.

## Rollback fallido

1. `journalctl --user -u halsa-api -u halsa-worker --since -15m --no-pager`.
2. Si es configuración (`ProductionGuard: ...` en el log y exit `1`), corregir `.env` o los secretos y repetir.
3. Si es el esquema: detener `api` y `worker` (NPM responde 502), avisar (runbook 07, "Comunicación") y decidir
   entre corregir hacia adelante (un `vX.Y.Z+1` con el arreglo) o el PITR del runbook 04.

## `ProductionGuard`

En `Production`, la API y el Worker **no arrancan** (exit `1`) si encuentran un atajo de desarrollo. El log nombra
la condición:

| Condición | Corrección |
| --- | --- |
| `Keycloak:RequireHttpsMetadata=false` | quitar el override; las unidades lo fijan en `true` |
| un emisor no es `https` | `HALSA_DOMINIO` mal escrito o vacío |
| `Halsa:Rol` vacío o `Ambos` | cada servicio tiene su rol en su unidad (`halsa-*.container`) |
| `Integraciones:ExigirCertificado=false` | quitar el override |
| `Webhooks:PermitirDestinosLocales=true` | quitar el override |
| `Clinico:KekBase64` tiene valor | la KEK viene solo del secreto `halsa_kek` |
| el stub de afiliación está registrado | build de desarrollo desplegado por error |
| `ConnectionStrings:Database` usa `halsa_owner` o `postgres` | la API usa `halsa_app` (`arrancar-dotnet.sh`) |

## NPM se recreó (actualización, reinicio) y Halsa responde 502

`podman network connect` no sobrevive a la recreación del contenedor de NPM: NPM pierde la red `halsa_borde`. El
monitor externo alerta en 2 minutos. Para recuperarlo:

```bash
~/halsa/deploy/scripts/sincronizar-npm.sh     # vuelve a unirlo con la IP 172.30.0.10; idempotente
```

Para que no vuelva a pasar, la red va fija en el `npm.container` de NPM (deploy-quadlets/README, "NPM en una red
bridge").

Si después de una **actualización** de NPM los proxy hosts de Halsa quedan *Offline* con `duplicate location "/"`,
cambió cómo NPM detecta el `location /` de Advanced: runbook 01, "Actualizar NPM".

## Keycloak compartido

La consola y la operación de `auth.edcastdev.com` son de su dueño. Halsa solo:

- crea sus realms con `deploy/keycloak/importar-realms.sh` (runbook 01, paso 5);
- da de alta los clients `int-*` (runbook 06);
- rota `kc_bff_secret` (runbook 05).

Si el dueño actualiza o recrea Keycloak, hay que verificar que conserva las opciones `--spi-x509cert-lookup--*`.
Sin ellas, las integraciones reciben `invalid_request` ("Client Certification missing for MTLS HoK Token Binding")
al pedir el token. Ver README, "Keycloak → Shared instance".

## Interfaz de Seq (solo por túnel)

Seq no se publica. Para su interfaz, un proxy efímero en el loopback del VPS y un túnel SSH:

```bash
# En el VPS, como edhunter (--network podman: un contenedor en pasta, el predeterminado rootless, no puede unirse a otra red):
podman run --rm -d --name seq-tunel --network podman -p 127.0.0.1:8082:80 alpine/socat tcp-listen:80,fork,reuseaddr tcp-connect:seq:80
podman network connect halsa_app seq-tunel
# En tu equipo:
ssh -N -L 8082:127.0.0.1:8082 <usuario>@<vps>      # → http://localhost:8082
# Al terminar, en el VPS:
podman rm -f seq-tunel
```

> `halsa_app` es interna y Podman no publica puertos desde una red interna: el proxy efímero arranca en la red bridge
> `podman` (que publica solo en el loopback del VPS) y luego se une a `halsa_app`. Existe solo mientras dura la sesión.

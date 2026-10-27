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
4. En el VPS, `halsa-recibir-despliegue` (`deploy/scripts/recibir-despliegue.sh`):
   1. verifica el comando, el tamaño y las sumas; si algo falla, sale con `1` **sin tocar nada**;
   2. `podman load` de las imágenes;
   3. sincroniza `/opt/halsa/deploy` con `rsync --delete`, sin tocar `.env`;
   4. ejecuta `desplegar.sh v1.0.<n>`:
      1. verifica que las imágenes del tag estén cargadas;
      2. `podman compose run --rm migrador`, que sale con `0` o `1`. Si falla, no se toca nada más;
      3. `up -d api worker bff`;
      4. espera hasta 120 s a que `/health` de `api` y `worker` esté en `Healthy`;
      5. sincroniza la configuración de borde con NPM (`sincronizar-npm.sh`, que valida con `nginx -t` antes de recargar);
      6. si no llega a `Healthy`, **vuelve al tag anterior**. Termina con `1` en cualquier caso de fallo;
   5. si todo terminó bien: conserva las 5 versiones más recientes (más la actual y la anterior) y borra la entrega.
5. Revisar Seq: ningún `Error` nuevo desde el despliegue.

Si el job falla, su log tiene la salida completa de los dos scripts. Una entrega fallida queda en
`/var/lib/halsa/entregas/<tag>/` para diagnóstico; se puede borrar a mano.

Dos despliegues nunca corren a la vez: el job tiene su grupo de concurrencia (`despliegue-produccion`) y el script
toma `/var/lock/halsa-despliegue.lock`. Si quedan varios esperando aprobación, basta con aprobar el más reciente y
rechazar los anteriores.

## Rollback manual

`desplegar.sh` vuelve a cualquier tag que siga cargado en el VPS. Como root:

```bash
podman image ls localhost/halsa-backend                 # tags disponibles (los 5 más recientes)
/opt/halsa/deploy/scripts/desplegar.sh v1.0.41
```

Un tag que ya no está cargado falla con `1` sin cambiar nada (`FALLO: falta la imagen ...`). Para volver a él,
relanza en GitHub el run de `main` que lo creó (*Re-run all jobs*): un re-run conserva el `run_number`, así que
construye y envía otra vez el mismo `v1.0.<n>`.

El rollback vuelve a la imagen anterior con el `deploy/` **nuevo** (compose y scripts). Por eso, igual que las
migraciones, los cambios en `deploy/` deben ser compatibles con la versión anterior, y el PR lo declara.

## PostgreSQL: imagen nueva

El CI solo **carga** `localhost/halsa-postgres:17` cuando cambia `deploy/postgres/`; `desplegar.sh` nunca recrea
`postgres`. Para aplicarla, en una ventana de mantenimiento (corta las conexiones y el archivado unos segundos):

```bash
cd /opt/halsa/deploy && C="podman compose -f docker-compose.prod.yml"
podman image ls localhost/halsa-postgres                # 17 y 17-v1.0.<n>
$C up -d postgres
$C exec -u postgres postgres pgbackrest --stanza=halsa check
```

## Transición desde GHCR (una sola vez)

Para un VPS que ya corría con imágenes de GHCR (SPEC 13). Hacerlo **antes** del primer despliegue del CI, para que el
primer rollback tenga a dónde volver. Como root:

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

1. `podman compose -f docker-compose.prod.yml logs --since 15m api worker`.
2. Si es configuración (`ProductionGuard: ...` en el log y exit `1`), corregir `.env` o los secretos y repetir.
3. Si es el esquema: detener `api` y `worker` (NPM responde 502), avisar (runbook 07, "Comunicación") y decidir
   entre corregir hacia adelante (un `vX.Y.Z+1` con el arreglo) o el PITR del runbook 04.

## `ProductionGuard`

En `Production`, la API y el Worker **no arrancan** (exit `1`) si encuentran un atajo de desarrollo. El log nombra
la condición:

| Condición | Corrección |
| --- | --- |
| `Keycloak:RequireHttpsMetadata=false` | quitar el override; el compose lo fija en `true` |
| un emisor no es `https` | `HALSA_DOMINIO` mal escrito o vacío |
| `Halsa:Rol` vacío o `Ambos` | cada servicio tiene su rol en el compose |
| `Integraciones:ExigirCertificado=false` | quitar el override |
| `Webhooks:PermitirDestinosLocales=true` | quitar el override |
| `Clinico:KekBase64` tiene valor | la KEK viene solo del secreto `halsa_kek` |
| el stub de afiliación está registrado | build de desarrollo desplegado por error |
| `ConnectionStrings:Database` usa `halsa_owner` o `postgres` | la API usa `halsa_app` (`arrancar-dotnet.sh`) |

## NPM se recreó (actualización, reinicio) y Halsa responde 502

`podman network connect` no sobrevive a la recreación del contenedor de NPM: NPM pierde la red `halsa_borde`. El
monitor externo alerta en 2 minutos. Para recuperarlo:

```bash
/opt/halsa/deploy/scripts/sincronizar-npm.sh     # vuelve a unirlo con la IP 172.30.0.10; idempotente
```

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
# En el VPS:
podman run --rm -d --name seq-tunel -p 127.0.0.1:8082:80 alpine/socat tcp-listen:80,fork,reuseaddr tcp-connect:seq:80
podman network connect halsa_app seq-tunel
# En tu equipo:
ssh -N -L 8082:127.0.0.1:8082 <usuario>@<vps>      # → http://localhost:8082
# Al terminar, en el VPS:
podman rm -f seq-tunel
```

> `halsa_app` es interna y Podman no publica puertos desde una red interna: el proxy efímero arranca en la red por
> defecto (que publica solo en el loopback del VPS) y luego se une a `halsa_app`. Existe solo mientras dura la sesión.

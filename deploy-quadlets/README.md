# Halsa con Podman Quadlets (rootless)

> SPEC 13 y SPEC 14. Las mismas piezas de `deploy/docker-compose.prod.yml`, pero las administra el **systemd del
> usuario** dueño de NPM (`edhunter` en el VPS actual): cada contenedor, red y volumen es una unidad, arranca con el
> VPS (linger) y se reinicia sola. El compose sigue siendo la referencia: cada `.container` es la traducción 1:1 de su
> servicio, con el mismo `.env`, los mismos secretos y la misma imagen. `deploy/scripts/desplegar.sh` detecta los
> Quadlets (`~/.config/containers/systemd/halsa-api.container`) y usa `systemctl --user`; el CI no cambia.

**Por qué rootless y con ese usuario:** NPM corre rootless como `edhunter`, y las redes de Podman son por usuario.
Solo un Halsa del mismo usuario puede compartir con NPM la red `halsa_borde` (la IP fija 172.30.0.10 de la que
`Red__ProxyConfiable` acepta `X-Forwarded-*` y `X-Client-Cert`). Un Halsa rootful o de otro usuario no es alcanzable
por NPM sin publicar puertos, y la SPEC 13 prohíbe publicarlos.

## Rutas

Todo vive en el home del usuario; las unidades lo nombran con `%h`, así que no dependen de su nombre.

| Ruta                                                | Qué                                                          | Permisos          |
| --------------------------------------------------- | ------------------------------------------------------------ | ----------------- |
| `~/halsa/deploy/`                                   | `deploy/` del repo; lo sincroniza el CI con `rsync --delete` | del usuario       |
| `~/halsa/deploy/.env`                               | el `.env` de producción (nunca lo toca el CI)                | 0600              |
| `~/halsa/deploy-quadlets/`                          | esta carpeta (el CI no la envía)                             | del usuario       |
| `~/.config/halsa/secretos/`                         | un archivo por secreto (runbook 02)                          | 0700 / 0600       |
| `~/.config/halsa/pki/`                              | la intermedia, `publicar/` y `clientes/` (runbook 06)        | 0700; llaves 0600 |
| `~/.config/containers/systemd/halsa-*`              | las unidades instaladas                                      | 0644              |
| `~/.local/share/containers/storage/volumes/halsa_*` | los volúmenes (datos de postgres, adjuntos, …)               | de Podman         |
| `~/.local/state/halsa/entregas/`                    | la zona de recepción de `recibir-despliegue.sh`              | 0700              |

En rootless, el root del contenedor **es** el usuario: los secretos deben ser suyos con 0600 (con `root:root` el
contenedor no podría leerlos). **Excluye `~/.config/halsa/` de cualquier backup o sincronización del home** (runbook
02): ahí están la KEK y la llave de la intermedia. Los respaldos de la base van por pgBackRest, no por el home.

Los contenedores se llaman `halsa-<servicio>` (Quadlet arranca con `--replace`: con `postgres` a secas habría
reemplazado el postgres que ya corre en el VPS). Dentro de las redes de Halsa conservan su nombre corto con
`NetworkAlias=` (`postgres`, `redis`, `api`, …), que es el que usan las cadenas de conexión y NPM.
`NetworkAlias=` vale en **todas** las redes del contenedor. Por eso postgres lleva su alias solo en `datos`
(`Network=halsa-datos.network:alias=postgres`): el worker también está en `egreso`, y con el alias ahí a veces
resolvía `postgres` a una IP 172.30.3.x, que `pg_hba.conf` rechaza.

## Archivos

| Archivo                                                                | Del compose                        | Notas                                                                              |
| ---------------------------------------------------------------------- | ---------------------------------- | ---------------------------------------------------------------------------------- |
| `halsa-borde.network`                                                  | `borde`                            | interna, 172.30.0.0/24; automáticas solo en .128/25 (NPM usa la .10)               |
| `halsa-app.network`, `halsa-datos.network`                             | `app`, `datos`                     | internas, 172.30.1.0/24 y 172.30.2.0/24 (la que admite `pg_hba.conf`)              |
| `halsa-egreso.network`                                                 | `egreso`                           | con salida a internet, 172.30.3.0/24                                               |
| `halsa-*.volume` (5)                                                   | `volumes:`                         | mismos nombres que el compose (`halsa_postgres-datos`, …)                          |
| `halsa-postgres.container`, `halsa-redis.container`                    | `postgres`, `redis`                | `Notify=healthy` (el `service_healthy` del compose)                                |
| `halsa-antivirus.container`, `halsa-seq.container`                     | `antivirus`, `seq`                 |                                                                                    |
| `halsa-api.container`, `halsa-worker.container`, `halsa-bff.container` | `api`, `worker`, `bff`             | imagen `:${HALSA_TAG}`                                                             |
| `halsa-migrador.container`                                             | `migrador` (perfil `migracion`)    | `Type=oneshot`, sin `[Install]`: nunca corre al arrancar                           |
| `instalar.sh`                                                          |                                    | verifica requisitos, valida con el generador, instala y arranca la infraestructura |
| `migrar.sh`                                                            | `podman compose run --rm migrador` | migraciones a mano                                                                 |

### Cómo se tradujo el compose

| Compose                          | Quadlet                                                                                                                                                                              |
| -------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------ |
| `${VAR}` del `.env`              | `[Service] EnvironmentFile=%h/halsa/deploy/.env`: systemd expande `${VAR}` en `Image=`, `Environment=` y `AddHost=` al arrancar. **Sin defaults**: `instalar.sh` exige las variables |
| `entrypoint` + `command`         | `Entrypoint=/bin/sh` + `Exec=`: la imagen .NET tiene `ENTRYPOINT ["dotnet", ...]` y `Exec=` solo le agregaría argumentos                                                             |
| `secrets:`                       | `Volume=%h/.config/halsa/secretos/<s>:/run/secrets/<s>:ro`                                                                                                                           |
| `extra_hosts` (Keycloak por NPM) | `AddHost=${HALSA_KEYCLOAK_HOST}:${HALSA_NPM_IP}`                                                                                                                                     |
| `depends_on: service_healthy`    | `Notify=healthy` en postgres/redis + `After=`/`Requires=`                                                                                                                            |
| `restart: unless-stopped`        | `Restart=always` (systemd no conoce `unless-stopped`; `systemctl stop` no reinicia)                                                                                                  |
| `logging: json-file`             | `LogDriver=journald`: `journalctl --user -u`                                                                                                                                         |
| `$$` en `command`                | igual (`$$` → `$`), y además `%%` para un `%` literal: systemd expande `%s` (el shell del usuario)                                                                                   |

## Requisitos (una vez, con sudo)

```bash
sudo loginctl enable-linger edhunter       # sin esto, NADA del usuario arranca tras un reinicio (tampoco NPM)
sudo apt install -y podman-docker gettext-base openssl rsync
podman --version                           # ≥ 5.0
grep edhunter /etc/subuid                  # un rango de 65536 (arrancar-dotnet.sh baja al UID 1654 del contenedor)
```

### NPM en una red bridge

Un contenedor en modo `pasta` (el que Podman rootless usa por defecto) **no puede unirse** a otra red:
`podman network connect` responde `"pasta" is not supported`, y `sincronizar-npm.sh` se detiene con ese aviso.
Comprueba el modo de NPM:

```bash
podman inspect npm -f '{{.HostConfig.NetworkMode}}'      # "bridge" sirve; "pasta" no
cat ~/.config/containers/systemd/npm.container
```

Si es `pasta`, agrega a `npm.container` una red bridge (por ejemplo `Network=podman`) y reinícialo
(`systemctl --user daemon-reload && systemctl --user restart npm`). Toca un servicio que no es de Halsa: hazlo en una
ventana de mantenimiento, porque todos los sitios de NPM se cortan unos segundos. **Después verifica que NPM siga
viendo la IP real de los clientes** (con la red bridge, Podman rootless publica los puertos por otro mecanismo y la
IP de origen puede pasar a ser la interna): mira la columna de IP en
`podman exec npm tail -5 /data/logs/proxy-host-1_access.log` tras visitar uno de los sitios desde tu equipo. Si ya no
es la tuya, el rate limit por IP de Halsa y las IP de auditoría dejan de ser fiables: hay que resolverlo antes de
publicar Halsa.

`sincronizar-npm.sh` une NPM a `halsa_borde` en caliente (no persiste si NPM se recrea; el script lo repite en cada
despliegue). Para dejarlo fijo, en `npm.container`: `Network=halsa-borde.network:ip=172.30.0.10`.

## Instalación

```bash
# En tu máquina, del mismo commit que se va a desplegar:
git archive main deploy/ deploy-quadlets/ | ssh edhunter@<vps> 'mkdir -p ~/halsa && tar -xf - -C ~/halsa'
# git archive main deploy/ deploy-quadlets/ | ssh alcast1 'rm -rf ~/halsa && mkdir -p ~/halsa && tar -xf - -C ~/halsa'

rsync -avz --delete --exclude='.env' deploy/ deploy-quadlets/ alcast1:~/halsa/


# En el VPS, como edhunter (sin sudo):
cd ~/halsa/deploy && cp .env.ejemplo .env && chmod 600 .env && $EDITOR .env
install -d -m 0700 ~/.config/halsa ~/.config/halsa/secretos ~/.config/halsa/pki
# Secretos: runbook 02, en ~/.config/halsa/secretos (archivos del usuario, 0600).
# PKI: runbook 06 con HALSA_PKI_DIR=~/.config/halsa/pki.
bash ~/halsa/deploy-quadlets/instalar.sh
```

En el `.env`, además de lo del runbook 01:

```bash
HALSA_SECRETOS=/home/edhunter/.config/halsa/secretos
HALSA_PKI_PUBLICADA=/home/edhunter/.config/halsa/pki/publicar
HALSA_NPM_CONTENEDOR=npm
# podman volume inspect npm_data --format '{{.Mountpoint}}' (en su propia línea: systemd no corta comentarios al final)
HALSA_NPM_DATA=/home/edhunter/.local/share/containers/storage/volumes/npm_data/_data
```

Son los valores de `deploy/.env.ejemplo`. Si el VPS corría Halsa con el compose como root, la migración de datos,
secretos e imágenes es el runbook 03, "Transición a rootless".

`instalar.sh`:

1. **verifica** que no corra como root, linger, Podman ≥ 5, que `docker` sea Podman, el `.env` con todas las
   variables que el compose exige y las rutas de arriba, cada secreto que montan las unidades (del usuario, 0600), la
   PKI pública, que el compose de Halsa no esté corriendo (ni el de este usuario ni el de root) y que **ningún
   contenedor ajeno** se llame como uno de Halsa;
2. **valida** las unidades con el generador de Quadlet (`--user --dryrun`): si alguna no es válida, no copia nada;
3. las copia a `~/.config/containers/systemd/`, ejecuta `systemctl --user daemon-reload` y descarga las imágenes
   públicas (la primera descarga de ClamAV no cabe en el timeout de arranque);
4. arranca postgres (espera a _healthy_), redis, antivirus y seq.

Las unidades **no** se habilitan con `systemctl --user enable`: son generadas y systemd lo rechaza. Su `[Install]`
(`WantedBy=default.target`) lo aplica el generador en cada `daemon-reload`. api, worker y bff los arranca
`desplegar.sh`.

**Primera vez:**

```bash
podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa stanza-create    # runbook 04
~/halsa/deploy/scripts/desplegar.sh v1.0.<n>      # si la imagen ya está cargada; si no, el job desplegar del CI
```

**Cuando cambie un archivo de esta carpeta:** copiarla de nuevo y ejecutar `instalar.sh`. No reinicia lo que ya
corre: si cambió la unidad de un servicio activo, `systemctl --user restart <servicio>` (postgres y redis reinician
también api, worker y bff, por `Requires=`).

## Despliegue desde el CI (runbook 01, paso 10)

El usuario `halsa-deploy` y su llave forzada siguen igual; lo que cambia es **a quién** llama `sudo`. El script se
ejecuta como `edhunter`, que carga las imágenes en **su** almacén de Podman (como root irían a otro almacén, que los
Quadlets no ven) y no necesita root para nada:

```bash
sudo install -o root -g root -m 0755 ~/halsa/deploy/scripts/recibir-despliegue.sh /usr/local/sbin/halsa-recibir-despliegue
cat > /tmp/halsa-deploy <<'EOF'
Defaults!/usr/local/sbin/halsa-recibir-despliegue env_keep += "SSH_ORIGINAL_COMMAND"
halsa-deploy ALL=(edhunter) NOPASSWD: /usr/local/sbin/halsa-recibir-despliegue
EOF
sudo visudo -cf /tmp/halsa-deploy && sudo install -o root -g root -m 0440 /tmp/halsa-deploy /etc/sudoers.d/halsa-deploy
```

En `authorized_keys` de `halsa-deploy`: `command="sudo -u edhunter /usr/local/sbin/halsa-recibir-despliegue",no-pty,…`

Como `edhunter`, `recibir-despliegue.sh` (versión 2) usa `~/halsa/deploy`, `~/.local/state/halsa/entregas` y un lock
en `/run/user/<uid>`, y llama a `desplegar.sh`, que con los Quadlets:

1. verifica que estén `localhost/halsa-backend:<tag>` y `localhost/halsa-bff-paciente:<tag>`;
2. fija `HALSA_TAG=<tag>` en `.env` (las unidades lo leen al arrancar);
3. `systemctl --user start halsa-migrador` (sale con el código del Migrador; si falla, vuelve el tag);
4. `systemctl --user restart halsa-api halsa-worker halsa-bff`;
5. espera hasta 120 s a que `/health` de api y worker esté en `Healthy` y sincroniza NPM;
6. si no llega, vuelve `HALSA_TAG` al anterior y reinicia (las migraciones quedan aplicadas: runbook 03).

A mano: `~/halsa/deploy/scripts/desplegar.sh v1.0.41`. Solo las migraciones: `bash ~/halsa/deploy-quadlets/migrar.sh`.

## Operación

```bash
systemctl --user status 'halsa-*'
podman ps --format '{{.Names}} {{.Status}}' | grep halsa-
journalctl --user -u halsa-api -f
journalctl --user -u halsa-migrador -n 100 --no-pager
systemctl --user restart halsa-worker
```

Desde una sesión con `sudo -u edhunter` o desde cron, exporta antes `XDG_RUNTIME_DIR=/run/user/$(id -u edhunter)`.

### Equivalencias con el compose

Los runbooks 01–07 ya usan los Quadlets como `edhunter`. Para quien venga del compose como root:

| Runbook | Compose                                          | Quadlets                                                                   |
| ------- | ------------------------------------------------ | -------------------------------------------------------------------------- |
| 03, 07  | `$C ps`                                          | `systemctl --user status 'halsa-*'`                                        |
| 03, 07  | `$C logs --since 30m api`                        | `journalctl --user -u halsa-api --since -30m`                              |
| 07      | `$C stop antivirus` / `start` / `restart worker` | `systemctl --user stop halsa-antivirus` / `start` / `restart halsa-worker` |
| 04      | `$C exec -T -u postgres postgres pgbackrest ...` | `podman exec -u postgres halsa-postgres pgbackrest ...`                    |
| 04, 05  | `$C exec -T -u postgres postgres psql ...`       | `podman exec -u postgres halsa-postgres psql ...`                          |
| 05      | `$C up -d --force-recreate api worker`           | `systemctl --user restart halsa-api halsa-worker`                          |
| 02, 05  | `/etc/halsa/secretos`                            | `~/.config/halsa/secretos`                                                 |
| 06      | `HALSA_PKI_DIR=/etc/halsa/pki`                   | `HALSA_PKI_DIR=~/.config/halsa/pki`                                        |

**Los cron pasan al crontab de `edhunter`** (`crontab -e`, sin la columna de usuario; runbooks 04 y 06). Un cron de
root con `podman compose exec` no ve los contenedores rootless, y los backups fallarían en silencio.
`XDG_RUNTIME_DIR` es `/run/user/` + `id -u edhunter`:

```cron
XDG_RUNTIME_DIR=/run/user/1001
CRON_TZ=America/Bogota
# Backups (runbook 04)
0 1 * * 0    podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa --type=full backup >> ~/halsa/backups.log 2>&1
0 1 * * 1-6  podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa --type=diff backup >> ~/halsa/backups.log 2>&1
0 3 1-7 * 1  ~/halsa/deploy/scripts/restaurar-prueba.sh >> ~/halsa/restaurar-prueba.log 2>&1
# CRL de la intermedia y su validación (runbook 06): los tres pasos al log
0 4 * * 1    { HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/generar-crl.sh && bash ~/halsa/deploy/scripts/sincronizar-npm.sh && HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/verificar-certificados.sh; } >> ~/halsa/crl.log 2>&1
```

**Restauración real** (runbook 04, paso 3), con los mismos secretos, entorno y red que `halsa-postgres.container`:

```bash
systemctl --user stop halsa-api halsa-worker halsa-bff halsa-postgres
set -a; source ~/halsa/deploy/.env; set +a
V=(); for s in pg_postgres_password pg_owner_password pg_app_password pg_clinico_password pg_ingesta_password \
               pg_worker_password backups_s3_key backups_s3_secret backups_cipher_pass; do
  V+=(-v "$HALSA_SECRETOS/$s:/run/secrets/$s:ro"); done
podman run --rm --network halsa_egreso "${V[@]}" -e HALSA_BACKUPS_BUCKET -e HALSA_BACKUPS_REGION -e HALSA_BACKUPS_ENDPOINT \
  -v halsa_postgres-datos:/var/lib/postgresql/data --entrypoint bash "localhost/halsa-postgres:$HALSA_POSTGRES_TAG" -c '
    /usr/local/bin/halsa-arrancar.sh true &&
    su postgres -c "pgbackrest --stanza=halsa --delta --type=time \"--target=2026-10-14 15:30:00+00\" --target-action=promote restore"'
systemctl --user start halsa-postgres          # verificar con SQL; luego desplegar.sh con el tag correcto
```

## Validación de los certificados

Halsa depende de cinco grupos de certificados. Si uno vence o no coincide, el corte es silencioso hasta que alguien
llama: una CRL vencida, por ejemplo, hace que NPM rechace **todas** las integraciones. Esta validación se corre:

- al instalar o renovar la intermedia, y al emitir, renovar o revocar un certificado cliente (runbook 06);
- después de `instalar.sh`;
- cada semana, junto con el cron de la CRL (arriba);
- en el equipo sin red, antes y después de la ceremonia anual de la raíz.

| Qué                                            | Dónde                                                       | Cómo se valida                 |
| ---------------------------------------------- | ----------------------------------------------------------- | ------------------------------ |
| Raíz, intermedia, CRL y certificados cliente   | `~/.config/halsa/pki` (VPS) y la bóveda (equipo sin red)    | 1. `verificar-certificados.sh` |
| Lo publicado en NPM y lo que lee el worker     | `$HALSA_NPM_DATA/halsa-pki`, `~/.config/halsa/pki/publicar` | 2.                             |
| mTLS de `halsa-integraciones` de punta a punta | NPM → API / Keycloak                                        | 3.                             |
| TLS público (Let's Encrypt, NPM)               | `halsa-api`, `halsa-integraciones`, `halsa-pacientes`       | 4.                             |
| TLS de PostgreSQL                              | volumen `halsa_postgres-datos`                              | 5.                             |

### 1. La PKI: `verificar-certificados.sh`

Solo lee: no firma ni regenera nada.

```bash
# VPS (intermedia, CRL, publicar/ y clientes/):
HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/verificar-certificados.sh

# Equipo sin red (la raíz), y de paso la intermedia recién firmada que va en la memoria USB:
HALSA_PKI_DIR=/media/boveda/halsa-pki bash deploy/pki/verificar-certificados.sh /media/usb/intermedia.crt
```

| Revisa           | FALLO si                                                                                                             | AVISO si                                       |
| ---------------- | -------------------------------------------------------------------------------------------------------------------- | ---------------------------------------------- |
| `raiz.crt`       | no es una CA autofirmada, o venció                                                                                   | vence en < 365 días (nueva CA, runbook 05)     |
| `raiz.crl`       | no la firmó la raíz, o venció                                                                                        | vence en < 30 días (ceremonia anual)           |
| `raiz.key`       | no está cifrada, o está junto a `intermedia.key` (la raíz en el VPS)                                                 |                                                |
| `intermedia.crt` | no verifica contra la raíz, la raíz la revocó, no tiene `pathlen:0`, o venció                                        | vence en < 90 días (reemitir 3 meses antes)    |
| `intermedia.key` | no corresponde al certificado, o no es 0600                                                                          |                                                |
| `intermedia.crl` | no la firmó la intermedia, o venció                                                                                  | vence en < 48 h (la alerta `CrlPorVencer`)     |
| `publicar/`      | la cadena o la CRL no son exactamente intermedia + raíz (lo que monta NPM)                                           |                                                |
| `clientes/*.crt` | no verifica con la cadena y las dos CRL (`-crl_check_all`, como Nginx), sin `clientAuth`, CN ≠ `client_id`, o venció | vence en < 30 días; su `.key` sigue en el VPS  |
| todo PEM         |                                                                                                                      | empieza con un BOM UTF-8 (lo guardó un editor) |

Un cliente **revocado** sale como `OK ... revocado`: es lo esperado después de `revocar.sh`. Termina con
`Resultado: N fallo(s), M aviso(s)` y exit `1` si hay algún fallo.

### 2. Lo publicado en NPM y lo que lee el worker

```bash
set -a; source ~/halsa/deploy/.env; set +a

# NPM tiene exactamente lo publicado (sincronizar-npm.sh lo copia):
diff -r "$HALSA_PKI_PUBLICADA" "$HALSA_NPM_DATA/halsa-pki" && echo "OK: NPM al día"

# El worker ve la CRL (Observabilidad__CrlPath) y su próxima actualización:
podman exec halsa-worker sh -c 'ls -l /etc/halsa/pki/'
openssl crl -in "$HALSA_PKI_PUBLICADA/halsa-integraciones.crl" -noout -nextupdate

# Y no hay alertas de vigencia en sus logs:
journalctl --user -u halsa-worker --since -2h | grep -E 'CrlPorVencer|TlsPorVencer' || echo "OK: sin alertas"
```

### 3. mTLS de `halsa-integraciones` de punta a punta

```bash
H="$HALSA_HOST_INTEGRACIONES"

# NPM pide certificados de la CA de Halsa (la intermedia y la raíz):
openssl s_client -connect "$H:443" -servername "$H" </dev/null 2>/dev/null \
  | sed -n '/Acceptable client certificate CA names/,/^Requested/p'

# Sin certificado: 400 de NPM, nada llega a la API.
curl -s -o /dev/null -w '%{http_code}\n' "https://$H/v1/integraciones/"          # 400
```

Con un certificado de prueba, que se revoca al terminar:

```bash
export HALSA_PKI_DIR=~/.config/halsa/pki; P=~/halsa/deploy/pki; C=$HALSA_PKI_DIR/clientes/int-arl-VERIFICACION
bash $P/emitir-cliente.sh int-arl-VERIFICACION
curl -s -o /dev/null -w '%{http_code}\n' --cert $C.crt --key $C.key "https://$H/v1/integraciones/"
#   ≠ 400 (401 o 404): el mTLS pasó y respondió la API
bash $P/revocar.sh int-arl-VERIFICACION cessationOfOperation && bash ~/halsa/deploy/scripts/sincronizar-npm.sh
curl -s -o /dev/null -w '%{http_code}\n' --cert $C.crt --key $C.key "https://$H/v1/integraciones/"
#   400: NPM ya aplica la CRL nueva
rm -f $C.key
```

La vinculación token ↔ certificado (`cnf.x5t#S256`, `401 Auth.CertificadoNoVinculado` con otro certificado) se
prueba con una integración real al darla de alta (runbook 06, "Lo que se le entrega a la integración").

### 4. TLS público (Let's Encrypt, lo renueva NPM)

```bash
for h in "$HALSA_HOST_API" "$HALSA_HOST_INTEGRACIONES" "$HALSA_HOST_PACIENTES"; do
  printf '%-40s ' "$h"
  openssl s_client -connect "$h:443" -servername "$h" </dev/null 2>/dev/null \
    | openssl x509 -noout -enddate -checkend $(( 14 * 86400 )) | tr '\n' ' '; echo
done
# "Certificate will not expire": quedan más de 14 días (el umbral de TlsPorVencer). Si no, runbook 05.
```

### 5. TLS de PostgreSQL

Autofirmado, 10 años, generado por `deploy/postgres/arrancar.sh` en el volumen. La aplicación usa
`SSL Mode=Require` y `pg_hba.conf` solo admite `hostssl` desde la red `datos`:

```bash
podman exec halsa-postgres openssl x509 -in /var/lib/postgresql/tls/servidor.crt -noout -subject -enddate
podman exec -u postgres halsa-postgres psql -d halsa -tAc \
  "SELECT a.usename, s.ssl, s.version FROM pg_stat_ssl s JOIN pg_stat_activity a USING (pid) WHERE a.usename LIKE 'halsa_%'"
#   cada conexión de la aplicación: t | TLSv1.3
```


# Runbook 01 — Aprovisionar el VPS

> SPEC 13 y SPEC 14. Lleva Halsa a producción en el VPS que ya tiene **Nginx Proxy Manager (NPM)** y el **Keycloak compartido**
> de `auth.edcastdev.com`. Junto con el runbook 04 es también el procedimiento para **reconstruir** Halsa si se
> pierde el VPS. NPM y Keycloak tienen sus propios dueños y procedimientos: aquí solo se hace lo que Halsa necesita de
> ellos.

## Requisitos

| | |
| --- | --- |
| VPS | El actual, con Podman ≥ 5.0. Para Halsa: 4 vCPU y 8 GB de RAM libres (los valores de `deploy/postgres/postgresql.conf` asumen 8 GB), y 100 GB de disco |
| Arquitectura | **arm64** (`uname -m` → `aarch64`). El CI construye las imágenes de Halsa para `linux/arm64` (`PLATAFORMA` en `ci.yml`), y las públicas (redis, `clamav/clamav-debian`, seq) son multiarquitectura. Si cambia de VPS a amd64, cambia `PLATAFORMA` |
| Usuario | **`edhunter`**, el dueño de NPM. Halsa corre **rootless** con sus Quadlets (`deploy-quadlets/`): las redes de Podman son por usuario, y solo así NPM alcanza `halsa_borde` sin publicar puertos (deploy-quadlets/README) |
| NPM | Rootless como `edhunter`, en una red **bridge** (no `pasta`). Versión verificada: **2.16** (ver "Actualizar NPM") |
| Keycloak | `keykloack-keycloak-1` (26.7), publicado por NPM en `auth.edcastdev.com` |
| DNS | `halsa-api`, `halsa-integraciones` y `halsa-pacientes` `.edcastdev.com` → IP pública del VPS |
| Fuera del VPS | bucket S3 de backups (runbook 04), bucket WORM (SPEC 11), monitor externo y heartbeat (runbook 07) |
| Custodia | la KEK, la llave de sello y la raíz de la CA, en sus ubicaciones fuera de línea (runbook 02) |

Salvo donde dice `sudo`, todo se ejecuta **como `edhunter`**. Desde `sudo -u edhunter` o desde cron, exporta antes
`XDG_RUNTIME_DIR=/run/user/$(id -u edhunter)`.

## 1. Sistema base (con sudo, lo que falte)

```bash
sudo apt install -y podman-docker gettext-base openssl rsync
    # podman-docker: los scripts de deploy/ llaman a "docker"; envsubst: sincronizar-npm.sh e importar-realms.sh;
    # rsync: recibir-despliegue.sh (SPEC 14)
sudo loginctl enable-linger edhunter     # sin esto, nada del usuario arranca tras un reinicio (tampoco NPM)
podman --version                         # ≥ 5.0 (Entrypoint= y Notify=healthy en las unidades)
grep edhunter /etc/subuid /etc/subgid    # un rango de 65536: arrancar-dotnet.sh baja al UID 1654 del contenedor

# Sin core dumps en el host (los contenedores ya llevan Ulimit=core=0).
echo '* hard core 0' | sudo tee /etc/security/limits.d/90-halsa-nocore.conf
echo 'kernel.core_pattern=|/bin/false' | sudo tee /etc/sysctl.d/90-halsa-nocore.conf
sudo sysctl --system
```

No hay registry ni `podman login` (SPEC 14): el CI construye las imágenes y las carga por SSH en el almacén de Podman
de `edhunter`.

**NPM en una red bridge.** Un contenedor en modo `pasta` (el predeterminado en rootless) no puede unirse a
`halsa_borde`. Compruébalo con `podman inspect npm -f '{{.HostConfig.NetworkMode}}'`. Si es `pasta`, sigue
deploy-quadlets/README, "NPM en una red bridge", **incluida la verificación de la IP real de los clientes**.

> **Puertos:** Halsa no publica ninguno (lo verifica el paso 9). El 80 y el 443 son de NPM.

## 2. Código de despliegue

En el VPS viven `~/halsa/deploy/` y `~/halsa/deploy-quadlets/`. No hay código fuente, builds ni clon de git: cada
despliegue del CI sincroniza `deploy/` con `rsync --delete` (paso 10) y nunca toca `.env`. El CI no envía
`deploy-quadlets/`: se copia a mano cuando cambia (deploy-quadlets/README).

La **primera vez**, los copias desde una máquina con el repo, del mismo commit que se va a desplegar:

```bash
# En tu máquina:
git archive main deploy/ deploy-quadlets/ | ssh edhunter@<vps> 'mkdir -p ~/halsa && tar -xf - -C ~/halsa'

# En el VPS:
cd ~/halsa/deploy
cp .env.ejemplo .env && chmod 600 .env
$EDITOR .env
```

En `.env`, además de hosts, buckets, heartbeat y hash de Seq (`HALSA_SECRETOS` y `HALSA_PKI_PUBLICADA` ya traen las
rutas de `edhunter`, y `instalar.sh` exige exactamente esas):

```bash
podman ps --format '{{.Names}}' | grep -i -E 'npm|proxy'        # → HALSA_NPM_CONTENEDOR
podman inspect <npm> -f '{{range .Mounts}}{{.Source}} → {{.Destination}}{{println}}{{end}}' | grep ' /data$'
                                                                  # → HALSA_NPM_DATA (la carpeta del host)
```

`HALSA_UPSTREAM_KEYCLOAK` es **el mismo** *forward* que usa hoy el proxy host `auth.edcastdev.com` en NPM, p. ej.
`http://keykloack-keycloak-1:8080`. No pongas comentarios al final de una línea del `.env`: las unidades lo leen con
`EnvironmentFile=`, y systemd dejaría el comentario en el valor.

## 3. Secretos

Sigue el **runbook 02**: crea `~/.config/halsa/secretos/` (0700, con archivos 0600 de `edhunter`), restaura la KEK y
la llave de sello desde su custodia y genera las contraseñas. Exclúyelo de los snapshots y de cualquier copia del home.

## 4. PKI de integraciones

Sigue el **runbook 06**, "Instalar la intermedia en el VPS". Al final existen `~/.config/halsa/pki/publicar/`
(`halsa-integraciones-ca.pem` y `halsa-integraciones.crl`) y el cron semanal de la CRL.

## 5. Keycloak compartido

1. **Cambios del dueño** (README, sección "Keycloak → Shared instance"):
   - las tres opciones `--spi-x509cert-lookup--*`;
   - `KC_HOSTNAME=https://auth.edcastdev.com`;
   - el bloque `location /` que borra `ssl-client-cert` y `ssl-client-verify` en el Advanced del proxy host
     `auth.edcastdev.com`.

   **Sin el bloque no se da de alta ninguna integración** (lo verifica el paso 9).
2. Crear los realms:
   ```bash
   ~/halsa/deploy/keycloak/importar-realms.sh     # pide un admin de master; idempotente
   ```
3. **SMTP** de `halsa-pacientes`: consola de Keycloak → realm `halsa-pacientes` → *Realm settings → Email*. Hasta
   entonces, el autorregistro de pacientes no se completa (`verifyEmail=true`). No abrir el portal sin esto.

## 6. Primer arranque de Halsa

Si el VPS ya corría Halsa con el compose como root, antes sigue el runbook 03, "Transición a rootless".

`instalar.sh` necesita `localhost/halsa-postgres` en el almacén de `edhunter`. No hay registry: esa imagen (y las de
api y bff) solo las construye el CI y las envía por SSH (paso 10). Un push normal **no** la incluye, porque el CI
solo construye `halsa-postgres` cuando cambia `deploy/postgres/`. Por eso la primera vez se lanza el workflow a mano:

1. Configura el paso 10 (usuario `halsa-deploy`, sudoers y secretos del environment `produccion`).
2. GitHub → *Actions* → **CI** → *Run workflow* → rama `main`, marca **`incluir_postgres`** → *Run workflow*.
3. Cuando el job **Desplegar en producción** pida aprobación: *Review deployments* → `produccion` → *Approve*.
4. El job construye las tres imágenes para `linux/arm64` y las envía. `recibir-despliegue.sh` las carga en el almacén
   de `edhunter` y sincroniza `~/halsa/deploy`. El job termina **en rojo** con
   `FALLO: … faltan los Quadlets de Halsa`. Es lo esperado: `desplegar.sh` no arranca nada sin las unidades.
5. Comprueba en el VPS que llegaron y que son arm64:
   ```bash
   podman image ls localhost/halsa-*        # halsa-postgres:17 y 17-v1.0.<n>, halsa-backend:v1.0.<n>, halsa-bff-paciente:v1.0.<n>
   podman image inspect --format '{{.Architecture}}' localhost/halsa-postgres:17    # arm64
   ```

Si `instalar.sh` dice `… es amd64 y este VPS es arm64`, son imágenes de un CI anterior al cambio de arquitectura.
Repite los pasos 2–4: `podman load` reemplaza el tag `halsa-postgres:17`, y api/bff llegan con el tag del run nuevo.
Las capas viejas quedan sin tag; bórralas con `podman image prune`.

```bash
bash ~/halsa/deploy-quadlets/instalar.sh
# PostgreSQL crea los roles la primera vez (initdb/01-roles.sh). Si se reconstruye: runbook 04, "VPS perdido".
podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa stanza-create
~/halsa/deploy/scripts/desplegar.sh v1.0.<n>     # el tag de ese run: podman image ls localhost/halsa-backend
```

`instalar.sh` verifica los requisitos (linger, Podman, el `.env`, los secretos, la PKI y que no corra otro Halsa),
valida las unidades con el generador de Quadlet, las instala en `~/.config/containers/systemd/` y arranca postgres,
redis, antivirus y seq.

Desde entonces, cada despliegue del CI: `recibir-despliegue.sh` carga las imágenes y llama a
`./scripts/desplegar.sh v1.0.<n>`, que migra (`halsa-migrador`), reinicia api/worker/bff, verifica `/health` y
sincroniza NPM.

`desplegar.sh` termina llamando a `scripts/sincronizar-npm.sh`. Ese script:
- une NPM a `halsa_borde` con la IP 172.30.0.10;
- copia `deploy/npm` a `<HALSA_NPM_DATA>/nginx/custom/halsa/` y la PKI pública a `<HALSA_NPM_DATA>/halsa-pki/`;
- agrega a `http_top.conf` la línea que incluye las zonas de Halsa;
- valida con `nginx -t` dentro de NPM y lo recarga.

Si `nginx -t` falla, restaura lo anterior: los otros sitios de NPM no se tocan.

## 7. Proxy hosts en NPM (una vez)

En la interfaz de NPM, **Add Proxy Host**, uno por host:

| Campo | `halsa-api` | `halsa-integraciones` | `halsa-pacientes` |
| --- | --- | --- | --- |
| Domain Names | `halsa-api.edcastdev.com` | `halsa-integraciones.edcastdev.com` | `halsa-pacientes.edcastdev.com` |
| Scheme / Forward | `http` / `api` / `8080` | `http` / `api` / `8080` | `http` / `bff` / `8080` |
| Cache Assets, Block Common Exploits, Websockets | **off** | **off** | **off** |
| SSL | *Request a new SSL Certificate* (Let's Encrypt), **Force SSL** y **HTTP/2** on, **HSTS off** | igual | igual |
| Advanced | ver abajo | ver abajo | ver abajo |

- **Forward:** la interfaz lo exige, pero el `include` lo reemplaza: Halsa define todas las `location`.
- **HSTS en off:** lo pone Halsa (`snippets/cabeceras.conf`). Activarlo en NPM lo duplica.

El **Advanced** de cada uno es exactamente esto, cambiando `<sitio>` por `api`, `integraciones` o `pacientes`:

```nginx
include /data/nginx/custom/halsa/sitios/<sitio>.conf;
#; location / { ... } lo define el include de Halsa: esta linea evita el location / de NPM
```

**La segunda línea es funcional.** NPM solo omite su `location /` si una línea coincide con
`^(?:.*;)?\s*location\s*/\s*{`. Sin ella, NPM genera un `location /` duplicado y marca el host *offline*.

Al guardar, NPM valida y el host debe quedar **Online**. Si queda *Offline*, el error está en *⋮ → Edit → Advanced*
(`nginx_err`).

## 8. Monitores, alertas y backups

1. **Seq** (runbook 07): SMTP, las señales y las alertas.
2. **Backups** (runbook 04): el crontab de `edhunter` y el primer backup completo.
3. **Monitores externos** (runbook 07).

## 9. Verificación (criterios *staging* de la SPEC 13)

```bash
# Halsa corre como edhunter: todas las unidades activas, salvo halsa-migrador (oneshot).
systemctl --user list-units 'halsa-*' --all --no-pager

# Halsa no publica puertos.
podman ps --filter name='^halsa-' --format '{{.Names}} {{.Ports}}' | grep -- '->' && echo "FALLO: un servicio de Halsa publica puertos"

# Egreso: api no sale a internet; worker sí.
podman exec halsa-api curl -sS -m 5 https://example.com >/dev/null && echo "FALLO: api tiene egreso" || echo "OK: api sin egreso"
podman exec halsa-worker curl -sS -m 5 -o /dev/null https://example.com && echo "OK: worker con egreso"

# Sin core dumps.
podman exec halsa-api sh -c 'ulimit -c'      # 0
podman exec halsa-worker sh -c 'ulimit -c'   # 0

# Sincronización idempotente: la segunda vez, "sin cambios".
~/halsa/deploy/scripts/sincronizar-npm.sh && ~/halsa/deploy/scripts/sincronizar-npm.sh

# El proxy host de Keycloak borra las cabeceras de certificado (paso 5.1).
curl -s -o /dev/null -w '%{http_code}\n' -H 'ssl-client-cert: FALSO' -H 'ssl-client-verify: SUCCESS' \
  https://auth.edcastdev.com/realms/halsa/.well-known/openid-configuration
#   y en los logs de Keycloak no debe aparecer "nginx could not verify the certificate" por ese request.

# Los certificados (deploy-quadlets/README, "Validación de los certificados").
HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/verificar-certificados.sh
```

Tras un `sudo reboot` de prueba, Halsa y NPM deben volver solos (linger), sin que nadie entre como `edhunter`.

Y en <https://www.ssllabs.com/ssltest/>: `halsa-api` y `halsa-pacientes` deben calificar **A+**.

## 10. Despliegue desde el CI (SPEC 14)

El CI entra como `halsa-deploy`, un usuario sin contraseña cuya llave solo puede ejecutar
`halsa-recibir-despliegue`. El script corre como **`edhunter`** vía `sudo -u`, sin root: carga las imágenes en el
almacén de Podman de `edhunter` (como root irían a otro almacén, que los Quadlets no ven), y `.env`, los secretos y los
datos de NPM son de `edhunter`.

1. **El script**, fuera de `~/halsa/deploy` (el propio despliegue reescribe esa carpeta) y de root, para que el CI no
   pueda cambiar lo que ejecuta:
   ```bash
   sudo install -o root -g root -m 0755 ~/halsa/deploy/scripts/recibir-despliegue.sh /usr/local/sbin/halsa-recibir-despliegue
   ```
2. **El usuario.** Necesita un shell real: `sshd` ejecuta el `command=` forzado **con el shell del usuario**, y con
   `/usr/sbin/nologin` cualquier conexión falla. La restricción la ponen el `command=` y el `sudo`, no el shell.
   ```bash
   sudo adduser --system --group --shell /bin/sh --home /home/halsa-deploy halsa-deploy   # sin contraseña
   ```
3. **El sudoers.** `env_keep` es imprescindible: sin él `sudo` borra `SSH_ORIGINAL_COMMAND` y el script rechaza
   todo con exit `2`.
   ```bash
   cat > /tmp/halsa-deploy <<'EOF'
   Defaults!/usr/local/sbin/halsa-recibir-despliegue env_keep += "SSH_ORIGINAL_COMMAND"
   halsa-deploy ALL=(edhunter) NOPASSWD: /usr/local/sbin/halsa-recibir-despliegue
   EOF
   sudo visudo -cf /tmp/halsa-deploy && sudo install -o root -g root -m 0440 /tmp/halsa-deploy /etc/sudoers.d/halsa-deploy
   sudo -l -U halsa-deploy         # solo (edhunter) NOPASSWD: /usr/local/sbin/halsa-recibir-despliegue
   ```
   Para cambiar `HALSA_PAQUETE_MAX_MB` o `HALSA_VERSIONES_RETENIDAS` (README, "Environment variables"), agrégalas al
   `env_keep` y al `command=` del paso 5 (`command="HALSA_VERSIONES_RETENIDAS=8 sudo -u edhunter …"`).
4. **La llave del CI**, generada fuera del VPS (en tu máquina) y sin passphrase:
   ```bash
   ssh-keygen -t ed25519 -N '' -C ci-halsa -f ci-halsa
   ```
   `ci-halsa` (privada) va al secret `VPS_SSH_KEY`; después se borra de tu máquina.
5. **`authorized_keys`**, una sola línea con `ci-halsa.pub`:
   ```bash
   sudo install -d -o halsa-deploy -g halsa-deploy -m 0700 /home/halsa-deploy/.ssh
   echo 'command="sudo -u edhunter /usr/local/sbin/halsa-recibir-despliegue",no-pty,no-port-forwarding,no-agent-forwarding,no-X11-forwarding ssh-ed25519 AAAA… ci-halsa' \
     | sudo tee /home/halsa-deploy/.ssh/authorized_keys >/dev/null
   sudo chown halsa-deploy:halsa-deploy /home/halsa-deploy/.ssh/authorized_keys
   sudo chmod 600 /home/halsa-deploy/.ssh/authorized_keys
   ```
   Si `sshd_config` tiene `AllowUsers` o `AllowGroups`, agrega `halsa-deploy`.
6. **La huella del host** para `VPS_SSH_KNOWN_HOSTS`. Obtenla desde fuera y compárala con la del propio VPS:
   ```bash
   ssh-keyscan -t ed25519 <vps>                         # en tu máquina → VPS_SSH_KNOWN_HOSTS
   ssh-keygen -lf /etc/ssh/ssh_host_ed25519_key.pub     # en el VPS: la misma huella SHA256
   ```
   El host del secret debe ser exactamente `VPS_HOST`: si `VPS_HOST` es una IP, el keyscan se hace a la IP.
7. **GitHub** → *Settings → Environments → New environment* `produccion`:
   - *Required reviewers*: al menos una persona;
   - *Deployment branches and tags*: *Selected branches*, solo `main`;
   - *Environment secrets*: `VPS_HOST`, `VPS_USER` (`halsa-deploy`), `VPS_SSH_KEY` y `VPS_SSH_KNOWN_HOSTS`.
8. **Verificación:**
   ```bash
   ssh -i ci-halsa halsa-deploy@<vps>; echo "exit=$?"            # "comando no permitido", exit=2, sin shell
   ssh -i ci-halsa halsa-deploy@<vps> bash; echo "exit=$?"       # igual
   ```
   Si el job del CI termina con `desplegar: command not found` (exit `127`), la llave **sí entró, pero sin el
   `command=`**: el shell remoto ejecutó `desplegar v1.0.<n>` como comando, y esa llave tiene un shell completo en el
   VPS. Pasa cuando `VPS_USER` no es `halsa-deploy`, cuando `ci-halsa.pub` también está en otro `authorized_keys`
   (`ssh-copy-id`), o cuando la línea del paso 5 no lleva `command=`. `sudo journalctl -u ssh | grep Accepted` dice
   con qué usuario entró. Quita la llave de cualquier otro `authorized_keys` y deja solo la línea del paso 5.

   Si termina con `Permission denied (publickey)`, `sshd` dice el motivo en el VPS:
   `sudo journalctl -u ssh --since "10 min ago"`. Los habituales:
   - `bad ownership or modes`: `/home/halsa-deploy` escribible por otros, o `.ssh` / `authorized_keys` que no son de
     `halsa-deploy` (0700 / 0600);
   - `not allowed because not listed in AllowUsers` (o `AllowGroups`): agrégalo en `sshd_config`;
   - `account is locked`: `sudo usermod -p '*' halsa-deploy` (sin contraseña, pero no bloqueada);
   - solo `Connection closed by authenticating user halsa-deploy … [preauth]`: la llave no coincide. Compara `ssh-keygen -lf ci-halsa` (tu máquina) con
     `sudo ssh-keygen -lf /home/halsa-deploy/.ssh/authorized_keys`. Suele ser la línea partida al pegarla, el
     `AAAA…` del ejemplo sin reemplazar, o un secret `VPS_SSH_KEY` de otra llave.

**Cuando cambie `deploy/scripts/recibir-despliegue.sh` en el repo** (su cabecera lleva `# version:`), el despliegue
copia el archivo nuevo a `~/halsa/deploy/scripts/`, pero el que se ejecuta es el instalado. Revisa el diff y
reinstálalo a mano:

```bash
diff /usr/local/sbin/halsa-recibir-despliegue ~/halsa/deploy/scripts/recibir-despliegue.sh
sudo install -o root -g root -m 0755 ~/halsa/deploy/scripts/recibir-despliegue.sh /usr/local/sbin/halsa-recibir-despliegue
```

## Actualizar NPM

Halsa depende de dos comportamientos de NPM:
- incluye `/data/nginx/custom/http_top.conf`;
- omite su `location /` cuando una línea de Advanced coincide con su regex.

Después de actualizar NPM:

1. Ejecutar `~/halsa/deploy/scripts/sincronizar-npm.sh`. Si NPM se recreó, el script vuelve a unirlo a `halsa_borde`.
   Para que no lo pierda: `Network=halsa-borde.network:ip=172.30.0.10` en su `npm.container` (deploy-quadlets/README).
2. Revisar que los tres proxy hosts de Halsa sigan **Online**.
3. Repetir los criterios del borde: los `404` de rutas filtradas, el `400` sin certificado en `halsa-integraciones` y
   el `X-Forwarded-For` reescrito.

Anotar aquí la versión verificada: `NPM 2.16 — 2026-10-15`.

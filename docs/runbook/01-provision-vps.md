# Runbook 01 — Aprovisionar el VPS

> SPEC 13 y SPEC 14. Lleva Halsa a producción en el VPS que ya tiene **Nginx Proxy Manager (NPM)** y el **Keycloak compartido**
> de `auth.edcastdev.com`. Junto con el runbook 04 es también el procedimiento para **reconstruir** Halsa si se
> pierde el VPS. NPM y Keycloak tienen sus propios dueños y procedimientos: aquí solo se hace lo que Halsa necesita de
> ellos.

## Requisitos

| | |
| --- | --- |
| VPS | El actual, con Docker. Para Halsa: 4 vCPU y 8 GB de RAM libres (los valores de `deploy/postgres/postgresql.conf` asumen 8 GB), y 100 GB de disco |
| NPM | Corriendo en Docker en el mismo host. Versión verificada: **2.16** (ver "Actualizar NPM") |
| Keycloak | `keykloack-keycloak-1` (26.7), publicado por NPM en `auth.edcastdev.com` |
| DNS | `halsa-api`, `halsa-integraciones` y `halsa-pacientes` `.edcastdev.com` → IP pública del VPS |
| Fuera del VPS | bucket S3 de backups (runbook 04), bucket WORM (SPEC 11), monitor externo y heartbeat (runbook 07) |
| Custodia | la KEK, la llave de sello y la raíz de la CA, en sus ubicaciones fuera de línea (runbook 02) |

## 1. Sistema base (lo que falte)

```bash
apt install -y gettext-base openssl rsync     # envsubst: sincronizar-npm.sh e importar-realms.sh;
                                              # rsync: recibir-despliegue.sh (SPEC 14)

# Sin core dumps en el host (los contenedores ya llevan ulimits core: 0).
echo '* hard core 0' > /etc/security/limits.d/90-halsa-nocore.conf
echo 'kernel.core_pattern=|/bin/false' > /etc/sysctl.d/90-halsa-nocore.conf
sysctl --system
```

No hay registry ni `docker login` (SPEC 14): el CI construye las imágenes y las carga en el VPS por SSH.

> **Puertos:** Halsa no publica ninguno (`docker compose ps` lo verifica en el paso 9). El 80 y el 443 son de NPM.

## 2. Código de despliegue

En el VPS solo vive `deploy/`, en `/opt/halsa/deploy`. No hay código fuente, builds ni clon de git: cada despliegue
del CI lo sincroniza con `rsync --delete` (paso 10) y nunca toca `.env`.

La **primera vez**, los pasos 5 y 6 necesitan esos archivos antes del primer despliegue del CI. Se copian una vez
desde una máquina con el repo, del mismo commit que se va a desplegar:

```bash
# En tu máquina:
git archive main deploy/ | ssh root@<vps> 'mkdir -p /opt/halsa && tar -x --no-same-owner -C /opt/halsa'

# En el VPS:
cd /opt/halsa/deploy
cp .env.ejemplo .env && chmod 600 .env
$EDITOR .env
```

En `.env`, además de hosts, buckets, heartbeat y hash de Seq:

```bash
docker ps --format '{{.Names}}' | grep -i -E 'npm|proxy'        # → HALSA_NPM_CONTENEDOR
docker inspect <npm> -f '{{range .Mounts}}{{.Source}} → {{.Destination}}{{println}}{{end}}' | grep ' /data$'
                                                                  # → HALSA_NPM_DATA (la carpeta del host)
```

`HALSA_UPSTREAM_KEYCLOAK` es **el mismo** *forward* que usa hoy el proxy host `auth.edcastdev.com` en NPM, p. ej.
`http://keykloack-keycloak-1:8080`.

## 3. Secretos

Sigue el **runbook 02**: crea `/etc/halsa/secretos/` (root:root 0700, archivos 0600), restaura la KEK y la llave de
sello desde su custodia y genera las contraseñas. Exclúyelo de los snapshots del proveedor.

## 4. PKI de integraciones

Sigue el **runbook 06**, "Instalar la intermedia en el VPS". Al final existen `/etc/halsa/pki/publicar/`
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
   /opt/halsa/deploy/keycloak/importar-realms.sh     # pide un admin de master; idempotente
   ```
3. **SMTP** de `halsa-pacientes`: consola de Keycloak → realm `halsa-pacientes` → *Realm settings → Email*. Hasta
   entonces, el autorregistro de pacientes no se completa (`verifyEmail=true`). No abrir el portal sin esto.

## 6. Primer arranque de Halsa

```bash
cd /opt/halsa/deploy && C="docker compose -f docker-compose.prod.yml"
$C up -d postgres redis antivirus seq
# PostgreSQL crea los roles la primera vez (initdb/01-roles.sh).
$C exec -u postgres postgres pgbackrest --stanza=halsa stanza-create
```

Luego, el primer despliegue: configura el paso 10 y aprueba el job `desplegar` del CI en GitHub (o ejecuta
**Re-run** del último workflow de `main`). `recibir-despliegue.sh` carga las imágenes y llama a
`./scripts/desplegar.sh v1.0.<n>`, que migra, levanta api/worker/bff, verifica `/health` y sincroniza NPM.

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
2. **Backups** (runbook 04): el cron de pgBackRest y el primer backup completo.
3. **Monitores externos** (runbook 07).

## 9. Verificación (criterios *staging* de la SPEC 13)

```bash
cd /opt/halsa/deploy && C="docker compose -f docker-compose.prod.yml"

# Halsa no publica puertos.
$C ps --format '{{.Service}} {{.Ports}}' | grep -- '->' && echo "FALLO: un servicio de Halsa publica puertos"

# Egreso: api no sale a internet; worker sí.
$C exec api curl -sS -m 5 https://example.com >/dev/null && echo "FALLO: api tiene egreso" || echo "OK: api sin egreso"
$C exec worker curl -sS -m 5 -o /dev/null https://example.com && echo "OK: worker con egreso"

# Sin core dumps.
$C exec api sh -c 'ulimit -c'      # 0
$C exec worker sh -c 'ulimit -c'   # 0

# Sincronización idempotente: la segunda vez, "sin cambios".
./scripts/sincronizar-npm.sh && ./scripts/sincronizar-npm.sh

# El proxy host de Keycloak borra las cabeceras de certificado (paso 5.1).
curl -s -o /dev/null -w '%{http_code}\n' -H 'ssl-client-cert: FALSO' -H 'ssl-client-verify: SUCCESS' \
  https://auth.edcastdev.com/realms/halsa/.well-known/openid-configuration
#   y en los logs de Keycloak no debe aparecer "nginx could not verify the certificate" por ese request.
```

Y en <https://www.ssllabs.com/ssltest/>: `halsa-api` y `halsa-pacientes` deben calificar **A+**.

## 10. Despliegue desde el CI (SPEC 14)

El CI entra como `halsa-deploy`, un usuario sin contraseña cuya llave solo puede ejecutar
`halsa-recibir-despliegue`. El script corre como root vía `sudo`, porque `.env`, `/etc/halsa/secretos` y los datos de
NPM son de root.

1. **El script**, fuera de `/opt/halsa/deploy` (el propio despliegue reescribe esa carpeta):
   ```bash
   install -o root -g root -m 0755 /opt/halsa/deploy/scripts/recibir-despliegue.sh /usr/local/sbin/halsa-recibir-despliegue
   ```
2. **El usuario.** Necesita un shell real: `sshd` ejecuta el `command=` forzado **con el shell del usuario**, y con
   `/usr/sbin/nologin` cualquier conexión falla. La restricción la ponen el `command=` y el `sudo`, no el shell.
   ```bash
   adduser --system --group --shell /bin/sh --home /home/halsa-deploy halsa-deploy   # sin contraseña
   ```
3. **El sudoers.** `env_keep` es imprescindible: sin él `sudo` borra `SSH_ORIGINAL_COMMAND` y el script rechaza
   todo con exit `2`.
   ```bash
   cat > /tmp/halsa-deploy <<'EOF'
   Defaults!/usr/local/sbin/halsa-recibir-despliegue env_keep += "SSH_ORIGINAL_COMMAND"
   halsa-deploy ALL=(root) NOPASSWD: /usr/local/sbin/halsa-recibir-despliegue
   EOF
   visudo -cf /tmp/halsa-deploy && install -o root -g root -m 0440 /tmp/halsa-deploy /etc/sudoers.d/halsa-deploy
   sudo -l -U halsa-deploy         # solo /usr/local/sbin/halsa-recibir-despliegue
   ```
   Para cambiar `HALSA_PAQUETE_MAX_MB` o `HALSA_VERSIONES_RETENIDAS` (README, "Environment variables"), agrégalas al
   `env_keep` y al `command=` del paso 5 (`command="HALSA_VERSIONES_RETENIDAS=8 sudo …"`).
4. **La llave del CI**, generada fuera del VPS (en tu máquina) y sin passphrase:
   ```bash
   ssh-keygen -t ed25519 -N '' -C ci-halsa -f ci-halsa
   ```
   `ci-halsa` (privada) va al secret `VPS_SSH_KEY`; después se borra de tu máquina.
5. **`authorized_keys`**, una sola línea con `ci-halsa.pub`:
   ```bash
   install -d -o halsa-deploy -g halsa-deploy -m 0700 /home/halsa-deploy/.ssh
   echo 'command="sudo /usr/local/sbin/halsa-recibir-despliegue",no-pty,no-port-forwarding,no-agent-forwarding,no-X11-forwarding ssh-ed25519 AAAA… ci-halsa'      > /home/halsa-deploy/.ssh/authorized_keys
   chown halsa-deploy:halsa-deploy /home/halsa-deploy/.ssh/authorized_keys && chmod 600 /home/halsa-deploy/.ssh/authorized_keys
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

**Cuando cambie `deploy/scripts/recibir-despliegue.sh` en el repo** (su cabecera lleva `# version:`), el despliegue
copia el archivo nuevo a `/opt/halsa/deploy/scripts/`, pero el que se ejecuta es el instalado. Revisa el diff y
reinstálalo a mano:

```bash
diff /usr/local/sbin/halsa-recibir-despliegue /opt/halsa/deploy/scripts/recibir-despliegue.sh
install -o root -g root -m 0755 /opt/halsa/deploy/scripts/recibir-despliegue.sh /usr/local/sbin/halsa-recibir-despliegue
```

## Actualizar NPM

Halsa depende de dos comportamientos de NPM:
- incluye `/data/nginx/custom/http_top.conf`;
- omite su `location /` cuando una línea de Advanced coincide con su regex.

Después de actualizar NPM:

1. Ejecutar `scripts/sincronizar-npm.sh`. Si NPM se recreó, el script vuelve a unirlo a `halsa_borde`.
2. Revisar que los tres proxy hosts de Halsa sigan **Online**.
3. Repetir los criterios del borde: los `404` de rutas filtradas, el `400` sin certificado en `halsa-integraciones` y
   el `X-Forwarded-For` reescrito.

Anotar aquí la versión verificada: `NPM 2.16 — 2026-10-15`.

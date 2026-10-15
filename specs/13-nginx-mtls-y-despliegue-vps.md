# SPEC 13 — Borde con Nginx Proxy Manager y mTLS, Keycloak compartido y despliegue de producción en VPS

> **Estado:** Aprobado
> **Depende de:** SPEC 01 a SPEC 12
> **Fecha:** 2026-10-09 (enmendada el 2026-10-15)
> **Objetivo:** Publicar Halsa en un VPS detrás del Nginx Proxy Manager existente y con el Keycloak compartido de `auth.edcastdev.com`, con un host por audiencia, mTLS con una CA privada y tokens ligados a certificado para las integraciones, y dejar el despliegue reproducible (imágenes desde CI, secretos, backups con PITR, alertas y runbook).

---

## Enmiendas del 2026-10-15

La versión aprobada asumía un Nginx propio, un Keycloak propio y un dominio con subdominios fijos. En el VPS real ya existen un **Nginx Proxy Manager (NPM)** y un **Keycloak 26.7** compartido. Esta enmienda cambia **el borde y la identidad**:

1. **NPM es el borde.**
   - Termina TLS, gestiona Let's Encrypt y hace el mTLS de `integraciones`.
   - Salen de Halsa el servicio `nginx`, el servicio `certbot` y la red `publica`.
   - El compose de NPM **no se toca**: `sincronizar-npm.sh` une NPM a la red `borde` y copia la configuración de Halsa (`deploy/npm/`) a su `/data`.
2. **Keycloak es el compartido de `https://auth.edcastdev.com`** (contenedor `keykloack-keycloak-1`, Keycloak 26.7).
   - Halsa no lo despliega, no lo actualiza y no configura su proxy host en NPM.
   - Halsa crea una vez sus realms `halsa` y `halsa-pacientes` con `deploy/keycloak/importar-realms.sh`.
   - Salen de Halsa el servicio `keycloak`, la base `keycloak` y su rol en PostgreSQL, los secretos `kc_db_password` y `kc_admin_password`, y `deploy/keycloak/arrancar.sh`.
   - Para conservar los tokens ligados a certificado (RFC 8705), el dueño de ese Keycloak hace **dos cambios**, documentados en el README y en el runbook 06: las opciones del lookup x509 al arrancar, y que el proxy host `auth.edcastdev.com` borre `ssl-client-cert` y `ssl-client-verify`.
3. **Hosts:** `halsa-api.edcastdev.com`, `halsa-integraciones.edcastdev.com` y `halsa-pacientes.edcastdev.com`. Cada uno se configura en su propia variable (`HALSA_HOST_*`); ya no se arman como `<audiencia>.<dominio>`.
4. **TLS 1.2 y 1.3 en todos los hosts:** es la política global de NPM. Se descarta "solo TLS 1.3".
5. **SMTP:** el de la verificación de correo de pacientes se configura más adelante en el realm `halsa-pacientes`, no en Halsa.
6. **Desarrollo** conserva su Keycloak local (`docker-compose.yml`) y un Nginx que imita a NPM con los mismos archivos.
   - El README documenta cómo configurar Keycloak en las dos situaciones: local y compartido.
7. **Ya implementado y vigente** en la rama `spec-13-nginx-mtls-y-despliegue-vps`:
   - el código de la API, el BFF y el Worker;
   - la PKI y los realms;
   - PostgreSQL y los backups;
   - las alertas y el CI de tests.

   Se rehacen los pasos 3, 4, 6, 8 y 10, y se ajustan el 7 y partes de los runbooks 01, 02, 03, 05, 06 y 07.

---

## Por qué existe esta spec

Las specs anteriores dejaron pendiente todo lo que vive en el borde y en la operación:

- mTLS y `private_key_jwt` (SPEC 01 y SPEC 05);
- `ForwardedHeaders` y la IP real en la auditoría (SPEC 04);
- el runbook de la KEK, la llave de sello, ClamAV, Redis y los *core dumps* (SPEC 03, 07, 11 y 12);
- el emisor de los tokens (`KC_HOSTNAME`, SPEC 01);
- las alertas operativas (SPEC 09 y SPEC 10);
- los contenedores de la API y el Worker (SPEC 09).

El VPS es un único servidor, así que la defensa en profundidad depende de dos cosas:

1. **qué sale de él**: los backups y los sellos van a buckets externos, y las llaves se custodian fuera de línea;
2. **qué expone**: Halsa no publica puertos; el único que lo hace es NPM.

---

## Alcance

**Incluido:**

- **Borde (NPM):** 3 proxy hosts (`halsa-api`, `halsa-integraciones` y `halsa-pacientes`), con su certificado de Let's Encrypt gestionado por NPM.
  - **Desde el repo:** las cabeceras de seguridad, los rate limits y las allowlists de rutas, en archivos que NPM incluye.
  - **`sincronizar-npm.sh`:** une NPM a la red `borde`, renderiza y copia la configuración y la PKI pública, valida con `nginx -t` dentro de NPM y lo recarga. Si la validación falla, vuelve atrás.
- **mTLS:** la CA privada de Halsa (raíz fuera de línea y una intermedia), scripts de emisión y revocación (CRL), `ssl_verify_client on` en `halsa-integraciones`, y el certificado reenviado a la API y al Keycloak compartido.
- **Tokens ligados a certificado** (RFC 8705) y `private_key_jwt` en los clients `int-*`, y la verificación `cnf.x5t#S256` en la API.
- **Keycloak compartido:**
  - `deploy/keycloak/importar-realms.sh` crea `halsa` y `halsa-pacientes` en el Keycloak de `auth.edcastdev.com` desde `deploy/keycloak/*.prod.json`, una vez y sin pisar un realm que ya exista;
  - la documentación de los dos cambios que necesita su dueño.
- `ForwardedHeaders` en la API y el BFF, con NPM como proxy de confianza (su IP fija en `borde`).
- **Compose de producción:** API, Worker, BFF, PostgreSQL con pgBackRest, Redis con AOF, ClamAV y Seq, con redes segmentadas y secretos de Docker. Sin Nginx, certbot ni Keycloak.
- El modo `Halsa:Rol=Migrador` y `ProductionGuard` (validación de la configuración de producción al arrancar).
- **Backups:** pgBackRest hacia un bucket S3 externo, con PITR y una restauración ensayada.
- **Observabilidad:** Seq con alertas por correo, un endpoint `/health/vivo` público mínimo, un *heartbeat* del Worker hacia un monitor externo y un monitor de disponibilidad externo.
- **CI/CD:** GitHub Actions (tests, `nginx -t` y publicación de imágenes en GHCR) y el script de despliegue en el VPS (*pull*, migrar, levantar, verificar y sincronizar NPM).
- **Entorno de desarrollo:** Keycloak local, un Nginx que imita a NPM con los mismos archivos de `deploy/npm/`, y una CA de desarrollo en `docker-compose.yml`, para probar mTLS de punta a punta.
- **README:** una sección "Keycloak" que explica cómo queda configurado en local (`docker-compose.yml`, el realm importado al arrancar, los certificados de la PKI de desarrollo) y en un Keycloak compartido (opciones de arranque, cabeceras del proxy host, `importar-realms.sh`, el secreto del BFF, el SMTP del realm y el alta de clients `int-*`).
- El runbook en `docs/runbook/`.
- `.env.example` (desarrollo, `docker-compose.yml`) y `deploy/.env.ejemplo` (producción), con las variables que se necesitan y sin secretos.

**Fuera de alcance (specs futuras):**

- **La instalación, actualización y configuración general de NPM y del Keycloak compartido**, el proxy host `auth.edcastdev.com` (salvo las dos cabeceras que se piden) y los otros sitios y realms que alojan.
- La configuración del SMTP del realm `halsa-pacientes` (la hace el usuario después; el runbook 01 dice dónde).
- La ruta degradada para las ARL sin mTLS (`client_secret` + allowlist de IP + HMAC del cuerpo, ARQ §10) → spec propia, cuando una ARL lo necesite.
- La alta disponibilidad (varios VPS, balanceador y réplica de PostgreSQL) → spec propia.
- El WAF (ModSecurity u otro) → spec propia. ARQ lo menciona en el diagrama.
- Prometheus y Grafana → descartado (ver Decisiones).
- El hosting del frontend (SPA del backoffice y del portal) → fuera del backend.
- Una base de datos por ARL (ARQ §3.2) → diferido.
- El escaneo de imágenes y el SBOM en el CI → spec propia.
- La copia fuera del VPS de los soportes cifrados (volumen `archivos`, SPEC 07) → spec propia.

---

## Modelo de datos

Esta spec no crea tablas de negocio.
Introduce archivos de despliegue y configuración.

### Estructura de archivos

```text
deploy/
  docker-compose.prod.yml
  .env.ejemplo                      # HALSA_HOST_*, HALSA_KEYCLOAK_URL, HALSA_UPSTREAM_KEYCLOAK, HALSA_NPM_*, HALSA_TAG...
  npm/http.conf                     # nivel http: zonas de rate limit (prefijo halsa_) y el map de /vinculos
  npm/sitios/{api,integraciones,pacientes}.conf        # el "Advanced" de cada proxy host (plantillas ${HALSA_*})
  npm/snippets/{cabeceras,proxy}.conf
  postgres/Dockerfile               # postgres:17 + pgBackRest
  postgres/pgbackrest.conf
  keycloak/halsa.prod.json          # realms de producción: sin usuarios, sin halsa-dev-cli, sin OTP precargados, sin SMTP
  keycloak/halsa-pacientes.prod.json
  keycloak/importar-realms.sh       # crea los realms en el Keycloak compartido (kcadm por docker exec)
  pki/crear-raiz.sh  pki/crear-intermedia.sh  pki/emitir-cliente.sh  pki/revocar.sh  pki/generar-crl.sh
  scripts/desplegar.sh  scripts/restaurar-prueba.sh  scripts/sincronizar-npm.sh
docker/nginx-dev/                   # Nginx que imita a NPM (nginx.conf + servidores.conf) y CA de desarrollo
docs/runbook/{01-provision-vps,02-secretos-y-custodia,03-despliegue,04-backups-y-restauracion,
              05-llaves-y-rotacion,06-certificados-integraciones,07-alertas-e-incidentes}.md
.github/workflows/{ci.yml,release.yml}
```

Desaparecen `deploy/nginx/` (el `nginx.conf` principal es de NPM) y `deploy/keycloak/arrancar.sh`.

### Hosts

| Variable | Producción | Desarrollo |
| --- | --- | --- |
| `HALSA_HOST_API` | `halsa-api.edcastdev.com` | `api.localhost` |
| `HALSA_HOST_INTEGRACIONES` | `halsa-integraciones.edcastdev.com` | `integraciones.localhost` |
| `HALSA_HOST_PACIENTES` | `halsa-pacientes.edcastdev.com` | `pacientes.localhost` |
| `HALSA_KEYCLOAK_URL` | `https://auth.edcastdev.com` (compartido) | `http://localhost:8080` (contenedor local) |
| `HALSA_UPSTREAM_KEYCLOAK` | `http://keykloack-keycloak-1:8080` (el mismo upstream que usa el proxy host `auth.edcastdev.com`) | `http://halsa.backend.identity:8080` |

Emisores: `${HALSA_KEYCLOAK_URL}/realms/halsa` y `${HALSA_KEYCLOAK_URL}/realms/halsa-pacientes`.

### NPM: qué vive dónde

| Dónde | Qué |
| --- | --- |
| Interfaz de NPM, por proxy host de Halsa | Dominio, *forward* a `api:8080` o `bff:8080` (lo pide la interfaz; el `include` lo reemplaza), certificado de Let's Encrypt, **Force SSL** y **HTTP/2** activados, **HSTS**, **Cache Assets**, **Block Common Exploits** y **Websockets** desactivados (las cabeceras las pone Halsa). En **Advanced**, solo el texto de abajo |
| `<HALSA_NPM_DATA>/nginx/custom/halsa/` | `http.conf`, `snippets/` y `sitios/*.conf` ya renderizados (hosts y upstreams resueltos) |
| `<HALSA_NPM_DATA>/nginx/custom/http_top.conf` | Una línea, que agrega `sincronizar-npm.sh` si falta, sin tocar el resto: `include /data/nginx/custom/halsa/http.conf;` |
| `<HALSA_NPM_DATA>/halsa-pki/` | La copia de `/etc/halsa/pki/publicar`: `halsa-integraciones-ca.pem` y `halsa-integraciones.crl` |

El campo **Advanced** de cada proxy host de Halsa es exactamente esto:

```nginx
include /data/nginx/custom/halsa/sitios/<host>.conf;
#; location / { ... } lo define el include de Halsa: esta linea evita el location / de NPM
```

- **El comentario es funcional** (verificado con NPM 2.16). NPM solo omite su `location /` por defecto si una línea de Advanced coincide con `^(?:.*;)?\s*location\s*/\s*{`. Por eso el comentario empieza con `#;`: coincide sin ser configuración. Cada archivo de `sitios/` define **todas** sus `location`, incluida `location /` (`404`).
- **Upstreams como variables** (`set $halsa_api ...; proxy_pass $halsa_api;`): se resuelven en cada request con el *resolver* de NPM, no al cargar. Si un contenedor de Halsa está caído, solo sus rutas responden `502`: NPM y sus otros sitios siguen arrancando y recargando.
- **Lo que no llevan los archivos de `sitios/`:** `ssl_certificate`, `ssl_protocols`, `ssl_ciphers`, `listen`, `server_name` ni `access_log`, porque los pone NPM.
- **Lo que sí llevan:** las directivas de mTLS, `limit_req_status 429`, las cabeceras, `client_max_body_size`, los `proxy_set_header` y las `location`.

### Proxy hosts de Halsa

| Host | Backend | TLS | Rutas permitidas | Rate limit |
| --- | --- | --- | --- | --- |
| `halsa-api` | `api:8080` | 1.2 y 1.3 (política de NPM) | `/v1/*` (excepto `/v1/integraciones/*` y `/v1/paciente/*`) y `/health/vivo` | 20 r/s por IP, *burst* de 40. `client_max_body_size 15m`. |
| `halsa-integraciones` | `api:8080` y `${HALSA_UPSTREAM_KEYCLOAK}` | 1.2 y 1.3 (política de NPM) | `/v1/integraciones/*`, `POST /v1/casos/*/incapacidades`, `POST /auth/realms/halsa/protocol/openid-connect/token` | 10 r/s por certificado, *burst* de 50. `2m`, salvo `15m` en incapacidades. |
| `halsa-pacientes` | `bff:8080` | 1.2 y 1.3 (política de NPM) | `/paciente/*` y `/health/vivo` | 10 r/s por IP. `POST /paciente/api/vinculos`: 5 r/min por IP. |

- **Cabeceras** (los 3 hosts, desde `snippets/cabeceras.conf`): `Strict-Transport-Security: max-age=31536000; includeSubDomains`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: frame-ancestors 'none'` y `Permissions-Policy` mínima. Todo `429` lleva `Retry-After`.
- **Zonas de rate limit:** se llaman `halsa_*`, para no chocar con las de los otros sitios de NPM.
- **Puertos:** el 80, el 443 y el ACME los maneja NPM. **Ningún servicio de Halsa publica puertos.**
- **Cabeceras de confianza:** cada `location` de Halsa **reescribe** `X-Forwarded-For` con `$remote_addr`, así como `X-Forwarded-Proto/Host`. Borra las entrantes `X-Client-Cert`, `ssl-client-cert`, `ssl-client-verify`, `Forwarded` y `X-Original-For`. Nunca usa `$proxy_add_x_forwarded_for` (el valor por defecto de NPM, que agrega).

### Keycloak compartido (`auth.edcastdev.com`)

Lo que Halsa **necesita** del Keycloak 26.7 compartido. Lo aplica su dueño, con la guía del README y el runbook 06:

1. **Opciones de arranque**, que afectan a todos los realms pero solo se usan en los clients con tokens ligados a certificado:
   - `--spi-x509cert-lookup--provider=nginx`;
   - `--spi-x509cert-lookup--nginx--ssl-client-cert=ssl-client-cert`;
   - `--spi-x509cert-lookup--nginx--trust-proxy-verification=true`.
2. **El emisor fijo:** el hostname configurado es `https://auth.edcastdev.com` (`KC_HOSTNAME`), así que el emisor no cambia cuando el token se pide por `halsa-integraciones`.
3. **El proxy host `auth.edcastdev.com` en NPM** borra las cabeceras que manden los clientes. Si no lo hace, cualquiera podría declararle a Keycloak un certificado que no tiene: verificado que NPM, por defecto, las reenvía.
   - Va en su **Advanced** como un `location /` completo, que reemplaza el de NPM. Un `proxy_set_header` suelto, a nivel `server`, no aplica, porque el `location /` de NPM define los suyos.

     ```nginx
     location / {
         proxy_set_header ssl-client-cert "";
         proxy_set_header ssl-client-verify "";
         include conf.d/include/proxy.conf;
     }
     ```
   - El resto de ese proxy host (`/admin`, rate limits) es del dueño.
4. **Alcance de red:** NPM llega a `${HALSA_UPSTREAM_KEYCLOAK}`, la misma red que ya usa para `auth.edcastdev.com`.

Lo que **hace Halsa**, con `deploy/keycloak/importar-realms.sh`:

- **Entradas:** `HALSA_KEYCLOAK_CONTENEDOR`, las credenciales de un administrador del realm `master` (las pide en la terminal) y `/etc/halsa/secretos/kc_bff_secret`.
- **Render:** completa `deploy/keycloak/*.prod.json` con `envsubst` (`HALSA_HOST_PACIENTES`, `HALSA_BACKOFFICE_URL` y el secreto del BFF), sin escribir el resultado en disco.
- **Creación:** con `kcadm.sh` (`docker exec`), crea `halsa` y `halsa-pacientes` **solo si no existen**, y nunca pisa uno existente. Sale con `0` si los dos existen al final.
- **SMTP:** los realms de producción no lo traen. `verifyEmail=true` queda en `halsa-pacientes`, y el registro de pacientes no se completa hasta configurarlo (runbook 01).

### mTLS en `halsa-integraciones`

Va en `npm/sitios/integraciones.conf`, a nivel `server`:

```nginx
ssl_verify_client on;
ssl_client_certificate /data/halsa-pki/halsa-integraciones-ca.pem;   # intermedia + raíz
ssl_crl /data/halsa-pki/halsa-integraciones.crl;                     # CRL de la intermedia + CRL de la raíz
ssl_verify_depth 2;
proxy_set_header X-Client-Cert $ssl_client_escaped_cert;              # PEM url-encoded, para la API
proxy_set_header ssl-client-cert $ssl_client_escaped_cert;            # para el lookup x509 de Keycloak
proxy_set_header ssl-client-verify $ssl_client_verify;                # el lookup solo lo acepta con SUCCESS
```

- **CA privada:** la raíz es RSA 4096 con 10 años de validez, generada en un equipo sin red. Se custodia fuera de línea y nunca toca el VPS. La intermedia "Halsa Integraciones" es ECDSA P-384 con 3 años.
- **Certificados cliente:** ECDSA P-256 con 1 año de validez, `CN={client_id}` (p. ej. `int-arl-ARL_A`) y `extendedKeyUsage=clientAuth`.
- **CRL:** se regenera con cada revocación y cada 7 días, y vale 10. Como Nginx revisa la CRL de **cada** CA de la cadena, el archivo lleva la de la intermedia y la de la raíz.
  - **Vencida, falla cerrado:** Nginx rechaza todo. Hay una alerta 48 h antes.
  - **Publicación:** después de regenerarla, `sincronizar-npm.sh` la copia a NPM y lo recarga.
- **Sin certificado, inválido o revocado:** Nginx completa el *handshake* y responde **`400`**. Nada llega a la API ni a Keycloak.

### Tokens ligados a certificado

- En los realms de producción, los clients `int-*` usan el autenticador *Signed JWT* (`private_key_jwt`) con la llave pública del mismo certificado, y *OAuth 2.0 Mutual TLS Certificate Bound Access Tokens* activado. Se dan de alta con el runbook 06.
- El token se pide por `https://halsa-integraciones.edcastdev.com/auth/realms/halsa/protocol/openid-connect/token`, con mTLS. El emisor es siempre `https://auth.edcastdev.com/realms/halsa`.
- `IntegrationCertificateBindingMiddleware` (API) se ejecuta si el actor es `Integracion` e `Integraciones:ExigirCertificado=true` (obligatorio en producción).
  - Exige `X-Client-Cert`, que solo se acepta si el request viene del proxy de confianza.
  - Calcula `base64url(SHA-256(DER))` y lo compara con `cnf.x5t#S256`.
  - Si falta o no coincide, responde `401` con `Auth.CertificadoNoVinculado`.

### `ForwardedHeaders`

En la API y el BFF: `XForwardedFor | XForwardedProto | XForwardedHost`, con `ForwardLimit=1` y `KnownNetworks` igual a la **IP fija de NPM en `borde`** (`Red:ProxyConfiable=172.30.0.10/32`).
`HttpAuditActorProvider` (SPEC 04) pasa a leer la IP ya resuelta.

### `sincronizar-npm.sh`

Se ejecuta en el VPS. Es idempotente y lo llaman `desplegar.sh` y el cron de la CRL; también se puede ejecutar a mano. Lee de `.env`: `HALSA_NPM_CONTENEDOR`, `HALSA_NPM_DATA`, `HALSA_NPM_IP` (`172.30.0.10`), `HALSA_HOST_*` y `HALSA_UPSTREAM_KEYCLOAK`.

1. Si el contenedor de NPM no está en `halsa_borde`, ejecuta `docker network connect --ip $HALSA_NPM_IP halsa_borde $HALSA_NPM_CONTENEDOR`.
2. Renderiza `deploy/npm/**` con `envsubst`, solo las variables `HALSA_*`.
3. Guarda una copia de lo que hay en `<HALSA_NPM_DATA>/nginx/custom/halsa/` y en `halsa-pki/`, y copia lo nuevo.
4. Agrega la línea `include` a `http_top.conf` si falta.
5. Ejecuta `docker exec $HALSA_NPM_CONTENEDOR nginx -t`. Si falla, restaura la copia del paso 3 y termina con `1`. Así no rompe los otros sitios de NPM.
6. Ejecuta `docker exec $HALSA_NPM_CONTENEDOR nginx -s reload`.

### Compose de producción

| Servicio | Imagen | Redes | Notas |
| --- | --- | --- | --- |
| `api` | `ghcr.io/<org>/halsa-backend:${HALSA_TAG}` | `borde`, `app`, `datos` | `Halsa__Rol=Api`. Healthcheck en `/health`. Resuelve `auth.edcastdev.com` a la IP de NPM (`extra_hosts`): no tiene egreso y lee los metadatos y el JWKS por NPM. |
| `worker` | La misma imagen | `app`, `datos`, `egreso` | `Halsa__Rol=Worker`. Es el único con la red de egreso a internet (webhooks, WORM y heartbeat). |
| `migrador` | La misma imagen | `datos` | `Halsa__Rol=Migrador`, perfil `migracion`. Se ejecuta con `run --rm`. |
| `bff` | `ghcr.io/<org>/halsa-bff-paciente:${HALSA_TAG}` | `borde`, `app` | Resuelve `auth.edcastdev.com` a la IP de NPM. |
| `postgres` | `ghcr.io/<org>/halsa-postgres` (`deploy/postgres/Dockerfile`, publicada por `release.yml`) | `datos`, `egreso` | `hostssl` en `pg_hba`. `archive_command` de pgBackRest. Lleva `pgaudit`. Solo la base `halsa` y sus roles. |
| `redis` | `redis:7-alpine` | `app` | `appendonly yes` y `requirepass` desde un secreto. |
| `antivirus` | `clamav/clamav:1.4` | `app`, `egreso` | Volumen persistente de firmas (`freshclam` necesita egreso). |
| `seq` | `datalust/seq` | `app`, `egreso` | La interfaz solo por túnel SSH. Las alertas salen por SMTP. |

- **Redes:** `borde` (172.30.0.0/24), `app` y `datos` son `internal: true`. Solo `egreso` sale a internet.
  - NPM se une a `borde` con la IP fija 172.30.0.10, por fuera del compose (`sincronizar-npm.sh`).
  - NPM tiene además sus propias redes, entre ellas la que lo une a Keycloak. Halsa no las conoce ni las toca.
- **Contenedores:** `ulimits: core: 0`, `read_only: true` donde se pueda y `no-new-privileges`.
- **Secretos de Docker** (archivos en `/etc/halsa/secretos/`, `root:root 0600`, **excluidos** de todo backup del VPS):
  - `halsa_kek` y `halsa_sello`;
  - las contraseñas de cada rol de PostgreSQL;
  - las credenciales de los buckets (WORM y backups);
  - `kc_bff_secret` (el client secret del BFF, que también usa `importar-realms.sh`);
  - la contraseña de Redis.
- **Servicios que no corren como root:** copian sus secretos a un tmpfs propio al arrancar y luego bajan de privilegios (`deploy/scripts/arrancar-dotnet.sh` y `deploy/postgres/arrancar.sh`).

### Arranque y validación

- `Halsa:Rol=Migrador`: aplica las migraciones de los dos contextos con `halsa_owner`, ejecuta `auditoria.asegurar_particiones(3)` y termina con `0`, o con `1` si algo falla.
- `ProductionGuard`: en `Production`, el arranque falla si se cumple cualquiera de estas condiciones:
  - `Keycloak:RequireHttpsMetadata=false`;
  - un emisor no es `https`;
  - `Halsa:Rol` está vacío o vale `Ambos`;
  - `Integraciones:ExigirCertificado=false`;
  - `Webhooks:PermitirDestinosLocales=true`;
  - `Clinico:KekBase64` tiene valor;
  - el stub de afiliación está registrado;
  - la cadena de conexión `Database` usa `halsa_owner` o `postgres`.

### Backups (pgBackRest)

- Repositorio en un bucket S3 externo **distinto** del WORM, con `repo1-cipher-type=aes-256-cbc` (passphrase en un secreto).
- Programación: un completo el domingo a las 01:00, un diferencial diario a las 01:00 y WAL continuo (PITR). Retención de 4 completos.
- `deploy/scripts/restaurar-prueba.sh` restaura el último backup en un contenedor temporal, ejecuta las verificaciones (conteo de casos y `SELECT` de la auditoría) y lo destruye. Se ensaya cada mes (runbook 04).
- La KEK, la llave de sello y la raíz de la CA **no** están en estos backups: tienen custodia propia (runbook 02).
- Los realms de Halsa viven en la base del Keycloak compartido. Su respaldo es de su dueño. Lo que Halsa puede reconstruir está en el repo (`*.prod.json`) y en el runbook 06 (los clients `int-*`).

### Observabilidad

- `/health/vivo` es público, sin detalle (`200` o `503`). `/health` detallado solo es accesible desde la red interna.
- **Señales de Seq con alerta por correo** (propiedad `Alerta`):
  - `HealthCheck` en `Degraded` o `Unhealthy` durante más de 5 min;
  - un mensaje del outbox con `attempts >= MaxAttempts`;
  - un sello sin publicar durante más de 1 h (pendiente de la SPEC 11);
  - una entrega en `DEAD_LETTER`;
  - una suscripción `SUSPENDIDA`;
  - una CRL a menos de 48 h de vencer;
  - un certificado TLS de un host de Halsa a menos de 14 días de vencer.
- `HeartbeatJob` (Worker, cada 5 min) hace un `GET` a `Observabilidad:HeartbeatUrl` (un servicio de tipo *dead man's switch*). Si deja de llegar, el servicio externo alerta, porque el VPS no puede avisar de su propia caída.
- **Monitor de disponibilidad externo, cada minuto:**
  - `https://halsa-api.edcastdev.com/health/vivo`;
  - `https://halsa-pacientes.edcastdev.com/health/vivo`;
  - `https://auth.edcastdev.com/realms/halsa/.well-known/openid-configuration`.

  También detecta que NPM perdió la red `borde` (502).

### CI/CD

- `ci.yml` (en cada PR y push a `main`):
  - `dotnet build` y `dotnet test` (Testcontainers sobre Docker en `ubuntu-latest`);
  - `nginx -t` sobre `deploy/npm` con el envoltorio de `docker/nginx-dev`;
  - el verificador de auditoría de la SPEC 11 (pendiente).
- `release.yml` (en cada tag `v*`): construye `halsa-backend` (API, Worker y Migrador), `halsa-bff-paciente` y `halsa-postgres`, y las publica en GHCR con el tag y el digest.
- `deploy/scripts/desplegar.sh <tag>`, que se ejecuta en el VPS:
  1. `docker compose pull`;
  2. `docker compose run --rm migrador`;
  3. `up -d api worker bff`;
  4. espera hasta 120 s a que `/health` esté en `Healthy`;
  5. `sincronizar-npm.sh`;
  6. si el paso 4 falla, vuelve al tag anterior (las migraciones solo avanzan; ver Riesgos).
- El CI no tiene acceso SSH al VPS.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde. Los pasos marcados **(hecho)** ya están implementados en la rama y no cambian con las enmiendas.

1. **(hecho)** Añadir `ForwardedHeaders` a la API y al BFF, el endpoint `/health/vivo`, `Halsa:Rol=Migrador` y `ProductionGuard`. Tests unitarios de cada condición de `ProductionGuard`, y un test funcional de la IP real en la auditoría con `X-Forwarded-For` desde un proxy de confianza.
2. **(hecho)** Crear `IntegrationCertificateBindingMiddleware` y la opción `Integraciones:ExigirCertificado`. Tests: un `cnf` que coincide responde `200`, uno que no coincide responde `401`, y un `X-Client-Cert` que no viene del proxy de confianza se ignora.
3. **Rehacer el Nginx de desarrollo.**
   - `docker/nginx-dev/nginx.conf` y `servidores.conf` imitan a NPM: un `server` TLS por host de Halsa (`api`, `integraciones` y `pacientes` `.localhost`), con el certificado de desarrollo, que incluye `deploy/npm/sitios/<host>.conf`. Los archivos se montan en las **mismas rutas** que en NPM (`/data/nginx/custom/halsa/`, `/data/halsa-pki/`).
   - Keycloak de desarrollo sigue en `localhost:8080`, ya sin un host `auth.localhost`.
   - **(hecho)** La PKI de desarrollo, y los clients `int-*` con `private_key_jwt` más `int-arl-ARL_A-secreto`.
4. **Crear `deploy/npm/`** (`http.conf`, `snippets/` y los 3 `sitios/`) a partir de `deploy/nginx/`, con los hosts como variables y las reglas de la sección "NPM: qué vive dónde", y borrar `deploy/nginx/`. El job `nginx -t` del CI pasa a usar el envoltorio de desarrollo.
5. **(hecho)** Crear `deploy/pki/*.sh` (raíz, intermedia, emisión, revocación y CRL) y el runbook 06. Ajustar el runbook 06 para que la CRL se publique con `sincronizar-npm.sh`.
6. **Keycloak compartido.**
   - Quitar de `deploy/keycloak/*.prod.json` el `smtpServer` y sus placeholders, y dejar como placeholders el host del BFF y el backoffice.
   - Borrar `deploy/keycloak/arrancar.sh`.
   - Crear `deploy/keycloak/importar-realms.sh`.
   - Actualizar los tests de los realms de producción: sin SMTP, y con el redirect al host de pacientes.
7. **Ajustar PostgreSQL:** quitar el rol y la base `keycloak` de `initdb/01-roles.sh` y su línea de `pg_hba.conf`. **(hecho)** El resto del paso: `Dockerfile`, `pgbackrest.conf`, `restaurar-prueba.sh` y el runbook 04.
8. **Ajustar `deploy/docker-compose.prod.yml`.**
   - Quitar `nginx`, `certbot`, `keycloak`, `publica`, los volúmenes de Let's Encrypt y los secretos `kc_db_password`, `kc_admin_password` y `smtp_password`.
   - Cambiar a `Red__ProxyConfiable=172.30.0.10/32`.
   - Usar los emisores con `HALSA_KEYCLOAK_URL`.
   - Poner los `extra_hosts` de `auth.edcastdev.com` hacia la IP de NPM.
   - Pasar `HostsTls` a los 3 hosts de Halsa.
   - Actualizar `.env.ejemplo` con `HALSA_HOST_*`, `HALSA_KEYCLOAK_URL`, `HALSA_KEYCLOAK_CONTENEDOR`, `HALSA_UPSTREAM_KEYCLOAK`, `HALSA_NPM_CONTENEDOR`, `HALSA_NPM_DATA` y `HALSA_NPM_IP`.
9. **(hecho)** Crear `HeartbeatJob`, las señales y alertas de Seq documentadas (runbook 07) y la configuración del monitor externo. Ajustar en el runbook 07 los 3 monitores.
10. **Crear `deploy/scripts/sincronizar-npm.sh`**, llamarlo desde `desplegar.sh` y desde el cron de la CRL, y escribir la sección **"Keycloak"** del README: local y compartido. Actualizar los runbooks:
    - 01: el alta de los 3 proxy hosts en NPM y la primera sincronización; los cambios que se piden al dueño de Keycloak; `importar-realms.sh`; dónde se configura después el SMTP del realm;
    - 02: sin los secretos de Keycloak;
    - 03: el despliegue con sincronización, qué hacer si se recrea NPM, y sin la consola de Keycloak por túnel;
    - 05: TLS renovado por NPM, y el `kc_bff_secret` rotado en el Keycloak compartido;
    - 06: el alta de los clients `int-*` en el Keycloak compartido, y el token endpoint en `halsa-integraciones`;
    - 07: los monitores y el diagnóstico del 502.

---

## Criterios de aceptación

- Los criterios marcados *(dev)* se verifican con `docker compose up` local, que incluye Keycloak local, el Nginx que imita a NPM y la CA de desarrollo.
- Los marcados *(staging)* se verifican en el VPS con NPM, el Keycloak compartido y los hosts `halsa-*.edcastdev.com`.

- [ ] *(dev)* `curl` a `https://integraciones.localhost/v1/integraciones/furat` **sin** certificado cliente recibe `400` de Nginx, y el request no llega a la API.
- [ ] *(dev)* Con el certificado de `int-arl-ARL_A`, se obtiene un token por `private_key_jwt` en el token endpoint de `integraciones`, el token trae `cnf.x5t#S256`, y el POST del FURAT responde `202`.
- [ ] *(dev)* El mismo token, presentado con el certificado de `int-ips-110011234501`, recibe `401` con `Auth.CertificadoNoVinculado`.
- [ ] *(dev)* El mismo token, enviado a `api.localhost` (sin mTLS), recibe `401`.
- [ ] *(dev)* Después de revocar el certificado de `int-arl-ARL_A` y regenerar la CRL, Nginx responde `400` con ese certificado, y otro certificado vigente sigue pasando.
- [ ] *(dev)* Un header `X-Client-Cert` falso enviado por el cliente a `api.localhost` no llega a la API (Nginx lo borra).
- [ ] *(dev)* `https://api.localhost/v1/integraciones/furat` y `https://api.localhost/v1/paciente/mis-casos` responden `404` desde Nginx.
- [ ] *(dev)* La fila de auditoría de un request por Nginx registra la IP del cliente, no la del contenedor de Nginx.
- [ ] *(dev)* Un `X-Forwarded-For: 1.2.3.4` enviado por el cliente no aparece en la fila de auditoría: queda la IP real del cliente.
- [ ] La API con `ASPNETCORE_ENVIRONMENT=Production` y cualquiera de las condiciones de `ProductionGuard` termina con `1` y un mensaje que nombra la condición.
- [ ] `Halsa:Rol=Migrador` contra una base vacía aplica todas las migraciones y termina con `0`. Una segunda ejecución también termina con `0`, sin cambios.
- [ ] El CI corre `nginx -t` sobre `deploy/npm` (con el envoltorio de `docker/nginx-dev`) y falla si la configuración es inválida.
- [ ] El README tiene la sección "Keycloak", con los pasos para el Keycloak local y para uno compartido.
  - Para el compartido incluye: las 3 opciones de arranque, las 2 cabeceras del proxy host, `importar-realms.sh`, el secreto del BFF, el SMTP del realm y el alta de un client `int-*`.
- [ ] *(staging)* `importar-realms.sh` contra el Keycloak compartido crea `halsa` y `halsa-pacientes`. Una segunda ejecución termina con `0` y no cambia nada. Los realms preexistentes de ese Keycloak no cambian.
- [ ] *(staging)* Con el certificado de `int-arl-ARL_A` (emitido por la CA de producción), el token pedido en `halsa-integraciones` trae `cnf.x5t#S256` y emisor `https://auth.edcastdev.com/realms/halsa`, y el FURAT responde `202`.
- [ ] *(staging)* Una petición al token endpoint por `auth.edcastdev.com` con una cabecera `ssl-client-cert` inventada no produce un token ligado a ese certificado.
- [ ] *(staging)* SSL Labs califica `halsa-api` y `halsa-pacientes` con `A+`.
- [ ] *(staging)* `docker compose ps` del proyecto `halsa` muestra que **ningún** servicio de Halsa publica puertos. Desde el contenedor `api`, `curl https://example.com` falla (sin egreso), y desde `worker` funciona.
- [ ] *(staging)* `sincronizar-npm.sh` ejecutado dos veces seguidas no cambia nada. Después, `docker exec <npm> nginx -t` pasa y los otros proxy hosts de NPM, incluido `auth.edcastdev.com`, siguen respondiendo igual que antes.
- [ ] *(staging)* Un `sitios/*.conf` inválido hace que `sincronizar-npm.sh` termine con `1`, restaure la configuración anterior y deje NPM sirviendo.
- [ ] *(staging)* Después de recrear el contenedor de NPM, `halsa-api` responde `502` hasta que se ejecuta `sincronizar-npm.sh`, y luego `200`.
- [ ] *(staging)* `restaurar-prueba.sh` restaura el backup de la noche anterior y sus verificaciones pasan. Un PITR a un instante elegido recupera una fila insertada justo antes.
- [ ] *(staging)* Al detener el Worker, el monitor externo alerta en menos de 15 min (heartbeat).
- [ ] *(staging)* Al detener ClamAV, Seq envía la alerta de health `Degraded` en menos de 10 min.
- [ ] *(staging)* `desplegar.sh v1.0.1` sobre `v1.0.0` deja los servicios `Healthy` sin errores en Seq. Un tag con una imagen rota vuelve al tag anterior.
- [ ] *(staging)* Los archivos de `/etc/halsa/secretos/` no aparecen en el repositorio de pgBackRest ni en los snapshots del VPS (revisión del runbook 02).
- [ ] *(staging)* `ulimit -c` dentro de los contenedores `api` y `worker` es `0`.

---

## Decisiones

- **Sí:** todo el borde y el despliegue en una sola spec, por decisión del usuario.
- **Sí:** hosts separados por audiencia. **No:** un solo dominio con rutas (como en el diagrama de ARQ), porque `ssl_verify_client optional` en un host compartido debilita el mTLS y mezcla límites. **Cambio respecto a ARQ §2.**
- **Sí (enmienda):** los hosts `halsa-api`, `halsa-integraciones` y `halsa-pacientes` `.edcastdev.com`, cada uno en su variable. **No:** el patrón `<audiencia>.<dominio>`, porque el dominio es compartido con otros servicios.
- **Sí (enmienda):** el borde es el **NPM** que ya existe en el VPS, porque sirve otros sitios y es dueño del 80 y del 443. **No:** un Nginx propio de Halsa en el 443. **No:** NPM delante de un Nginx de Halsa, porque NPM terminaría TLS y el certificado cliente no llegaría a quien hace el mTLS. **No:** una segunda IP pública.
- **Sí (enmienda):** el compose de NPM **no se modifica**. Halsa se une a NPM con `docker network connect` y le copia su configuración a `/data`.
- **Sí (enmienda):** el repo es la fuente de verdad de la configuración de Halsa en NPM, y la interfaz solo guarda dominio, certificado, interruptores y el `include`. **No:** pegar la configuración en la interfaz.
- **Sí (enmienda):** `X-Forwarded-For` se **reescribe** con `$remote_addr` en cada `location` de Halsa. **No:** aceptar el `$proxy_add_x_forwarded_for` de NPM.
- **Sí (enmienda):** el proxy de confianza es la IP fija de NPM en `borde` (`/32`). **No:** toda la red `borde`.
- **Sí (enmienda):** TLS 1.2 (solo ECDHE-AESGCM) y 1.3 en **todos** los hosts, por ser la política global de NPM. **No:** solo TLS 1.3 en `api` y `pacientes`. SSL Labs A+ sigue siendo alcanzable con HSTS.
- **Sí (enmienda):** Keycloak es el **compartido** de `auth.edcastdev.com` (26.7), y Halsa solo agrega sus realms. **No:** un Keycloak propio en el compose de Halsa (ni su base en el PostgreSQL de Halsa).
- **Sí (enmienda):** se conserva RFC 8705 (`cnf.x5t#S256`), pidiendo al dueño de Keycloak las opciones del lookup x509. **No:** sustituirlo por comparar el CN del certificado con el `azp` del token, que es más débil.
- **Sí (enmienda):** Halsa solo pide que el proxy host `auth.edcastdev.com` borre `ssl-client-cert` y `ssl-client-verify`. **No:** imponer ahí el bloqueo de `/admin` ni los rate limits de login, porque afectarían a las otras aplicaciones de ese Keycloak. Son decisión de su dueño.
- **Sí (enmienda):** los realms se crean con `importar-realms.sh` (kcadm), solo si no existen. **No:** importarlos al arrancar el Keycloak compartido (`--import-realm`), que es de otro dueño. **No:** sobrescribir un realm existente.
- **Sí (enmienda):** el SMTP de `halsa-pacientes` se configura después, en el realm. **No:** variables de SMTP en el `.env` de Halsa.
- **Sí (enmienda):** en desarrollo, Keycloak local y un Nginx simple que imita a NPM con los mismos archivos. **No:** NPM ni un Keycloak compartido en desarrollo.
- **Sí:** una CA privada de Halsa, con la raíz fuera de línea, la intermedia en el VPS y CRL. **No:** certificados de CA públicas con huellas fijadas.
- **Sí:** el certificado viaja como PEM en `X-Client-Cert` y la API calcula `x5t#S256`, porque Nginx solo expone la huella SHA-1.
- **Sí:** se conserva un client con secreto solo en el realm de desarrollo, para depurar sin certificados. **No:** existe en producción.
- **Sí:** pgBackRest con PITR a un bucket externo distinto del WORM. **No:** `pg_dump` nocturno, por la pérdida de hasta un día de datos.
- **Sí:** las llaves (la KEK, la de sello y la raíz de la CA) se custodian fuera de los backups. Un backup robado no expone la HC.
- **Sí:** Seq con alertas, más un heartbeat y un monitor externos. **No:** Prometheus y Grafana en el VPS, porque serían 3 o 4 servicios más para un solo servidor.
- **Sí:** las imágenes, incluida la de PostgreSQL, se construyen en el CI y se publican en GHCR, y el VPS hace *pull*. **No:** construir en el VPS. **No:** SSH desde el CI.
- **Sí:** las migraciones se ejecutan como un paso separado (`Migrador`) antes de levantar la nueva versión (README: "migraciones desde el pipeline, no desde `Program`").
- **Sí:** redes internas de Docker y egreso solo para los servicios que lo necesitan (Worker, PostgreSQL, ClamAV y Seq).

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Un único VPS es un único punto de falla. | Backups con PITR fuera del VPS, sellos en WORM, un runbook de reconstrucción desde cero (01 + 04) y un monitor externo. La alta disponibilidad es una spec propia. |
| Las migraciones no son reversibles, así que el rollback de imagen no deshace el esquema. | Las migraciones deben ser compatibles hacia atrás con la versión anterior (patrón *expand/contract*), y se revisa en el PR. El runbook 03 documenta el *restore* PITR como último recurso. |
| Si vence la CRL, todas las integraciones caen. | Se regenera semanalmente con un cron (y `sincronizar-npm.sh`), y hay una alerta 48 h antes. |
| Se compromete la intermedia de la CA. | Se revoca la intermedia desde la raíz fuera de línea, se emite una intermedia nueva y se reemiten los certificados (runbook 06). |
| Let's Encrypt no renueva. | NPM renueva solo. La alerta llega 14 días antes (`TlsPorVencer` y el monitor externo), y el runbook 05 documenta la renovación desde NPM. |
| Una ARL no soporta `private_key_jwt` ni mTLS. | La ruta degradada queda como spec propia (ARQ §10). Hasta entonces, esa ARL no puede integrarse. |
| Si se pierden a la vez las llaves custodiadas y el VPS, la HC queda ilegible. | Custodia en dos ubicaciones independientes y un ensayo semestral de recuperación (runbook 02). |
| Rate limits mal calibrados bloquean a una ARL legítima en un pico. | Los límites son por certificado y están en configuración. El `429` incluye `Retry-After`. La alerta por exceso de `429` queda pendiente: el log de NPM no llega a Seq. |
| NPM se recrea y pierde la red `borde`: Halsa responde `502`. | El monitor externo alerta en 2 min. El runbook 03 y el 07 indican ejecutar `sincronizar-npm.sh`, que es idempotente. |
| Una configuración inválida de Halsa en NPM tumba los otros sitios en la recarga. | `sincronizar-npm.sh` valida con `nginx -t` dentro de NPM antes de recargar, y restaura la copia anterior si falla. |
| Una versión nueva de NPM cambia cómo decide omitir su `location /`, o cómo incluye `http_top.conf`. | El criterio *staging* de sincronización y el `nginx -t` lo detectan. El runbook 01 fija la versión de NPM verificada y pide repetir los criterios del borde al actualizarla. |
| Un cambio hecho a mano en la interfaz de NPM duplica o rompe directivas de Halsa. | El runbook 01 lista los interruptores exactos de cada proxy host, y `nginx -t` en la sincronización detecta los duplicados. |
| **(enmienda)** El dueño del Keycloak compartido lo actualiza o recrea sin las opciones del lookup x509: los tokens `int-*` se rechazan ("Client Certification missing for MTLS HoK Token Binding"). | Falla cerrado: ninguna integración obtiene token, en vez de obtener uno sin ligar. El README y el runbook 06 dejan las opciones por escrito, y la falla se ve en los logs de Keycloak y en los reclamos de las integraciones. |
| **(enmienda)** El proxy host `auth.edcastdev.com` no borra `ssl-client-cert`: un cliente podría declarar un certificado ajeno al pedir el token por ahí. | Para usarlo hace falta igual la llave privada del client (`private_key_jwt`). Aun así, el criterio *staging* lo verifica, y el runbook 01 lo exige antes de dar de alta integraciones. |
| **(enmienda)** Halsa depende de un Keycloak que no administra (caídas, actualizaciones, respaldos). | El monitor externo revisa el `.well-known` del realm `halsa`. Los realms se pueden recrear desde el repo (`importar-realms.sh` y runbook 06). |
| **(enmienda)** Sin SMTP en `halsa-pacientes`, el autorregistro de pacientes no se completa (`verifyEmail=true`). | Pendiente explícito: se configura antes de abrir el portal (runbook 01). |

---

## Qué **no** está en esta spec

- La instalación, actualización y configuración general de NPM y del Keycloak compartido.
- La configuración del SMTP de `halsa-pacientes`.
- La ruta degradada sin mTLS.
- La alta disponibilidad con varios VPS.
- El WAF.
- Prometheus y Grafana.
- El hosting del frontend.
- Una base de datos por ARL.
- El escaneo de imágenes y el SBOM.
- La copia fuera del VPS de los soportes cifrados.

Cada uno, si se aborda, va en su propia spec.

# SPEC 13 — Borde Nginx con mTLS y despliegue de producción en VPS

> **Estado:** Borrador
> **Depende de:** SPEC 01 a SPEC 12
> **Fecha:** 2026-10-09
> **Objetivo:** Publicar Halsa en un VPS detrás de Nginx, con un subdominio por audiencia, mTLS con una CA privada y tokens ligados a certificado para las integraciones, y dejar el despliegue reproducible (imágenes desde CI, secretos, backups con PITR, alertas y runbook).

---

## Por qué existe esta spec

Las specs anteriores dejaron pendiente todo lo que vive en el borde y en la operación:

- mTLS y `private_key_jwt` (SPEC 01 y SPEC 05);
- `ForwardedHeaders` y la IP real en la auditoría (SPEC 04);
- el runbook de la KEK, la llave de sello, ClamAV, Redis y los *core dumps* (SPEC 03, 07, 11 y 12);
- `KC_HOSTNAME` (SPEC 01);
- las alertas operativas (SPEC 09 y SPEC 10);
- los contenedores de la API y el Worker (SPEC 09).

El VPS es un único servidor, así que la defensa en profundidad depende de dos cosas:

1. **qué sale de él**: los backups y los sellos van a buckets externos, y las llaves se custodian fuera de línea;
2. **qué expone**: solo Nginx publica puertos.

---

## Alcance

**Incluido:**

- **Nginx:** 4 subdominios (`api`, `integraciones`, `auth` y `pacientes`), TLS de Let's Encrypt (certbot), cabeceras de seguridad, rate limits y allowlists de rutas.
- **mTLS:** la CA privada de Halsa (raíz fuera de línea y una intermedia), scripts de emisión y revocación (CRL), `ssl_verify_client on` en `integraciones`, y el certificado reenviado a la API y a Keycloak.
- **Tokens ligados a certificado** (RFC 8705) y `private_key_jwt` en los clients `int-*`, y la verificación `cnf.x5t#S256` en la API.
- `ForwardedHeaders` en la API y el BFF, con el proxy de confianza.
- **Compose de producción:** Nginx, API, Worker, BFF, Keycloak (modo prod), PostgreSQL con pgBackRest, Redis con AOF, ClamAV, Seq y certbot, con redes segmentadas y secretos de Docker.
- El modo `Halsa:Rol=Migrador` y `ProductionGuard` (validación de la configuración de producción al arrancar).
- **Backups:** pgBackRest hacia un bucket S3 externo, con PITR y una restauración ensayada.
- **Observabilidad:** Seq con alertas por correo, un endpoint `/health/vivo` público mínimo, un *heartbeat* del Worker hacia un monitor externo y un monitor de disponibilidad externo.
- **CI/CD:** GitHub Actions (tests y publicación de imágenes en GHCR) y el script de despliegue en el VPS (*pull*, migrar, levantar y verificar).
- **Entorno de desarrollo:** Nginx y una CA de desarrollo en `docker-compose.yml`, para probar mTLS de punta a punta.
- El runbook en `docs/runbook/`.

**Fuera de alcance (specs futuras):**

- La ruta degradada para las ARL sin mTLS (`client_secret` + allowlist de IP + HMAC del cuerpo, ARQ §10) → spec propia, cuando una ARL lo necesite.
- La alta disponibilidad (varios VPS, balanceador y réplica de PostgreSQL) → spec propia.
- El WAF (ModSecurity u otro) → spec propia. ARQ lo menciona en el diagrama.
- Prometheus y Grafana → descartado (ver Decisiones).
- El hosting del frontend (SPA del backoffice y del portal) → fuera del backend.
- Una base de datos por ARL (ARQ §3.2) → diferido.
- El escaneo de imágenes y el SBOM en el CI → spec propia.

---

## Modelo de datos

Esta spec no crea tablas de negocio.
Introduce archivos de despliegue y configuración.

### Estructura de archivos

```text
deploy/
  docker-compose.prod.yml
  .env.ejemplo                      # HALSA_DOMINIO, HALSA_TAG, rutas de secretos (sin valores)
  nginx/nginx.conf
  nginx/sitios/{api,integraciones,auth,pacientes}.conf
  nginx/snippets/{tls,cabeceras,proxy}.conf
  postgres/Dockerfile               # postgres:17 + pgBackRest
  postgres/pgbackrest.conf
  keycloak/halsa.prod.json          # realms de producción: sin usuarios, sin halsa-dev-cli, sin OTP precargados
  keycloak/halsa-pacientes.prod.json
  pki/crear-raiz.sh  pki/crear-intermedia.sh  pki/emitir-cliente.sh  pki/revocar.sh  pki/generar-crl.sh
  scripts/desplegar.sh  scripts/restaurar-prueba.sh
docker/nginx-dev/                   # Nginx y CA de desarrollo para docker-compose.yml
docs/runbook/{01-provision-vps,02-secretos-y-custodia,03-despliegue,04-backups-y-restauracion,
              05-llaves-y-rotacion,06-certificados-integraciones,07-alertas-e-incidentes}.md
.github/workflows/{ci.yml,release.yml}
```

### Subdominios

| Host | Backend | TLS | Rutas permitidas | Rate limit |
| --- | --- | --- | --- | --- |
| `api.<dominio>` | `api:8080` | 1.3 | `/v1/*` (excepto `/v1/integraciones/*` y `/v1/paciente/*`) y `/health/vivo` | 20 r/s por IP, *burst* de 40. `client_max_body_size 15m`. |
| `integraciones.<dominio>` | `api:8080` y `keycloak:8080` | 1.2 (ECDHE-AESGCM) y 1.3 | `/v1/integraciones/*`, `POST /v1/casos/*/incapacidades`, `POST /auth/realms/halsa/protocol/openid-connect/token` | 10 r/s por certificado, *burst* de 50. `2m`, salvo `15m` en incapacidades. |
| `auth.<dominio>` | `keycloak:8080` | 1.3 | `/realms/*` y `/resources/*`. `/admin` responde `404`. | 10 r/s por IP en `/realms/*/login-actions` |
| `pacientes.<dominio>` | `bff:8080` | 1.3 | `/paciente/*` | 10 r/s por IP. `POST /paciente/api/vinculos`: 5 r/min por IP. |

- **Cabeceras** (todos los hosts): `Strict-Transport-Security: max-age=31536000; includeSubDomains`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Content-Security-Policy: frame-ancestors 'none'` y `Permissions-Policy` mínima.
- **Puertos:** el puerto 80 solo atiende el reto ACME y redirige a 443. Solo Nginx publica puertos.
- **Cabeceras de confianza:** Nginx **borra** las cabeceras entrantes `X-Client-Cert` y `X-Forwarded-*` y las vuelve a escribir.

### mTLS en `integraciones.<dominio>`

```nginx
ssl_verify_client on;
ssl_client_certificate /etc/nginx/pki/halsa-integraciones-ca.pem;   # intermedia + raíz
ssl_crl /etc/nginx/pki/halsa-integraciones.crl;
ssl_verify_depth 2;
proxy_set_header X-Client-Cert $ssl_client_escaped_cert;            # PEM url-encoded
proxy_set_header ssl-client-cert $ssl_client_escaped_cert;          # para el lookup x509 de Keycloak
```

- **CA privada:** la raíz es RSA 4096 con 10 años de validez, generada en un equipo sin red. Se custodia fuera de línea y nunca toca el VPS. La intermedia "Halsa Integraciones" es ECDSA P-384 con 3 años.
- **Certificados cliente:** ECDSA P-256 con 1 año de validez, `CN={client_id}` (p. ej. `int-arl-ARL_A`) y `extendedKeyUsage=clientAuth`.
- **CRL:** se regenera con cada revocación y cada 7 días. Una CRL vencida hace que Nginx rechace todo (falla cerrado), y por eso hay una alerta 48 h antes.

### Tokens ligados a certificado

- En los realms de producción, los clients `int-*` usan el autenticador *Signed JWT* (`private_key_jwt`) con la llave pública del mismo certificado, y *OAuth 2.0 Mutual TLS Certificate Bound Access Tokens* activado.
- Keycloak arranca con `--spi-x509cert-lookup--provider=nginx --spi-x509cert-lookup--nginx--ssl-client-cert=ssl-client-cert` y `KC_HOSTNAME=https://auth.<dominio>`. El emisor es siempre `auth.<dominio>`, aunque el token se pida por `integraciones.<dominio>`.
- `IntegrationCertificateBindingMiddleware` (API) se ejecuta si el actor es `Integracion` e `Integraciones:ExigirCertificado=true` (obligatorio en producción). Exige `X-Client-Cert` (que solo se acepta si el request viene del proxy de confianza), calcula `base64url(SHA-256(DER))` y lo compara con `cnf.x5t#S256`. Si falta o no coincide, responde `401` con `Auth.CertificadoNoVinculado`.

### `ForwardedHeaders`

En la API y el BFF: `XForwardedFor | XForwardedProto | XForwardedHost`, con `ForwardLimit=1` y `KnownNetworks` igual a la red `borde` de Docker (configurable con `Red:ProxyConfiable`).
`HttpAuditActorProvider` (SPEC 04) pasa a leer la IP ya resuelta.

### Compose de producción

| Servicio | Imagen | Redes | Notas |
| --- | --- | --- | --- |
| `nginx` | `nginx:1.27-alpine` | `borde` | Publica 80 y 443. Monta los certificados de Let's Encrypt y la PKI. |
| `certbot` | `certbot/certbot` | — | Renovación por webroot cada 12 h, con recarga de Nginx. |
| `api` | `ghcr.io/<org>/halsa-backend:${HALSA_TAG}` | `borde`, `app`, `datos` | `Halsa__Rol=Api`. Healthcheck en `/health`. |
| `worker` | La misma imagen | `app`, `datos`, `egreso` | `Halsa__Rol=Worker`. Es el único con la red de egreso a internet (webhooks, WORM y heartbeat). |
| `migrador` | La misma imagen | `datos` | `Halsa__Rol=Migrador`, perfil `migracion`. Se ejecuta con `run --rm`. |
| `bff` | `ghcr.io/<org>/halsa-bff-paciente:${HALSA_TAG}` | `borde`, `app` | |
| `keycloak` | `quay.io/keycloak/keycloak:26.4` (`start`) | `borde`, `datos` | `KC_DB=postgres`, base `keycloak` con su propio rol, `KC_PROXY_HEADERS=xforwarded` y la consola de administración solo por un túnel SSH al puerto de gestión. |
| `postgres` | `deploy/postgres/Dockerfile` | `datos`, `egreso` | `hostssl` en `pg_hba`. `archive_command` de pgBackRest. Lleva `pgaudit`. |
| `redis` | `redis:7-alpine` | `app` | `appendonly yes` y `requirepass` desde un secreto. |
| `antivirus` | `clamav/clamav:1.4` | `app`, `egreso` | Volumen persistente de firmas (`freshclam` necesita egreso). |
| `seq` | `datalust/seq` | `app`, `egreso` | La interfaz solo por túnel SSH. Las alertas salen por SMTP. |

- **Redes:** `borde`, `app` y `datos` son `internal: true`. Solo `egreso` sale a internet.
- **Contenedores:** `ulimits: core: 0`, `read_only: true` donde se pueda y `no-new-privileges`.
- **Secretos de Docker** (archivos en `/etc/halsa/secretos/`, `root:root 0600`, **excluidos** de todo backup del VPS):
  - `halsa_kek` y `halsa_sello`;
  - las contraseñas de cada rol de PostgreSQL;
  - las credenciales de los buckets (WORM y backups);
  - los secretos de los clients de Keycloak (BFF);
  - la contraseña de Redis y SMTP.

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

### Observabilidad

- `/health/vivo` es público, sin detalle (`200` o `503`). `/health` detallado solo es accesible desde la red interna.
- **Señales de Seq con alerta por correo:**
  - `HealthCheck` en `Degraded` o `Unhealthy` durante más de 5 min;
  - un mensaje del outbox con `attempts >= MaxAttempts`;
  - un sello sin publicar durante más de 1 h;
  - una entrega en `DEAD_LETTER`;
  - una suscripción `SUSPENDIDA`;
  - una CRL a menos de 48 h de vencer;
  - un certificado TLS a menos de 14 días de vencer.
- `HeartbeatJob` (Worker, cada 5 min) hace un `GET` a `Observabilidad:HeartbeatUrl` (un servicio de tipo *dead man's switch*). Si deja de llegar, el servicio externo alerta, porque el VPS no puede avisar de su propia caída.
- Un monitor de disponibilidad externo revisa `/health/vivo` de `api`, `auth` y `pacientes` cada minuto.

### CI/CD

- `ci.yml` (en cada PR y push a `main`): `dotnet build` y `dotnet test` (Testcontainers sobre Docker en `ubuntu-latest`), y el verificador de auditoría de la SPEC 11.
- `release.yml` (en cada tag `v*`): construye `halsa-backend` (API, Worker y Migrador) y `halsa-bff-paciente`, y las publica en GHCR con el tag y el digest.
- `deploy/scripts/desplegar.sh <tag>`, que se ejecuta en el VPS:
  1. `docker compose pull`;
  2. `docker compose run --rm migrador`;
  3. `up -d api worker bff`;
  4. espera hasta 120 s a que `/health` esté en `Healthy`;
  5. si no, vuelve al tag anterior (las migraciones solo avanzan; ver Riesgos).
- El CI no tiene acceso SSH al VPS.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Añadir `ForwardedHeaders` a la API y al BFF, el endpoint `/health/vivo`, `Halsa:Rol=Migrador` y `ProductionGuard`. Tests unitarios de cada condición de `ProductionGuard`, y un test funcional de la IP real en la auditoría con `X-Forwarded-For` desde un proxy de confianza.
2. Crear `IntegrationCertificateBindingMiddleware` y la opción `Integraciones:ExigirCertificado`. Tests: un `cnf` que coincide responde `200`, uno que no coincide responde `401`, y un `X-Client-Cert` que no viene del proxy de confianza se ignora.
3. Crear la PKI de desarrollo en `docker/nginx-dev/` (un script que genera la CA y los certificados de `int-arl-ARL_A` e `int-ips-110011234501`) y Nginx de desarrollo en `docker-compose.yml`. Pasar los clients `int-*` del realm de desarrollo a `private_key_jwt` con tokens ligados a certificado, conservando un client `int-arl-ARL_A-secreto` solo para la depuración local.
4. Crear `deploy/nginx/*` con los 4 sitios, los snippets y los rate limits. Validar con `nginx -t` en el CI (un contenedor de Nginx con la configuración).
5. Crear `deploy/pki/*.sh` (raíz, intermedia, emisión, revocación y CRL) y el runbook 06.
6. Crear `deploy/keycloak/*.prod.json` y la configuración de Keycloak en modo prod con el lookup x509.
7. Crear `deploy/postgres/Dockerfile` (con pgBackRest y pgaudit), `pgbackrest.conf` y `restaurar-prueba.sh`, y el runbook 04.
8. Crear `deploy/docker-compose.prod.yml` (redes, secretos, `ulimits`, Redis con AOF, ClamAV, Seq y certbot) y `.env.ejemplo`.
9. Crear `HeartbeatJob`, las señales y alertas de Seq documentadas (runbook 07) y la configuración del monitor externo.
10. Crear `.github/workflows/ci.yml`, `release.yml` y `deploy/scripts/desplegar.sh`, y los runbooks 01, 02, 03 y 05.

---

## Criterios de aceptación

Los criterios marcados *(dev)* se verifican con `docker compose up` local, que incluye Nginx y la CA de desarrollo. Los marcados *(staging)* se verifican en un VPS de prueba con un dominio real.

- [ ] *(dev)* `curl` a `https://integraciones.localhost/v1/integraciones/furat` **sin** certificado cliente falla en el handshake TLS.
- [ ] *(dev)* Con el certificado de `int-arl-ARL_A`, se obtiene un token por `private_key_jwt` en el token endpoint de `integraciones`, el token trae `cnf.x5t#S256`, y el POST del FURAT responde `202`.
- [ ] *(dev)* El mismo token, presentado con el certificado de `int-ips-110011234501`, recibe `401` con `Auth.CertificadoNoVinculado`.
- [ ] *(dev)* El mismo token, enviado a `api.localhost` (sin mTLS), recibe `401`.
- [ ] *(dev)* Después de revocar el certificado de `int-arl-ARL_A` y regenerar la CRL, el handshake falla.
- [ ] *(dev)* Un header `X-Client-Cert` falso enviado por el cliente a `api.localhost` no llega a la API (Nginx lo borra).
- [ ] *(dev)* `https://api.localhost/v1/integraciones/furat` y `https://api.localhost/v1/paciente/mis-casos` responden `404` desde Nginx. `https://auth.localhost/admin/` también.
- [ ] *(dev)* La fila de auditoría de un request por Nginx registra la IP del cliente, no la del contenedor de Nginx.
- [ ] La API con `ASPNETCORE_ENVIRONMENT=Production` y cualquiera de las condiciones de `ProductionGuard` termina con `1` y un mensaje que nombra la condición.
- [ ] `Halsa:Rol=Migrador` contra una base vacía aplica todas las migraciones y termina con `0`. Una segunda ejecución también termina con `0`, sin cambios.
- [ ] El CI corre `nginx -t` sobre `deploy/nginx` y falla si la configuración es inválida.
- [ ] *(staging)* SSL Labs califica `api`, `auth` y `pacientes` con `A+`.
- [ ] *(staging)* `docker compose ps` muestra que solo `nginx` publica puertos. Desde el contenedor `api`, `curl https://example.com` falla (sin egreso), y desde `worker` funciona.
- [ ] *(staging)* `restaurar-prueba.sh` restaura el backup de la noche anterior y sus verificaciones pasan. Un PITR a un instante elegido recupera una fila insertada justo antes.
- [ ] *(staging)* Al detener el Worker, el monitor externo alerta en menos de 15 min (heartbeat).
- [ ] *(staging)* Al detener ClamAV, Seq envía la alerta de health `Degraded` en menos de 10 min.
- [ ] *(staging)* `desplegar.sh v1.0.1` sobre `v1.0.0` deja los servicios `Healthy` sin errores en Seq. Un tag con una imagen rota vuelve al tag anterior.
- [ ] *(staging)* Los archivos de `/etc/halsa/secretos/` no aparecen en el repositorio de pgBackRest ni en los snapshots del VPS (revisión del runbook 02).
- [ ] *(staging)* `ulimit -c` dentro de los contenedores `api` y `worker` es `0`.

---

## Decisiones

- **Sí:** todo el borde y el despliegue en una sola spec, por decisión del usuario.
- **Sí:** subdominios separados por audiencia. **No:** un solo dominio con rutas (como en el diagrama de ARQ), porque `ssl_verify_client optional` en un host compartido debilita el mTLS y mezcla límites. **Cambio respecto a ARQ §2.**
- **Sí:** una CA privada de Halsa, con la raíz fuera de línea, la intermedia en el VPS y CRL. **No:** certificados de CA públicas con huellas fijadas.
- **Sí:** TLS 1.2 (solo ECDHE-AESGCM) además de 1.3 en `integraciones`, por los cores de las ARL con pilas Java antiguas. Los demás hosts solo admiten 1.3.
- **Sí:** el certificado viaja como PEM en `X-Client-Cert` y la API calcula `x5t#S256`, porque Nginx solo expone la huella SHA-1.
- **Sí:** el token de integración se pide por `integraciones.<dominio>` (con mTLS) y el emisor sigue siendo `auth.<dominio>` (`KC_HOSTNAME`).
- **Sí:** se conserva un client con secreto solo en el realm de desarrollo, para depurar sin certificados. **No:** existe en producción.
- **Sí:** pgBackRest con PITR a un bucket externo distinto del WORM. **No:** `pg_dump` nocturno, por la pérdida de hasta un día de datos.
- **Sí:** las llaves (la KEK, la de sello y la raíz de la CA) se custodian fuera de los backups. Un backup robado no expone la HC.
- **Sí:** Seq con alertas, más un heartbeat y un monitor externos. **No:** Prometheus y Grafana en el VPS, porque serían 3 o 4 servicios más para un solo servidor.
- **Sí:** las imágenes se construyen en el CI y se publican en GHCR, y el VPS hace *pull*. **No:** construir en el VPS. **No:** SSH desde el CI.
- **Sí:** las migraciones se ejecutan como un paso separado (`Migrador`) antes de levantar la nueva versión (README: "migraciones desde el pipeline, no desde `Program`").
- **Sí:** Keycloak en modo prod con su propia base en el mismo PostgreSQL y la consola solo por túnel.
- **Sí:** redes internas de Docker y egreso solo para los servicios que lo necesitan (Worker, PostgreSQL, ClamAV y Seq).

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Un único VPS es un único punto de falla. | Backups con PITR fuera del VPS, sellos en WORM, un runbook de reconstrucción desde cero (01 + 04) y un monitor externo. La alta disponibilidad es una spec propia. |
| Las migraciones no son reversibles, así que el rollback de imagen no deshace el esquema. | Las migraciones deben ser compatibles hacia atrás con la versión anterior (patrón *expand/contract*), y se revisa en el PR. El runbook 03 documenta el *restore* PITR como último recurso. |
| Si vence la CRL, todas las integraciones caen. | Se regenera semanalmente con un cron, y hay una alerta 48 h antes. |
| Se compromete la intermedia de la CA. | Se revoca la intermedia desde la raíz fuera de línea, se emite una intermedia nueva y se reemiten los certificados (runbook 06). |
| Let's Encrypt no renueva (DNS o puerto 80 bloqueados). | Alerta 14 días antes. El runbook 05 documenta la renovación manual. |
| Una ARL no soporta `private_key_jwt` ni mTLS. | La ruta degradada queda como spec propia (ARQ §10). Hasta entonces, esa ARL no puede integrarse. |
| Si se pierden a la vez las llaves custodiadas y el VPS, la HC queda ilegible. | Custodia en dos ubicaciones independientes y un ensayo semestral de recuperación (runbook 02). |
| Rate limits mal calibrados bloquean a una ARL legítima en un pico. | Los límites son por certificado y están en configuración. El `429` incluye `Retry-After`, y Seq alerta si hay más de 100 `429` en 5 min por cliente. |

---

## Qué **no** está en esta spec

- La ruta degradada sin mTLS.
- La alta disponibilidad con varios VPS.
- El WAF.
- Prometheus y Grafana.
- El hosting del frontend.
- Una base de datos por ARL.
- El escaneo de imágenes y el SBOM.

Cada uno, si se aborda, va en su propia spec.

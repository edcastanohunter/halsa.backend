# Halsa.Backend

ASP.NET Core (.NET 10) solution built on Clean Architecture, DDD tactical patterns and CQRS, with
selective Event Sourcing on PostgreSQL and **no MediatR** - the in-process mediator is part of the
solution and you own it.

## Layout

```
src/
  Halsa.Backend.SharedKernel   DDD primitives, Result/Error, event-sourcing contracts. Zero dependencies.
  Halsa.Backend.Mediation      The mediator: ISender/IPublisher, handlers, pipeline behaviours.
  Halsa.Backend.Domain         Aggregates, domain events, domain errors. Depends only on SharedKernel.
  Halsa.Backend.Application    CQRS contracts, use cases, validators, behaviours, read models.
  Halsa.Backend.Infrastructure EF Core + Npgsql, event store, outbox, projections, Keycloak, cache.
  Halsa.Backend.Api            Minimal API endpoints, Result -> HTTP translation, health, OpenAPI.
  Halsa.Bff.Paciente           The patient portal's BFF (SPEC 12): OIDC, the session cookie, YARP to /v1/paciente/*.
tests/
  Halsa.Backend.UnitTests         Domain, mediator and behaviours. xUnit + NSubstitute + Shouldly.
  Halsa.Backend.ArchitectureTests Layer rules enforced with NetArchTest.
  Halsa.Backend.IntegrationTests  Real PostgreSQL via Testcontainers: store, concurrency, outbox.
  Halsa.Backend.FunctionalTests   The API end to end through WebApplicationFactory.
  Halsa.Bff.Paciente.Tests        The BFF in memory, against stub API and Keycloak handlers. No Docker needed.
```

The dependency rule is one-way: `Api -> Infrastructure -> Application -> Domain -> SharedKernel`.
`Halsa.Backend.ArchitectureTests` fails the build if anyone breaks it.

## Getting started

Prerequisites: the .NET 10 SDK, Docker (or Podman) with Compose v2.24+, and `bash` + `openssl` (Git Bash on Windows).

```bash
# 1. The development PKI (SPEC 13), BEFORE the first `docker compose up`: the dev Nginx mounts its certificates,
#    and Keycloak reads the int-* client certificates (keycloak.env) when it imports the realm. Idempotent;
#    the files go to docker/nginx-dev/pki/, which git ignores.
bash docker/nginx-dev/generar-pki.sh

# 2. The containers. -v matters: docker/postgres/init/01-roles.sql (halsa_owner, halsa_app, halsa_clinico,
#    halsa_ingesta, halsa_worker) and Keycloak's realm import only run on empty volumes.
docker compose down -v && docker compose up -d   # PostgreSQL, Keycloak, Seq, Redis, ClamAV and the dev Nginx

# 3. The clinical KEK (SPEC 03): 32 random bytes, in user-secrets and never in appsettings.
#    Without it the API refuses to start. Keep it: losing it makes existing clinical data unreadable.
dotnet user-secrets --project src/Halsa.Backend.Api set "Clinico:KekBase64" "$(openssl rand -base64 32)"

# 4. The API (and the Worker, Ambos in Development).
dotnet run --project src/Halsa.Backend.Api    # http://localhost:5600/scalar/v1
```

| URL | What |
| --- | --- |
| `http://localhost:5600/scalar/v1` | The API directly (OpenAPI UI). No Nginx, no mTLS. |
| `http://localhost:8080` | Keycloak (`admin` / `admin`). Issuer of the dev tokens. |
| `http://localhost:5341` | Seq (logs). |
| `https://api.localhost`, `https://integraciones.localhost`, `https://pacientes.localhost` | The dev edge: the same `deploy/npm` files NPM uses in production. curl needs `--cacert docker/nginx-dev/pki/raiz/raiz.crt`. |

**Optional: the edge with mTLS end to end.** Run the API with the `mtls` profile
(`dotnet run --project src/Halsa.Backend.Api --launch-profile mtls`, i.e. `Integraciones:ExigirCertificado=true`) and
request integration tokens with `bash docker/nginx-dev/token.sh int-arl-ARL_A`. The checks, step by step, are in
`docker/nginx-dev/README.md`.

**Troubleshooting:**
- **Keycloak already had the realms before you generated the PKI:** the `int-*` clients have no certificate.
  Re-import them as described in [Keycloak → Locally](#locally-docker-composeyml).
- **Port 443 or 8080 is already in use:** set `HALSA_DEV_PUERTO_HTTPS` in `.env` (see `.env.example`). Keycloak's
  8080 is fixed, because it's the issuer that `appsettings.json` expects.
- **On Podman:** see the notes in `docker/nginx-dev/README.md`. `host.docker.internal` resolves to the LAN IP there,
  so the API must listen on `0.0.0.0` for the dev Nginx to reach it.

The same image is the API and the Worker (SPEC 09): `Halsa:Rol` is `Api`, `Worker` or `Ambos`. In `Development` it
defaults to `Ambos`; anywhere else it is required. To run them apart, use the launch profiles
`dotnet run --project src/Halsa.Backend.Api --launch-profile api` and `--launch-profile worker` (port 5601, only
`/health`). The Worker drains the outbox and runs Quartz (clustered, JobStore in the `quartz` schema): the legal
deadline sweep every minute, the Colombian holidays, the audit partitions, the idempotency purge and the orphan
files. Its cross-tenant sweeps connect as `halsa_worker` (connection string `Worker`), which never reads `atel`.

On startup the API applies migrations as `halsa_owner` (connection string `Migrations`) and, in
`Development`, loads `Extensions/Seed/casos-dev.sql`: 3 cases for `ARL_A` and 2 for `ARL_B`. At
runtime it connects as `halsa_app` (connection string `Database`), which has no `BYPASSRLS` and no
`DELETE` on cases, so PostgreSQL row-level security isolates tenants even if an EF filter is wrong.
The clinical detail lives apart, in the encrypted `clinico` schema: only `ClinicalDbContext` reaches
it, as `halsa_clinico` (connection string `Clinical`); `halsa_app` has no access to the schema at all.
FURAT and FUREP reports from the ARL cores (`POST /v1/integraciones/{furat|furep}`, SPEC 05) are radicated
by `IngestaDbContext` as `halsa_ingesta` (connection string `Ingesta`): one transaction writes the case,
the report, the encrypted raw message and the encrypted initial clinical record, and the role can insert
into the clinical history but never read it.

Keycloak comes preloaded with the `halsa` and `halsa-pacientes` realms. Get a token for a dev user
(`analista.arla`, `analista.arlb`, `medico.ips`, ... password `dev`) with the `halsa-dev-cli` client:

```bash
curl -s -X POST http://localhost:8080/realms/halsa/protocol/openid-connect/token \
  -d grant_type=password -d client_id=halsa-dev-cli -d username=analista.arla -d password=dev
```

Some endpoints (loading an incapacidad, SPEC 07) require step-up: a human token with `acr="2"`. The `halsa`
realm's browser flow (`browser-loa`) asks for the password at level 1 and an OTP at level 2, so an
authorization code flow of `halsa-backoffice` with `acr_values=2` prompts for the OTP. Only
`calificador.arla` and `medico.ips` have one. **Development only** - these secrets live in
`docker/keycloak/halsa-realm.json` and the production realm is never imported from it:

| User | TOTP secret (base32, for an authenticator app) |
| --- | --- |
| `calificador.arla` | `NBQWY43BMRSXMY3BNRUWM2LDMFSG64RR` |
| `medico.ips` | `NBQWY43BMRSXM3LFMRUWG33JOBZTAMBR` |

TOTP, SHA-1, 6 digits, 30 seconds. `analista.arla` has no OTP and can never reach `acr="2"`. With an OTP
configured, the password grant of `halsa-dev-cli` also needs the current code (`-d totp=123456`) and still issues
`acr="1"`: only the browser flow reaches level 2.

## The patient portal (SPEC 12)

The portal never holds a token. `Halsa.Bff.Paciente` (`https://localhost:5610`) signs the patient in against the
`halsa-pacientes` realm as the confidential client `halsa-bff-paciente` (code + PKCE), keeps the tokens in a Redis
session (Data Protection keys in Redis too) and gives the browser only `__Host-halsa-paciente`, an `HttpOnly`, `Secure`,
`SameSite=Strict` cookie with the session key. `/paciente/api/{**rest}` is proxied to `{Api}/v1/paciente/{**rest}` with
the access token, refreshed in the server; nothing else is forwarded. Every proxied request and the logout need the
header `X-Halsa-Csrf: 1`. The `halsa-pacientes` realm allows self-registration (email verification is off in
development, which has no SMTP), but an account sees nothing until it redeems an activation code issued by the ARL.

```bash
# The BFF's client secret lives in user-secrets outside Development (appsettings.Development.json has the dev one).
dotnet dev-certs https --trust
dotnet run --project src/Halsa.Backend.Api        # http://localhost:5600
dotnet run --project src/Halsa.Bff.Paciente       # https://localhost:5610, Redis from docker compose
```

1. **A `Validado` case.** The dev seed only has `Radicado` cases: radicate a FURAT with `int-arl-ARL_A-secreto`
   (client secret `int-arl-ARL_A-secreto-secret`; `int-arl-ARL_A` itself needs its certificate since SPEC 13)
   (`POST /v1/integraciones/furat`, see `docs/api/atel-api.openapi.yaml`). The Worker (`Ambos` in Development)
   validates it within seconds.
2. **The code.** As `analista.arla` (token from `halsa-dev-cli`), `POST /v1/casos/{id}/codigos-activacion`. The answer
   is the only time the whole code (`ABC123-K7Q2M9XD4P`) exists: the database keeps its salt and hash.
3. **Sign in.** Open `https://localhost:5610/paciente/bff/login?returnUrl=/` and log in as `paciente.dev` / `dev`
   (or register a new account). Keycloak sends you back to `/`, which the BFF does not serve: that is expected.
4. **Redeem and look around**, from the DevTools console of that tab (same site, so the cookie goes along):

   ```js
   const api = (path, init = {}) => fetch(`/paciente/api/${path}`,
     { ...init, headers: { 'X-Halsa-Csrf': '1', 'Content-Type': 'application/json', ...init.headers } });
   await (await api('vinculos', { method: 'POST', body: JSON.stringify({ codigo: 'ABC123-K7Q2M9XD4P' }) })).json();
   await (await fetch('/paciente/bff/usuario')).json();          // { nombre, correo, vinculado: true }
   await (await api('mis-casos')).json();
   await fetch('/paciente/bff/logout', { method: 'POST', headers: { 'X-Halsa-Csrf': '1' } });   // 204
   ```

   The logout ends the Keycloak session through the server channel (the refresh token never reaches the browser):
   opening the login again asks for the password. Neither `localStorage`, `sessionStorage` nor any BFF response holds
   a token.

The ARL works the HC copy requests at `GET /v1/solicitudes-copia-hc` (`privacidad.arla`, permission
`solicitudes-hc:gestionar`); each one runs the legal deadline `SOLICITUD_COPIA_HC` (10 business days, pending legal
validation) until it is closed as `ENTREGADA` or `RECHAZADA`.

## The mediator

`Halsa.Backend.Mediation` is a small, dependency-free reimplementation of the pattern MediatR made
popular. It gives you `ISender.Send`, `IPublisher.Publish`, `IRequestHandler<,>`,
`INotificationHandler<>` and `IPipelineBehavior<,>` - and nothing else.

- Dispatch closes the open generics once per message type and caches the wrapper, so after the
  first call there is no reflection on the hot path.
- A request must have exactly one handler; registering two throws `DuplicateHandlerException` at
  startup instead of silently picking one.
- Behaviour order is registration order, outermost first.
- Notification fan-out is a strategy (`SequentialNotificationPublisher` by default,
  `ParallelNotificationPublisher` when handlers are provably independent).

```csharp
services.AddMediation(cfg =>
{
    cfg.RegisterServicesFromAssemblyContaining<SomeHandler>();
    cfg.AddOpenBehavior(typeof(LoggingBehavior<,>));
});
```

## CQRS

`ICommand`, `ICommand<T>` and `IQuery<T>` are markers over `IRequest<Result>` / `IRequest<Result<T>>`.
Commands go through validation; queries do not. Everything returns a `Result`, so expected failures
never travel as exceptions and `CustomResults.Problem` is the single place that maps them to HTTP.

## Event sourcing, deliberately partial

The event store infrastructure is kept, but no production aggregate uses it yet: `CasoMedico` is
state-based with `xmin` concurrency, and the store is exercised by a test-only `TestAggregate` in
`Halsa.Backend.IntegrationTests`. Reach for event sourcing when the history *is* the business value
(audit, clinical records, ledgers) and stay state-based otherwise. Both styles coexist in the same
DbContext and the same transaction.

Guarantees:

- Optimistic concurrency comes from the unique index on `(stream_id, version)`, not a read-then-check.
- `[EventName("...")]` pins the persisted name so renaming a C# type cannot orphan history.
- Append, projection and outbox insert share one transaction, so the read model cannot drift and an
  integration event cannot be published for a write that rolled back.

## Outbox

`IIntegrationEventPublisher` writes to `outbox.outbox_messages` inside the business transaction.
`OutboxProcessor` drains it with `FOR UPDATE SKIP LOCKED`, so several API replicas can run it
concurrently. `InProcessEventBus` delivers each message to the `IIntegrationEventHandler<T>`s of the
process, in a DI scope of its own (the asynchronous validation of a radication is one). `IEventBus` is the
seam: swap it for Kafka or Service Bus and no handler changes.

## The edge and production (SPEC 13)

- **Production edge:** the VPS's existing **Nginx Proxy Manager** (NPM). Halsa has three proxy hosts:
  `halsa-api`, `halsa-integraciones` (mTLS) and `halsa-pacientes` `.edcastdev.com`. Their configuration lives in
  `deploy/npm/`, and `deploy/scripts/sincronizar-npm.sh` takes it to NPM. NPM's compose is never modified.
- **Development, end to end with mTLS:** `docker compose up` also starts an Nginx that plays NPM's part
  (`docker/nginx-dev`). It includes the same `deploy/npm` files, with a development CA, on
  `https://{api,integraciones,pacientes}.localhost`. First run `bash docker/nginx-dev/generar-pki.sh`. See
  `docker/nginx-dev/README.md`.
- **Production stack:** `deploy/docker-compose.prod.yml` (images from GHCR, built by
  `.github/workflows/release.yml`), `deploy/.env.ejemplo`, and the runbooks in `docs/runbook/` (provisioning,
  secrets, deployment, backups, keys, integration certificates, alerts).
- **Startup modes:** `Halsa:Rol=Migrador` applies the migrations and exits with `0` or `1` (`desplegar.sh` runs it
  before each deploy). In `Production`, `ProductionGuard` refuses to start on any development shortcut.

## Deploying to production

A summary of the procedure. Each step links to the runbook with the details. Everything runs on the VPS as root,
from `/opt/halsa/deploy` (a sparse checkout of `deploy/` only; nothing is built there).

**One-time setup** ([runbook 01](docs/runbook/01-provision-vps.md)):

1. **Prerequisites.**
   - Docker with Compose, plus `gettext-base` (for `envsubst`).
   - Read access to GHCR (`docker login ghcr.io`).
   - DNS for `halsa-api`, `halsa-integraciones` and `halsa-pacientes` `.edcastdev.com` pointing at the VPS.
2. **Configuration.**
   - `cp .env.ejemplo .env && chmod 600 .env`, then fill in the hosts, buckets, heartbeat and Seq hash.
   - `HALSA_NPM_CONTENEDOR` and `HALSA_NPM_DATA` (NPM's container and its `/data` folder).
   - `HALSA_UPSTREAM_KEYCLOAK` (the same upstream as the `auth.edcastdev.com` proxy host).
   - **No secrets go in `.env`.**
3. **Secrets** ([runbook 02](docs/runbook/02-secretos-y-custodia.md)). Create `/etc/halsa/secretos/` (root:root
   0600, one file per secret, excluded from every backup). Restore the KEK and the seal key from their offline
   custody. Generate the passwords.
4. **Integrations CA** ([runbook 06](docs/runbook/06-certificados-integraciones.md)).
   - Install the intermediate on the VPS. The root never touches it.
   - Install the weekly CRL cron.
5. **Shared Keycloak** ([Keycloak → Shared instance](#shared-instance-production-httpsauthedcastdevcom)).
   - The owner adds the three x509-lookup startup options and the header-clearing block on `auth.edcastdev.com`.
   - Then `keycloak/importar-realms.sh` creates the realms.
   - Configure SMTP in `halsa-pacientes` before opening the portal.
6. **First start:**
   ```bash
   C="docker compose -f docker-compose.prod.yml"
   $C up -d postgres redis antivirus seq
   $C exec -u postgres postgres pgbackrest --stanza=halsa stanza-create
   ./scripts/desplegar.sh v1.0.0      # pull, migrate, start api/worker/bff, wait for Healthy, sync NPM
   ```
7. **NPM proxy hosts** (runbook 01, step 7). Create the three hosts with Let's Encrypt, **Force SSL** and **HTTP/2**,
   with HSTS, Cache Assets, Block Common Exploits and Websockets **off**. Each one's **Advanced** field is
   exactly:
   ```nginx
   include /data/nginx/custom/halsa/sitios/<api|integraciones|pacientes>.conf;
   #; location / { ... } lo define el include de Halsa: esta linea evita el location / de NPM
   ```
8. **Operations.**
   - Backups: the pgBackRest cron and a first full backup ([runbook 04](docs/runbook/04-backups-y-restauracion.md)).
   - Seq alerts, the heartbeat and the external monitors ([runbook 07](docs/runbook/07-alertas-e-incidentes.md)).
9. **Verification:** the commands in runbook 01, step 9, and the *(staging)* acceptance criteria of SPEC 13.

**Every release** ([runbook 03](docs/runbook/03-despliegue.md)):

1. Merge to `main` with CI green.
2. Tag: `git tag vX.Y.Z && git push origin vX.Y.Z`. `release.yml` publishes the images to GHCR.
3. On the VPS: `./scripts/desplegar.sh vX.Y.Z`. It migrates (`Halsa:Rol=Migrador`) and starts the new version. It
   rolls back to the previous tag if `/health` isn't `Healthy` within 120 s, then syncs NPM. Migrations only go
   forward, so they must be backward compatible (expand/contract).

**If NPM is recreated** (upgrade, restart), it loses the `halsa_borde` network and Halsa answers 502. Run
`./scripts/sincronizar-npm.sh`. It's idempotent, and never applies a config that fails `nginx -t`.

## Keycloak

Halsa uses two realms:
- **`halsa`**: staff and integrations;
- **`halsa-pacientes`**: the patient portal.

The API validates tokens from both: `Keycloak:PersonalAuthority` and `Keycloak:PacientesAuthority` are the issuers.
The integration clients (`int-*`) authenticate with `private_key_jwt`, and their tokens are **bound to the client
certificate** (RFC 8705, `cnf.x5t#S256`). The API rejects a token presented with any other certificate
(`401 Auth.CertificadoNoVinculado`).

### Locally (`docker-compose.yml`)

- **What runs:** `halsa.backend.identity` (Keycloak 26.4, `start-dev`) imports `docker/keycloak/halsa-realm.json`
  and `halsa-pacientes-realm.json` **on its first start only**, with dev users, the `halsa-dev-cli` client and the
  OTP secrets above. The issuer is fixed at `http://localhost:8080` (`KC_HOSTNAME`), which is what
  `appsettings.json` expects.
- **Order:** run `bash docker/nginx-dev/generar-pki.sh` **before** Keycloak's first start. It writes the `int-*`
  certificates to `docker/nginx-dev/pki/keycloak.env`, and the realm import reads them.
- **Re-importing** (after regenerating the PKI or editing the realm files):
  `docker compose rm -sf halsa.backend.identity && docker volume rm halsabackend_halsa.backend-identity`. The volume
  prefix is `halsabackend`: Compose drops the dot from the project name.
- **Certificate-bound tokens:** they need the three `--spi-x509cert-lookup--*` options, already in
  `docker-compose.yml`. Request the token through `https://integraciones.localhost/auth/...`
  (`docker/nginx-dev/token.sh`).
- **Without certificates:** `int-arl-ARL_A-secreto` (secret `int-arl-ARL_A-secreto-secret`) has no `cnf`. Use it
  against `http://localhost:5600` with `Integraciones:ExigirCertificado=false`.
- **Known issue (outside SPEC 13):** `halsa-dev-cli` tokens carry no `sub`. The dev realm doesn't assign Keycloak
  26's `basic` scope, so the API answers `403 Tenant.ClaimsIncompletos` to them.

### Shared instance (production: `https://auth.edcastdev.com`)

Halsa does not deploy or upgrade that Keycloak (26.7, container `keykloack-keycloak-1`); it only adds its realms.
**Its owner** applies these changes once:

1. **Startup options**, global but only used by clients with certificate-bound tokens:
   ```
   --spi-x509cert-lookup--provider=nginx
   --spi-x509cert-lookup--nginx--ssl-client-cert=ssl-client-cert
   --spi-x509cert-lookup--nginx--trust-proxy-verification=true
   ```
   These go in its compose as `command: ["start", "--spi-x509cert-lookup--provider=nginx", ...]`.
2. **A fixed hostname:** `KC_HOSTNAME=https://auth.edcastdev.com` and `KC_PROXY_HEADERS=xforwarded`. The issuer
   must stay the same when Halsa's integrations request a token through `halsa-integraciones.edcastdev.com`.
3. **The `auth.edcastdev.com` proxy host in NPM must clear the certificate headers.** NPM forwards them by
   default (verified), and Keycloak trusts them. A loose `proxy_set_header` at server level does not apply, so the
   **Advanced** field needs this whole block (it replaces NPM's own `location /`):
   ```nginx
   location / {
       proxy_set_header ssl-client-cert "";
       proxy_set_header ssl-client-verify "";
       include conf.d/include/proxy.conf;
   }
   ```
4. **Network:** NPM reaches Keycloak by container name (`HALSA_UPSTREAM_KEYCLOAK`, the same upstream as that proxy
   host).

Then Halsa, on the VPS:

- **Realms:** `deploy/keycloak/importar-realms.sh` creates `halsa` and `halsa-pacientes` from
  `deploy/keycloak/*.prod.json` (no users, no `halsa-dev-cli`, no OTP, no SMTP). It only creates a realm that doesn't
  exist and never touches an existing one. It asks for a `master` admin and takes the BFF's client secret from
  `/etc/halsa/secretos/kc_bff_secret`.
- **SMTP:** set it in `halsa-pacientes` → *Realm settings → Email*. `verifyEmail` is on, so patient
  self-registration does not complete until then.
- **Integration clients:** each ARL or IPS gets a `int-*` client created from its certificate (runbook 06,
  `kcadm.sh`).
- **Configuration:** `.env` holds `HALSA_KEYCLOAK_URL=https://auth.edcastdev.com` (issuers `/realms/halsa` and
  `/realms/halsa-pacientes`) and `HALSA_KEYCLOAK_HOST`. The API and BFF have no internet egress, so they resolve
  that host to NPM on the `borde` network.

## Before production

- [x] Apply migrations from your pipeline, not from `Program` (`Halsa:Rol=Migrador`, SPEC 13).
- [ ] Register a real `IAfiliacionGateway` per ARL: outside `Development` and `Testing` the API refuses to
      start with the dev stub.
- [ ] Point `OpenTelemetry:OtlpEndpoint` at your collector.
- [ ] Turn `TreatWarningsAsErrors` on in `Directory.Build.props` once the solution is warning-clean.
- [x] Set `Keycloak:RequireHttpsMetadata` back to `true` (`deploy/docker-compose.prod.yml`; `ProductionGuard` enforces it).

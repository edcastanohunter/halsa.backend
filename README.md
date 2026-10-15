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

```bash
# -v matters: docker/postgres/init/01-roles.sql (halsa_owner, halsa_app, halsa_clinico, halsa_ingesta,
# halsa_worker) only runs on an empty volume. Without those roles the API fails to authenticate at startup,
# and the SPEC 09 migrations fail on an older volume without halsa_worker.
docker compose down -v && docker compose up -d   # PostgreSQL, Keycloak, Seq (+ Redis if enabled)

# The clinical KEK (SPEC 03): 32 random bytes, in user-secrets and never in appsettings.
# Without it the API refuses to start. Keep it: losing it makes existing clinical data unreadable.
dotnet user-secrets --project src/Halsa.Backend.Api set "Clinico:KekBase64" "$(openssl rand -base64 32)"

dotnet run --project src/Halsa.Backend.Api    # http://localhost:5600/scalar/v1
```

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

1. **A `Validado` case.** The dev seed only has `Radicado` cases: radicate a FURAT with `int-arl-ARL_A`
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

## Before production

- [ ] Apply migrations from your pipeline, not from `Program`.
- [ ] Register a real `IAfiliacionGateway` per ARL: outside `Development` and `Testing` the API refuses to
      start with the dev stub.
- [ ] Point `OpenTelemetry:OtlpEndpoint` at your collector.
- [ ] Turn `TreatWarningsAsErrors` on in `Directory.Build.props` once the solution is warning-clean.
- [ ] Set `Keycloak:RequireHttpsMetadata` back to `true`.

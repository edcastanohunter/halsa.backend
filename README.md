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
tests/
  Halsa.Backend.UnitTests         Domain, mediator and behaviours. xUnit + NSubstitute + Shouldly.
  Halsa.Backend.ArchitectureTests Layer rules enforced with NetArchTest.
  Halsa.Backend.IntegrationTests  Real PostgreSQL via Testcontainers: store, concurrency, outbox.
  Halsa.Backend.FunctionalTests   The API end to end through WebApplicationFactory.
```

The dependency rule is one-way: `Api -> Infrastructure -> Application -> Domain -> SharedKernel`.
`Halsa.Backend.ArchitectureTests` fails the build if anyone breaks it.

## Getting started

```bash
# -v matters: docker/postgres/init/01-roles.sql (halsa_owner, halsa_app, halsa_clinico)
# only runs on an empty volume. Without those roles the API fails to authenticate at startup.
docker compose down -v && docker compose up -d   # PostgreSQL, Keycloak, Seq (+ Redis if enabled)

dotnet run --project src/Halsa.Backend.Api    # http://localhost:5600/scalar/v1
```

On startup the API applies migrations as `halsa_owner` (connection string `Migrations`) and, in
`Development`, loads `Extensions/Seed/casos-dev.sql`: 3 cases for `ARL_A` and 2 for `ARL_B`. At
runtime it connects as `halsa_app` (connection string `Database`), which has no `BYPASSRLS` and no
`DELETE` on cases, so PostgreSQL row-level security isolates tenants even if an EF filter is wrong.

Keycloak comes preloaded with the `halsa` and `halsa-pacientes` realms. Get a token for a dev user
(`analista.arla`, `analista.arlb`, `medico.ips`, ... password `dev`) with the `halsa-dev-cli` client:

```bash
curl -s -X POST http://localhost:8080/realms/halsa/protocol/openid-connect/token \
  -d grant_type=password -d client_id=halsa-dev-cli -d username=analista.arla -d password=dev
```

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
concurrently. `IEventBus` is the seam: swap `LoggingEventBus` for Kafka or Service Bus and nothing
else changes.

## Before production

- [ ] Apply migrations from your pipeline, not from `Program`.
- [ ] Replace `LoggingEventBus` with a real broker.
- [ ] Point `OpenTelemetry:OtlpEndpoint` at your collector.
- [ ] Turn `TreatWarningsAsErrors` on in `Directory.Build.props` once the solution is warning-clean.
- [ ] Set `Keycloak:RequireHttpsMetadata` back to `true`.

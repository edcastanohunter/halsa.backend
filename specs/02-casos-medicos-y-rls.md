# SPEC 02 — Casos médicos con aislamiento por tenant en EF Core y PostgreSQL

> **Estado:** Implementado
> **Depende de:** SPEC 01
> **Fecha:** 2026-10-09
> **Objetivo:** Introducir `CasoMedico`, con sus remisiones a IPS y su equipo asignado, de forma que ningún actor pueda leer ni escribir casos ajenos aunque falle el filtro de EF Core, porque PostgreSQL lo impide con roles y RLS.

---

## Por qué existe esta spec

`CasoMedico` es el primer agregado real y reemplaza al ejemplo `WorkItems`.
Con él nacen dos de las cinco barreras de ARQ 01 (§3.1): el rol de base de datos sin privilegios y la RLS por tenant.

Hoy los tests de integración y los funcionales:

- crean el esquema con `EnsureCreatedAsync()`, que ignora las migraciones, y
- se conectan como `postgres`, que es superusuario y se salta la RLS.

Si no se cambia eso, los tests de esta spec pasarían en verde sin probar nada.
Por eso la spec también cambia cómo se monta la base de datos en desarrollo y en los tests.

---

## Alcance

**Incluido:**

- El agregado `CasoMedico`, con las entidades hijas `Remision` y `Asignacion`, en `Halsa.Backend.Domain/Casos`.
- El esquema `atel` con las tablas `casos_medicos`, `remisiones` y `asignaciones`.
- El script `docker/postgres/init/01-roles.sql`, que crea `halsa_owner`, `halsa_app` y `halsa_clinico`.
- Una migración EF que concede los permisos a `halsa_app`, habilita y fuerza la RLS, y crea las políticas.
- Dos cadenas de conexión: `Database` (que usa `halsa_app`) y `Migrations` (que usa `halsa_owner`).
- `TenantSessionInterceptor`, que fija las variables `app.*` al abrir cada conexión y las limpia al cerrarla.
- El query filter de EF sobre `CasoMedico`.
- El prefijo de rutas `/v1` para todos los endpoints.
- Los endpoints de lectura de casos, de remisiones y de asignaciones.
- Un seed de casos solo para desarrollo.
- El borrado del ejemplo `WorkItems`, conservando la infraestructura de event sourcing.
- Que Testcontainers ejecute el script de roles, aplique las migraciones y se conecte como `halsa_app`.

**Fuera de alcance (specs futuras):**

- Radicar casos por FURAT o FUREP, y el formato definitivo del radicado → SPEC 05.
- Las transiciones de `EstadoCaso` más allá de `Radicado` → las specs que las disparan (05, 07).
- Los permisos de `halsa_clinico` y el esquema `clinico` → SPEC 03. Esta spec solo crea el rol.
- La auditoría de lecturas y escrituras → SPEC 04.
- El ABAC de acceso clínico (`AccesoClinicoHandler`) → SPEC 08. Esta spec solo deja los datos que usa: remisiones y asignaciones.
- Los plazos que abre `CasoRadicado` → SPEC 09.
- Notificar a la IPS cuando se le remite un caso → SPEC 10.
- Los endpoints de paciente (`/v1/paciente/*`) y el vínculo paciente ↔ trabajador → SPEC 12.
- `ITenantConnectionResolver` (una base de datos por ARL) → diferido, sin spec.
- Caché de consultas de casos → no se cachea (ver Decisiones).

---

## Modelo de datos

### Dominio (`Halsa.Backend.Domain.Casos`)

```csharp
public readonly record struct CasoMedicoId(Guid Value);     // UUID v7
public readonly record struct RemisionId(Guid Value);       // UUID v7

public enum TipoEvento { AccidenteTrabajo = 1, EnfermedadLaboral = 2 }
public enum EstadoCaso { Radicado, Validado, EnAtencion, EnCalificacionOrigen, EnCalificacionPcl, Cerrado, Rechazado }

public sealed record Identificacion(string Tipo, string Numero);   // Tipo ∈ CC, CE, TI, PA, PE, PT, NIT
public sealed record ResultadoCalificacion(decimal? PorcentajePcl, DateOnly? FechaEstructuracion, string Origen);

public sealed class CasoMedico : AggregateRoot<CasoMedicoId>
{
    TenantId TenantId; string Radicado; TipoEvento TipoEvento; DateTimeOffset FechaEvento;
    Identificacion Trabajador; Identificacion Empleador; string MunicipioDivipola;
    EstadoCaso Estado; bool EsGraveOMortal; ResultadoCalificacion? Calificacion;
    DateTimeOffset RadicadoEn; uint Version;   // xmin
    IReadOnlyCollection<Remision> Remisiones; IReadOnlyCollection<Asignacion> Asignaciones;

    static Result<CasoMedico> Radicar(TenantId, string radicado, TipoEvento, Identificacion trabajador,
        Identificacion empleador, DateTimeOffset fechaEvento, string municipio, bool graveOMortal, DateTimeOffset ahora);
    Result<RemisionId> Remitir(IpsId ips, string remitidoPor, DateTimeOffset ahora);
    Result RevocarRemision(RemisionId id, string revocadoPor, DateTimeOffset ahora);
    void Asignar(string sub, string asignadoPor, DateTimeOffset ahora);   // idempotente
    Result Desasignar(string sub);
}

public sealed class Remision : Entity<RemisionId>
{
    IpsId IpsId; DateTimeOffset RemitidoEn; string RemitidoPor;
    DateTimeOffset? RevocadaEn; string? RevocadaPor;
    bool Vigente => RevocadaEn is null;
}

public sealed class Asignacion   // clave natural (CasoMedicoId, Sub)
{
    string Sub; DateTimeOffset AsignadoEn; string AsignadoPor;
}
```

Reglas del agregado:

- `Radicar` rechaza una `fechaEvento` futura (`Caso.FechaEventoFutura`) y emite `CasoRadicadoDomainEvent`. En esta spec ese evento no tiene handlers.
- `Remitir` rechaza una segunda remisión vigente a la misma IPS (`Caso.RemisionDuplicada`).
- `RevocarRemision` falla si la remisión no existe (`Caso.RemisionNoEncontrada`) o si ya está revocada (`Caso.RemisionYaRevocada`).
- `Asignar` es idempotente. `Desasignar` falla si el `sub` no está asignado (`Caso.AsignacionNoEncontrada`).
- Las remisiones nunca se borran. Revocar marca `revocada_en`.

### Tablas (esquema `atel`, snake_case)

| Tabla | Columnas | Claves e índices |
| --- | --- | --- |
| `casos_medicos` | `id uuid`, `tenant_id varchar(20)`, `radicado varchar(40)`, `tipo_evento varchar(20)`, `fecha_evento timestamptz`, `trabajador_tipo`, `trabajador_numero`, `empleador_tipo`, `empleador_numero`, `municipio_divipola char(5)`, `estado varchar(30)`, `es_grave_o_mortal bool`, `calificacion_*` (nullable), `radicado_en timestamptz` | PK `id`. Único `(tenant_id, radicado)`. Índice `(tenant_id, estado, radicado_en desc)`. Concurrencia por `xmin`. |
| `remisiones` | `id uuid`, `tenant_id`, `caso_medico_id`, `ips_id varchar(12)`, `remitido_en`, `remitido_por`, `revocada_en` (nullable), `revocada_por` (nullable) | PK `id`. FK a `casos_medicos`. Índice parcial `(ips_id, caso_medico_id) WHERE revocada_en IS NULL`. |
| `asignaciones` | `caso_medico_id`, `sub varchar(64)`, `tenant_id`, `asignado_en`, `asignado_por` | PK `(caso_medico_id, sub)`. FK a `casos_medicos`. |

`tenant_id` se copia en las tablas hijas para que la política de RLS de cada tabla no tenga que hacer un join.

### Roles de PostgreSQL

| Rol | Login | Privilegios |
| --- | --- | --- |
| `halsa_owner` | Sí | Dueño de los esquemas `app`, `atel`, `outbox` y `event_store`. Tiene `CREATE` sobre la base de datos. Las migraciones y el seed corren con él. Sin `BYPASSRLS`. |
| `halsa_app` | Sí | `USAGE` en `app`, `atel`, `outbox` y `event_store`. `SELECT, INSERT, UPDATE` en `casos_medicos` y `remisiones`. `SELECT, INSERT, DELETE` en `asignaciones`. Lo necesario en `outbox` y `event_store`. **Sin `DELETE` sobre casos ni remisiones.** |
| `halsa_clinico` | Sí | Ninguno todavía (SPEC 03). |

Las contraseñas de desarrollo están en el script de init y en `appsettings.Development.json`.
En producción salen de los secretos del despliegue.

### Variables de sesión y políticas

`TenantSessionInterceptor` (un `DbConnectionInterceptor`) hace esto:

- En `ConnectionOpened`, si `ITenantContext.Resuelto`, ejecuta `set_config('app.tenant_id', …, false)`, `set_config('app.ips_id', …, false)`, `set_config('app.actor_tipo', …, false)` y `set_config('app.sub', …, false)` (`app.sub` lo usa la idempotencia de las SPEC 05 a 07).
- En `ConnectionClosing`, deja las cuatro variables en `''`.
- Si el contexto no está resuelto (por ejemplo, en el `OutboxProcessor`), no fija nada, así que la RLS no devuelve filas de `atel`.

```sql
ALTER TABLE atel.casos_medicos ENABLE ROW LEVEL SECURITY;
ALTER TABLE atel.casos_medicos FORCE ROW LEVEL SECURITY;

CREATE POLICY p_casos_lectura ON atel.casos_medicos FOR SELECT USING (
    tenant_id = current_setting('app.tenant_id', true)
    OR EXISTS (SELECT 1 FROM atel.remisiones r
               WHERE r.caso_medico_id = casos_medicos.id
                 AND r.ips_id = current_setting('app.ips_id', true)
                 AND r.revocada_en IS NULL));

CREATE POLICY p_casos_escritura ON atel.casos_medicos FOR INSERT
    WITH CHECK (tenant_id = current_setting('app.tenant_id', true));
-- La política FOR UPDATE es igual, con USING y WITH CHECK por tenant: la IPS nunca escribe el caso.
```

- `remisiones`: lectura si `tenant_id` coincide o si `ips_id` coincide. Escritura solo por tenant. La política no referencia `casos_medicos`, así que no hay recursión.
- `asignaciones`: lectura por tenant o por remisión vigente de la IPS. Escritura y borrado solo por tenant.

### Query filter de EF (`ApplicationDbContext`)

```csharp
modelBuilder.Entity<CasoMedico>().HasQueryFilter(c =>
    (CurrentTenant != null && c.TenantId == CurrentTenant) ||
    (CurrentIps != null && c.Remisiones.Any(r => r.IpsId == CurrentIps && r.RevocadaEn == null)));
```

`CurrentTenant` y `CurrentIps` son propiedades del contexto que leen `ITenantContext`.
Valen `null` si el contexto no está resuelto, así que el filtro también falla cerrado.

### Configuración

```json
"ConnectionStrings": {
  "Database":   "Host=localhost;Database=halsa.backend;Username=halsa_app;Password=…",
  "Migrations": "Host=localhost;Database=halsa.backend;Username=halsa_owner;Password=…"
}
```

### Endpoints (todos bajo `/v1`)

| Método y ruta | Permiso | Actor | Respuesta |
| --- | --- | --- | --- |
| `GET /v1/casos?estado=&page=&pageSize=` | `casos:read` | `Arl`, `Ips` | `200 PagedList<CasoResumenResponse>` |
| `GET /v1/casos/{casoId}` | `casos:read` | `Arl`, `Ips` | `200 CasoDetalleResponse`, o `404` |
| `POST /v1/casos/{casoId}/remisiones` `{ ipsId }` | `casos:write` | `Arl` | `201 { remisionId }`, o `404` / `409` / `400` |
| `POST /v1/casos/{casoId}/remisiones/{remisionId}/revocar` | `casos:write` | `Arl` | `204`, o `404` / `409` |
| `POST /v1/casos/{casoId}/asignaciones` `{ sub }` | `casos:write` | `Arl` | `204` (idempotente), o `404` |
| `DELETE /v1/casos/{casoId}/asignaciones/{sub}` | `casos:write` | `Arl` | `204`, o `404` |

```csharp
public sealed record CasoResumenResponse(
    Guid Id, string Radicado, string TipoEvento, string Estado, DateTimeOffset FechaEvento,
    string TrabajadorDocumento, bool TieneCalificacion, decimal? PorcentajePcl);

public sealed record CasoDetalleResponse(
    CasoResumenResponse Resumen, string EmpleadorDocumento, string MunicipioDivipola, bool EsGraveOMortal,
    IReadOnlyList<RemisionResponse> RemisionesVigentes, IReadOnlyList<string> EquipoAsignado);

public sealed record RemisionResponse(Guid Id, string IpsId, DateTimeOffset RemitidoEn);
```

Reglas de los endpoints:

- Un caso que el actor no puede ver responde `404` y nunca `403`, para no revelar que existe.
- Si un actor que no es `Arl` intenta escribir, el handler devuelve `Caso.ActorNoAutorizado` (`403`), aunque tenga el permiso.
- Las proyecciones son explícitas. Ningún endpoint devuelve la entidad.

> **Ampliado por la SPEC 08:** las asignaciones pasan a exigir `casos:asignar`, admiten actores `Ips` sobre sus casos remitidos y guardan `ips_id`. Se implementa en el paso 1 de esa spec.
>
> **Ampliado por la SPEC 12:** las políticas de lectura ganan una cláusula para el actor `paciente`, por `atel.vinculos_paciente`.

### Seed de desarrollo

El archivo `src/Halsa.Backend.Api/Extensions/Seed/casos-dev.sql` es un recurso embebido.
Se ejecuta con la conexión `Migrations` después de `MigrateAsync()`, solo en `Development`, y es idempotente (`ON CONFLICT DO NOTHING`).
Fija `app.tenant_id` antes del bloque de cada tenant, porque la RLS forzada también aplica al dueño.

| Tenant | Casos | Remisiones vigentes a `110011234501` | Asignados |
| --- | --- | --- | --- |
| `ARL_A` | 3 | 1 caso | `calificador.arla` y `medico.ips` en ese caso |
| `ARL_B` | 2 | 1 caso | `medico.ips` en ese caso |

Los radicados del seed usan el formato de ejemplo del contrato (`ATEL-DEV-2026-000001`).
El formato definitivo lo fija la SPEC 05.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Crear `docker/postgres/init/01-roles.sql` (roles y `GRANT CREATE ON DATABASE` a `halsa_owner`) y montarlo en `docker-compose.yml` en `/docker-entrypoint-initdb.d`. Añadir la cadena `Migrations` y cambiar `Database` a `halsa_app` en `appsettings*.json`. `ApplyMigrationsAsync` pasa a usar la cadena `Migrations`. Prueba manual: `docker compose down -v && docker compose up -d` y luego `dotnet run`, que migra como `halsa_owner`.
2. Cambiar `DatabaseFixture` y `ApiFactory`. Montan el mismo script de init en el contenedor, aplican `MigrateAsync()` con `halsa_owner` (sustituye a `EnsureCreatedAsync()`) y construyen los servicios con `halsa_app`. Los tests actuales siguen en verde.
3. Borrar el ejemplo `WorkItems` en todas las capas: dominio, aplicación, endpoints, proyección, `WorkItemView`, `IReadDbContext.WorkItems`, el evento de integración, `Tags.WorkItems` y los permisos `work-items:*`. También se borran sus tests unitarios, de caché, funcionales y de repositorio. Para conservar la cobertura del event store, se crea el agregado `TestAggregate`, que vive solo en `tests/Halsa.Backend.IntegrationTests`, y `EventTypeRegistry` se registra con un marcador del ensamblado `Domain`. Se crea la migración `RemoveWorkItems`.
4. Añadir `MapGroup("v1")` en `Program.cs` para todos los `IEndpoint`. `/identidad/yo` pasa a ser `/v1/identidad/yo`. Se ajustan los tests funcionales de la SPEC 01 y los `servers` de `docs/api/atel-api.openapi.yaml` siguen siendo válidos (`…/v1`).
5. Crear el dominio de `Domain/Casos`, con los ids, enums, value objects, `CasoMedico`, `Remision`, `Asignacion`, `CasoMedicoErrors` y `CasoRadicadoDomainEvent`. Tests unitarios de cada regla del agregado.
6. Añadir `Schemas.Atel`, las configuraciones EF de las tres tablas (con `xmin` como token de concurrencia) y el query filter. Crear la migración `AtelCasos`, que crea las tablas y además ejecuta (`migrationBuilder.Sql`) los `GRANT` a `halsa_app`, `ENABLE`/`FORCE ROW LEVEL SECURITY` y las políticas. Los `GRANT` sobre `outbox` y `event_store` van en esta misma migración.
7. Crear `TenantSessionInterceptor` en `src/Halsa.Backend.Infrastructure/Database/` y registrarlo en `AddPersistence`. Añadir tests de integración de RLS con `NpgsqlConnection` cruda como `halsa_app`, tal como piden los criterios de aceptación.
8. Crear las consultas `ListCasosQuery` y `GetCasoByIdQuery`, con proyección explícita a los DTO de arriba (sin `ICachedQuery`), y sus endpoints en `src/Halsa.Backend.Api/Endpoints/Casos/`. Añadir `Tags.Casos`. Tests funcionales de lectura por tenant y por IPS.
9. Crear los comandos `RemitirCasoCommand`, `RevocarRemisionCommand`, `AsignarCasoCommand` y `DesasignarCasoCommand`, con sus validadores (`ipsId` de 12 dígitos, `sub` no vacío) y sus endpoints. Un `DbUpdateConcurrencyException` se traduce a `Caso.ConflictoConcurrencia` (`409`). Tests funcionales.
10. Crear el seed `casos-dev.sql` y su ejecución en `Development` después de las migraciones. Prueba manual: con el token de `analista.arlb`, `GET /v1/casos` devuelve 2 casos, y con el de `medico.ips` devuelve 2 (uno de cada ARL).

---

## Criterios de aceptación

- [ ] El repo ya no contiene tipos ni rutas `WorkItem*`, y `dotnet test` pasa completo, incluidos los tests del event store con `TestAggregate`.
- [ ] `DatabaseFixture` y `ApiFactory` ya no llaman a `EnsureCreatedAsync()` y se conectan como `halsa_app`.
- [ ] Como `halsa_app`, con `app.tenant_id='ARL_A'`, `SELECT count(*) FROM atel.casos_medicos WHERE tenant_id='ARL_B'` devuelve `0`.
- [ ] Como `halsa_app`, sin fijar ninguna variable `app.*`, `SELECT count(*) FROM atel.casos_medicos` devuelve `0` aunque existan filas.
- [ ] Como `halsa_app`, con `app.ips_id='110011234501'`, solo se ven los casos con remisión vigente a esa IPS, de ambos tenants. Al revocar la remisión, el caso deja de verse.
- [ ] Como `halsa_app`, con `app.tenant_id='ARL_A'`, un `INSERT` en `atel.casos_medicos` con `tenant_id='ARL_B'` falla por la política (`new row violates row-level security policy`).
- [ ] Como `halsa_app`, `DELETE FROM atel.casos_medicos` falla con `permission denied`.
- [ ] Como `halsa_owner`, sin variables, `SELECT count(*) FROM atel.casos_medicos` devuelve `0` (por `FORCE ROW LEVEL SECURITY`).
- [ ] Un test de integración abre una conexión con el tenant `ARL_A`, la devuelve al pool, la reutiliza sin contexto resuelto y no ve filas.
- [ ] Un test de integración desactiva el query filter (`IgnoreQueryFilters()`) con el contexto de `ARL_A` y sigue sin ver casos de `ARL_B`.
- [ ] `GET /v1/casos` como actor `ARL_A` no incluye ningún caso de `ARL_B`.
- [ ] `GET /v1/casos/{id}` de un caso de `ARL_B`, como actor `ARL_A`, responde `404`.
- [ ] `GET /v1/casos` como actor `Ips` `110011234501` devuelve solo los casos con remisión vigente a esa IPS, incluidos los de los dos tenants.
- [ ] `POST /v1/casos/{id}/remisiones` dos veces a la misma IPS responde `201` y luego `409` con el código `Caso.RemisionDuplicada`.
- [ ] `POST /v1/casos/{id}/remisiones` como actor `Ips`, aunque tenga `casos:write`, responde `403` con `Caso.ActorNoAutorizado`.
- [ ] `POST /v1/casos/{id}/asignaciones` repetido con el mismo `sub` responde `204` las dos veces y deja una sola fila.
- [ ] `GET /v1/identidad/yo` responde `200`, y `GET /identidad/yo` responde `404`.
- [ ] Con `docker compose down -v && docker compose up -d && dotnet run`, el seed carga 5 casos, y una segunda ejecución no los duplica.

---

## Decisiones

- **Sí:** `CasoMedico` se persiste por estado, con `xmin` como token de concurrencia (ARQ §5.1). **No:** event sourcing ahora, porque `event_store` no tiene `tenant_id` y complicaría la RLS.
- **Sí:** se conserva la infraestructura de event sourcing con un agregado que solo existe en los tests. **No:** borrarla, porque ARQ §5.5 deja abierta esa vía para `CasoMedico`.
- **Sí:** los roles se crean en un script de init y los permisos y políticas en las migraciones. **No:** `CREATE ROLE` en la migración, porque exigiría `CREATEROLE` y dejaría contraseñas en el historial.
- **Sí:** las migraciones y el seed corren como `halsa_owner`, y la API como `halsa_app`. Ninguno tiene `BYPASSRLS`.
- **Sí:** las políticas de RLS se basan en `app.tenant_id` y `app.ips_id`. `app.actor_tipo` se fija, pero ninguna política lo usa, porque la presencia de tenant o de IPS ya distingue al actor. **Cambio respecto a ARQ §3.2.**
- **Sí:** `tenant_id` se copia en `remisiones` y `asignaciones`. Así cada política es local y no hay recursión entre políticas.
- **Sí:** el query filter de EF también cubre a la IPS (por remisión vigente). **No:** `ActorEsIps || …` como en ARQ §5.3, porque dejaba la IPS protegida solo por la RLS. Así hay dos barreras.
- **Sí:** las variables de sesión se fijan al abrir la conexión y se limpian al cerrarla (ARQ §3.2). **No:** `SET LOCAL` por transacción, porque las lecturas sin transacción explícita quedarían sin contexto.
- **Sí:** el prefijo `/v1` en la API. Nginx (SPEC 13) recorta `/api`. **Cambio respecto al fragmento de Nginx de ARQ §2.**
- **Sí:** las remisiones vigentes se controlan solo por revocación explícita (`revocada_en`). **No:** `vigente_hasta`, porque cada consulta dependería del reloj.
- **Sí:** las remisiones nunca se borran. Revocar es la única forma de cerrarlas, y deja la trazabilidad.
- **Sí:** `medico-ips` recibe `casos:read` para listar lo que le remitieron (se ajustó la SPEC 01).
- **Sí:** un caso invisible responde `404`, no `403`. No se revela que el caso existe.
- **Sí:** solo un actor `Arl` remite y asigna, y el handler lo verifica además del permiso.
- **No:** caché de consultas de casos. Son datos personales por tenant, y una llave de caché sin tenant sería una fuga entre ARL. Se revisa si aparece un problema de rendimiento.
- **Sí:** un seed en SQL embebido y ejecutado como dueño, con el tenant fijado por bloque. **No:** un seeder en C# que necesitaría un contexto de sistema, que es materia de la SPEC 05.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| El volumen existente de PostgreSQL no ejecuta el script de init, porque solo corre sobre un volumen vacío. | El README y el paso 1 exigen `docker compose down -v`. Si `halsa_app` no existe, la API falla al arrancar con un error explícito de autenticación. |
| Una política de RLS mal escrita deja pasar filas y los tests no lo detectan porque corren como superusuario. | Los fixtures se conectan como `halsa_app`, y hay un criterio que verifica que `halsa_owner` tampoco ve filas sin contexto. |
| La subconsulta `EXISTS` de la política de la IPS es lenta con volumen. | Hay un índice parcial `(ips_id, caso_medico_id) WHERE revocada_en IS NULL`. El plan se revisa con `EXPLAIN` en el test de integración, solo de forma informativa. |
| Una conexión del pool conserva el tenant anterior. | El interceptor la limpia en `ConnectionClosing`, Npgsql hace `DISCARD ALL` al devolverla al pool, y hay un test de reutilización. |
| `OutboxProcessor` u otro proceso en background consulta `atel` sin contexto. | No ve filas (falla cerrado). El contexto de sistema llega en la SPEC 05. |
| Un analista asigna un `sub` que no existe en Keycloak. | Se acepta en esta spec: la asignación no valida contra Keycloak. Si hace falta validarla, se resuelve cuando la SPEC 08 consuma las asignaciones. |

---

## Qué **no** está en esta spec

- La radicación por FURAT/FUREP y el formato definitivo del radicado (SPEC 05).
- El esquema `clinico` y los permisos de `halsa_clinico` (SPEC 03).
- La auditoría (SPEC 04).
- El ABAC clínico y el step-up (SPEC 08).
- El contexto de sistema para procesos en background (SPEC 05) y los plazos (SPEC 09).
- La notificación de remisiones (SPEC 10).
- Los endpoints de paciente (SPEC 12).
- Una base de datos por ARL.

Cada uno, si se aborda, va en su propia spec.

# SPEC 01 — Contexto de tenant e identidad multi-realm

> **Estado:** Borrador
> **Depende de:** ARQ 01 (`docs/arquitectura/01-arquitectura-backend-atel.md`, insumo, no fuente de verdad)
> **Fecha:** 2026-10-09
> **Objetivo:** Que cada request autenticado llegue a la aplicación con un `ITenantContext` confiable (actor, tenant e IPS), derivado solo de tokens de los realms `halsa` y `halsa-pacientes`.

---

## Por qué existe esta spec

Todas las barreras de ARQ 01 dependen de saber **quién** pide y **para qué tenant**: el query filter de EF, la RLS, el ABAC y la auditoría.
Hoy la API conoce un solo realm (`halsa.backend`) y unos roles de ejemplo (`work-items:*`), y no tiene noción de tenant.
Esta spec construye esa base y nada más.
No toca la base de datos.

La corrección de `CurrentUser` (ARQ §6.1) ya está hecha: lee `sub` y `email` en crudo.
No forma parte de esta spec.

---

## Alcance

**Incluido:**

- Tipos `TenantId`, `IpsId` y `ActorTipo` en `Halsa.Backend.SharedKernel/Tenancy`.
- La interfaz `ITenantContext` en `Halsa.Backend.Application/Abstractions/Tenancy`.
- El catálogo completo de permisos de ARQ §6.3 en `Permissions.cs`.
- El realm de desarrollo `halsa`, con clients, roles compuestos, mappers de atributos y usuarios semilla.
- El realm de desarrollo `halsa-pacientes`, con un usuario semilla.
- La eliminación de `docker/keycloak/cleanarch-realm.json`.
- Dos esquemas JWT (`Personal` y `Paciente`) y un esquema selector que elige por el `iss` del token.
- `TenantContextResolver`, que convierte un principal validado en contexto o en error.
- `HttpTenantContext` (la implementación de `ITenantContext`) y `TenantContextMiddleware` (que responde `403` si faltan claims).
- El endpoint de diagnóstico `GET /identidad/yo`.
- La adaptación de `TestAuthenticationHandler` para que cada test elija sus claims.

**Fuera de alcance (specs futuras):**

- Query filters de EF, RLS, roles de PostgreSQL y `DbConnectionInterceptor` → SPEC 02.
- Borrar el ejemplo `WorkItems` → SPEC 02.
- El prefijo de rutas `/v1` → SPEC 02, con el primer endpoint ATEL.
- El contexto de tenant para procesos sin HTTP (Worker, jobs) → SPEC 05, que lo necesita para la validación asíncrona. Esta spec solo reserva el valor `ActorTipo.Sistema`.
- MFA, mapeo ACR→LoA y step-up genérico → SPEC 07. `clinico:emergencia` en uso → SPEC 08.
- Keycloak Organizations → decisión de producción, sin spec asignada.
- `private_key_jwt` y tokens ligados a certificado para las integraciones → SPEC 13.
- El client `halsa-bff-paciente` y los endpoints `/paciente/*` → SPEC 12.

---

## Modelo de datos

Esta spec no crea tablas.
Introduce estos tipos:

```csharp
namespace Halsa.Backend.SharedKernel.Tenancy;

/// <summary>Código de la ARL. Es el tenant. Formato: ^[A-Z0-9_]{2,20}$ (ej. "ARL_A").</summary>
public readonly record struct TenantId(string Value);

/// <summary>Código de habilitación REPS de la IPS. Formato: ^[0-9]{12}$.</summary>
public readonly record struct IpsId(string Value);

public enum ActorTipo { Arl = 1, Ips = 2, Paciente = 3, Integracion = 4, Sistema = 5 }
```

```csharp
namespace Halsa.Backend.Application.Abstractions.Tenancy;

public interface ITenantContext
{
    /// <summary>false fuera de un request autenticado. Las demás propiedades lanzan si es false.</summary>
    bool Resuelto { get; }
    ActorTipo Tipo { get; }
    string Sub { get; }
    TenantId? TenantId { get; }   // null para Ips (actor transversal) y Paciente
    IpsId? IpsId { get; }         // solo para Ips e Integracion de IPS
}
```

### Reglas de resolución (`TenantContextResolver`)

| Emisor (`iss`) | `actor_tipo` del token | Requisitos | Resultado |
| --- | --- | --- | --- |
| realm `halsa-pacientes` | se ignora | `sub` | `Paciente`, sin tenant ni IPS |
| realm `halsa` | `arl` | `tenant_id` válido | `Arl` |
| realm `halsa` | `ips` | `ips_id` válido | `Ips`, sin tenant |
| realm `halsa` | `integracion` | exactamente uno: `tenant_id` o `ips_id` | `Integracion` |
| realm `halsa` | ausente, `paciente`, `sistema` u otro valor | — | error `Tenant.ActorTipoInvalido` |
| realm `halsa` | válido, pero faltan claims o tienen mal formato | — | error `Tenant.ClaimsIncompletos` |

Los dos errores son `Error.Forbidden` y el middleware los traduce a `403` con ProblemDetails.
El tipo de actor de un paciente sale del **emisor**, nunca de un claim.
Así, un token de personal no puede hacerse pasar por paciente, ni al revés.

### Configuración (`appsettings.json`)

```json
"Keycloak": {
  "PersonalAuthority": "http://localhost:8080/realms/halsa",
  "PacientesAuthority": "http://localhost:8080/realms/halsa-pacientes",
  "Audience": "halsa-api",
  "ClientId": "halsa-api",
  "RequireHttpsMetadata": false
}
```

`KeycloakOptions.Authority` se reemplaza por `PersonalAuthority` y `PacientesAuthority`.

### Catálogo de permisos (client roles de `halsa-api`)

Permisos atómicos, como constantes en `src/Halsa.Backend.Infrastructure/Authorization/Permissions.cs`:
`casos:read`, `casos:write`, `incapacidades:read`, `incapacidades:write`, `plazos:read`, `clinico:read`, `clinico:write`, `clinico:emergencia`, `pcl:dictamen:read`, `pcl:dictamen:write`, `auditoria:read`, `auditoria:export`, `integracion:furat:write`, `integracion:furep:write`, `integracion:clinico:write`, `webhooks:manage`.
`work-items:read` y `work-items:write` se conservan hasta la SPEC 02.

Roles compuestos (igual que ARQ §6.3):

| Rol compuesto | Permisos |
| --- | --- |
| `analista-arl` | `casos:read`, `casos:write`, `incapacidades:read`, `plazos:read` |
| `medico-laboral` | `analista-arl` + `clinico:read`, `clinico:write`, `incapacidades:write` |
| `medico-calificador` | `medico-laboral` + `pcl:dictamen:read`, `pcl:dictamen:write` |
| `medico-ips` | `casos:read`, `clinico:read`, `clinico:write`, `incapacidades:write` |
| `oficial-privacidad` | `auditoria:read`, `auditoria:export` |
| `integracion-arl` | `integracion:furat:write`, `integracion:furep:write`, `webhooks:manage` |
| `integracion-ips` | `integracion:clinico:write`, `incapacidades:write` |

`medico-ips` incluye `casos:read` (a diferencia de ARQ §6.3) para poder listar los casos que le remitieron (decisión de la SPEC 02).
> **Ampliado por la SPEC 08:** los permisos `casos:asignar` y `auditoria:revisar`, el compuesto `coordinador-ips`, el usuario `coordinador.ips` y `clinico:emergencia` para `calificador.arla` en desarrollo. Se implementan en el paso 1 de esa spec.
>
> **Ampliado por la SPEC 09:** el permiso `plazos:write` en `analista-arl`.
>
> **Ampliado por la SPEC 10:** el permiso `webhooks:manage` en `integracion-ips`.
>
> **Ampliado por la SPEC 12:** los permisos `pacientes:activar` (en `analista-arl` e `integracion-arl`) y `solicitudes-hc:gestionar` (en `oficial-privacidad`), y en `halsa-pacientes` el client `halsa-bff-paciente` y el autorregistro.

`clinico:emergencia` no entra en ningún compuesto.
Se asigna usuario por usuario (SPEC 08).

### Realms de desarrollo

Archivos: `docker/keycloak/halsa-realm.json` y `docker/keycloak/halsa-pacientes-realm.json`.
`docker-compose.yml` ya importa todo el directorio, así que no cambia.

**Realm `halsa`** (sin autorregistro; en desarrollo solo pide contraseña):

| Client | Tipo | Notas |
| --- | --- | --- |
| `halsa-api` | Bearer-only | Dueño de los client roles de la tabla anterior. |
| `halsa-backoffice` | Público, Authorization Code + PKCE | Mapper de audiencia `halsa-api`. Access token de 5 min. |
| `halsa-dev-cli` | Público, *direct access grants* | **Solo desarrollo**, para sacar tokens con `curl`. Mapper de audiencia `halsa-api`. |
| `int-arl-ARL_A` | Confidencial, service account, `client_secret` | Mappers *hardcoded claim* `actor_tipo=integracion` y `tenant_id=ARL_A`. Rol `integracion-arl`. |
| `int-ips-110011234501` | Confidencial, service account, `client_secret` | Mappers `actor_tipo=integracion` e `ips_id=110011234501`. Rol `integracion-ips`. |

Mappers de alcance de realm (*User Attribute* → claim plano en el access token): `tenant_id`, `ips_id` y `actor_tipo`.

Usuarios semilla (contraseña `dev`). El `id` se fija en el JSON del realm, así que el `sub` es estable y el seed de la SPEC 02 lo puede referenciar:

| Usuario | `id` (`sub`) | `actor_tipo` | `tenant_id` | `ips_id` | Rol |
| --- | --- | --- | --- | --- | --- |
| `analista.arla` | `01999000-0000-7000-8000-000000000a01` | `arl` | `ARL_A` | — | `analista-arl` |
| `calificador.arla` | `01999000-0000-7000-8000-000000000a02` | `arl` | `ARL_A` | — | `medico-calificador` |
| `privacidad.arla` | `01999000-0000-7000-8000-000000000a03` | `arl` | `ARL_A` | — | `oficial-privacidad` |
| `analista.arlb` | `01999000-0000-7000-8000-000000000b01` | `arl` | `ARL_B` | — | `analista-arl` |
| `medico.ips` | `01999000-0000-7000-8000-000000000c01` | `ips` | — | `110011234501` | `medico-ips` |

El código REPS `110011234501` es ficticio.

**Realm `halsa-pacientes`** (en desarrollo, sin autorregistro):

- El client `halsa-api` (bearer-only) y el client `halsa-dev-cli` (con mapper de audiencia `halsa-api`).
- El usuario `paciente.dev` (contraseña `dev`, `id` `01999000-0000-7000-8000-000000000d01`), sin roles de `halsa-api`.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Crear `TenantId`, `IpsId` y `ActorTipo` en `src/Halsa.Backend.SharedKernel/Tenancy/`. Cada uno tiene un `TryCreate` que valida el formato. Tests unitarios de formato válido e inválido en `tests/Halsa.Backend.UnitTests/Tenancy/`.
2. Crear `ITenantContext` en `src/Halsa.Backend.Application/Abstractions/Tenancy/` y ampliar `Permissions.cs` con el catálogo completo.
3. Crear `docker/keycloak/halsa-realm.json` y `docker/keycloak/halsa-pacientes-realm.json`, y borrar `cleanarch-realm.json`. Añadir el test unitario `RealmCatalogTests`, que lee `halsa-realm.json` y verifica que `analista-arl` no tiene, ni de forma transitiva, ningún permiso `clinico:*`, `pcl:*` ni `auditoria:*`. Prueba manual: `docker compose down -v && docker compose up -d` y luego obtener un token de `analista.arla` con `halsa-dev-cli`.
4. Reemplazar `KeycloakOptions.Authority` por `PersonalAuthority` y `PacientesAuthority`. En `AuthenticationInstaller`, registrar los esquemas JwtBearer `Personal` y `Paciente` (uno por emisor, misma audiencia) y un *policy scheme* por defecto (`AuthSchemes.Bearer`). Ese esquema lee el `iss` del token sin validar y le reenvía el request al esquema correspondiente. Un emisor desconocido se reenvía a `Personal`, que lo rechaza con `401`. Actualizar `appsettings.json` y el README (cómo pedir el token).
5. Crear `TenantContextResolver` en `src/Halsa.Backend.Infrastructure/Tenancy/`. Recibe un `ClaimsPrincipal` y los emisores configurados, y devuelve `Result<TenantContextData>` según la tabla de reglas. Tests unitarios: uno por fila de la tabla, más un token de paciente con un `actor_tipo=arl` inyectado que debe resolver como `Paciente`.
6. Crear `HttpTenantContext : ITenantContext` (scoped). Lee el resultado que deja el middleware en `HttpContext.Items`. `Resuelto` es `false` si no hay contexto, y las demás propiedades lanzan `InvalidOperationException` en ese caso. Crear `TenantContextMiddleware` en `src/Halsa.Backend.Api/Middleware/`: si el usuario está autenticado, resuelve el contexto, y si falla responde `403` vía `CustomResults.Problem`. Con un usuario anónimo, deja pasar. Registrarlo en `Program.cs` entre `UseAuthentication()` y `UseAuthorization()`.
7. Adaptar `TestAuthenticationHandler`. Por defecto emite `iss` del realm `halsa`, `actor_tipo=arl` y `tenant_id=ARL_A`, más los permisos actuales, para que los tests de `WorkItems` sigan pasando. Si el request trae el header `X-Test-Claims` (un JSON con un diccionario de claims), lo usa en su lugar. Ese header solo existe en el proyecto de tests.
8. Crear el endpoint `GET /identidad/yo` en `src/Halsa.Backend.Api/Endpoints/Identidad/Yo.cs`. Exige un usuario autenticado, no pide ningún permiso, y devuelve `{ sub, actorTipo, tenantId, ipsId, permisos }` sacado de `ITenantContext` e `ICurrentUser`. Añadir los tests funcionales de los criterios de aceptación.

---

## Criterios de aceptación

- [ ] `docker/keycloak/cleanarch-realm.json` ya no existe, y después de `docker compose down -v && docker compose up -d` Keycloak expone los realms `halsa` y `halsa-pacientes`.
- [ ] El access token de `analista.arla` (obtenido con `halsa-dev-cli`) trae `tenant_id=ARL_A`, `actor_tipo=arl` y `aud` con `halsa-api`, y en `resource_access.halsa-api.roles` incluye `casos:read`.
- [ ] El token de `analista.arla` no incluye ningún rol `clinico:*` ni `pcl:*` en `resource_access.halsa-api.roles`.
- [ ] `RealmCatalogTests` falla si se añade `clinico:read` al compuesto `analista-arl` en `halsa-realm.json`.
- [ ] `GET /identidad/yo` con el token real de `analista.arla` responde `200` con `actorTipo="arl"` y `tenantId="ARL_A"`.
- [ ] `GET /identidad/yo` con el token real de `paciente.dev` (realm `halsa-pacientes`) responde `200` con `actorTipo="paciente"` y `tenantId=null`.
- [ ] `GET /identidad/yo` con el token de service account de `int-arl-ARL_A` responde `200` con `actorTipo="integracion"` y `tenantId="ARL_A"`.
- [ ] `GET /identidad/yo` con un token firmado por un emisor que no es ninguno de los dos realms responde `401`.
- [ ] Test funcional: un principal de personal con `actor_tipo=arl` y sin `tenant_id` recibe `403`, y el ProblemDetails trae el código `Tenant.ClaimsIncompletos`.
- [ ] Test funcional: un principal de personal con `actor_tipo=paciente` recibe `403` con el código `Tenant.ActorTipoInvalido`.
- [ ] Test funcional: un principal de personal con `actor_tipo=ips` e `ips_id` de 12 dígitos recibe `200` con `tenantId=null` e `ipsId` informado.
- [ ] `GET /health` sin token responde `200` (el middleware no bloquea a los anónimos).
- [ ] Los tests existentes de `WorkItems` siguen en verde sin cambiar sus aserciones.
- [ ] `dotnet build` no añade advertencias nuevas y `dotnet test` pasa completo.

---

## Decisiones

- **Sí:** atributos de usuario + mappers *User Attribute* para `tenant_id`, `ips_id` y `actor_tipo` en desarrollo. Los claims llegan planos y el resolver es trivial.
- **Diferido:** Keycloak Organizations (ARQ §4.1). Es el modelo de producción, pero el claim anidado `organization` complica el resolver. Cuando se adopte, solo cambia `TenantContextResolver`.
- **Sí:** dos realms (`halsa` y `halsa-pacientes`) desde esta spec. El esquema `Paciente` existe desde el inicio para que `ActorTipo.Paciente` nunca se derive de un claim.
- **Sí:** un *policy scheme* que elige por `iss`. **No:** registrar los dos esquemas en la política por defecto, porque cada request se validaría dos veces y quedaría un fallo de autenticación en el log.
- **Sí:** `403` con ProblemDetails cuando el token es válido pero le faltan claims. **No:** `401`, porque el cliente reintentaría el login sin ningún efecto.
- **Sí:** `TenantId`, `IpsId` y `ActorTipo` en `SharedKernel/Tenancy`. **No:** en `Domain.Casos` (como en ARQ §5.1), porque acoplaría `Domain.Clinico` a `Domain.Casos`.
- **Sí:** `ITenantContext` se llena solo desde claims de un token validado. **No:** desde headers ni desde la URL (ARQ §3.2).
- **Sí:** el catálogo completo de permisos de una vez, con un test sobre el JSON del realm que protege el secreto profesional (Ley 23/1981, art. 37) desde el primer día.
- **Sí:** `clinico:emergencia` existe como permiso pero queda fuera de todos los compuestos. Se otorga individualmente.
- **Diferido:** el contexto para procesos sin HTTP (SPEC 05). Solo se reserva `ActorTipo.Sistema`, que el resolver rechaza si llega en un token.
- **Sí:** el client `halsa-dev-cli` con *direct access grants*, solo en los JSON de desarrollo. **No:** habilitar el password grant en `halsa-backoffice`.
- **Sí:** las service accounts de integración usan `client_secret` en desarrollo. `private_key_jwt` y mTLS llegan con la SPEC 13.
- **Sí:** el endpoint `GET /identidad/yo`. Es la única forma de verificar el contexto de punta a punta antes de que exista un endpoint ATEL, y el frontend lo necesitará para pintar la sesión. Solo expone datos del propio actor.
- **Sí:** el header `X-Test-Claims` solo en `TestAuthenticationHandler`. **No:** cualquier mecanismo de sobrescribir claims en el código de producción.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| El volumen `halsa.backend-identity` conserva el realm viejo y la importación salta los realms existentes. | El README y el paso 3 indican `docker compose down -v`. El realm viejo no estorba, porque la API ya no lo acepta como emisor. |
| Desfase de `iss` si la API corre en un contenedor y llega a Keycloak por otro hostname. | Hoy la API corre en el host contra `localhost:8080`. Si se contenedoriza, se fija `KC_HOSTNAME` (anotado para la SPEC 13). |
| Un desarrollador consulta `ITenantContext` en un endpoint anónimo. | `HttpTenantContext` lanza una excepción en lugar de devolver un contexto vacío, así que falla de forma ruidosa y cerrada. |
| Un atributo de usuario mal escrito en Keycloak (ej. `tenant_id=arl_a`) deja al usuario sin acceso. | El resolver valida el formato y responde `Tenant.ClaimsIncompletos`, con un log que nombra el claim inválido. |

---

## Qué **no** está en esta spec

- RLS, query filters y roles de PostgreSQL (SPEC 02).
- El borrado de `WorkItems` y el prefijo `/v1` (SPEC 02).
- El contexto de tenant de sistema (SPEC 05).
- MFA y step-up (SPEC 07), y break-the-glass (SPEC 08).
- Keycloak Organizations.
- mTLS y `private_key_jwt` (SPEC 13).
- El BFF del paciente (SPEC 12).

Cada uno, si se aborda, va en su propia spec.

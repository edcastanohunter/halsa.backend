# SPEC 04 — Auditoría inmutable de accesos

> **Estado:** Implementado
> **Depende de:** SPEC 01, SPEC 02, SPEC 03
> **Fecha:** 2026-10-09
> **Objetivo:** Registrar en `auditoria.accesos` cada lectura y escritura clínica, cada escritura de casos y cada lectura del detalle de un caso, incluidos los intentos denegados, en una tabla que nadie puede modificar y que el oficial de privacidad consulta por la API.

---

## Por qué existe esta spec

La SFC, la Supersalud y la SIC pueden pedir quién vio qué expediente y cuándo (ARQ §0 y §3.3).
Un acceso clínico sin traza es un incumplimiento, aunque haya sido legítimo.
Por eso esta spec va **antes** de cualquier endpoint clínico.

Tres reglas la gobiernan:

1. **Las escrituras se auditan en la misma transacción** que el cambio. Si la auditoría falla, el cambio se revierte.
2. **Las lecturas fallan cerradas.** Si no se puede registrar la lectura, no se entregan los datos.
3. **Los intentos denegados también se registran**, porque son la mejor señal de fisgoneo.

El sellado Merkle y la exportación firmada son de la SPEC 11.

---

## Alcance

**Incluido:**

- El esquema `auditoria`, con la tabla `accesos` particionada por mes, inmutable, con RLS de lectura por tenant y un hash por registro.
- La función `auditoria.asegurar_particiones(meses_adelante)` y su llamada después de migrar.
- `IAuditTrail` (escrituras administrativas en `ApplicationDbContext`) e `IClinicalAuditTrail` (escrituras clínicas en `ClinicalDbContext`).
- `IAuditedQuery` y `AuditedQueryBehavior`, que auditan las lecturas y fallan cerrados.
- `IAuditScope`, para que el handler informe el tenant del recurso.
- El registro de los intentos denegados de autorización en rutas marcadas con `.Auditable(...)`.
- La auditoría de los comandos existentes (remisiones, asignaciones y los dos comandos clínicos) y de `GET /v1/casos/{id}` y `GetRegistrosClinicosQuery`.
- El endpoint `GET /v1/auditoria/accesos` para el oficial de privacidad.
- Un test de arquitectura que obliga a los handlers de comandos clínicos a depender de `IClinicalAuditTrail`.

**Fuera de alcance (specs futuras):**

- El sellado Merkle por lotes, la exportación JSON Lines firmada y el almacenamiento WORM → SPEC 11.
- El job diario que llama a `asegurar_particiones` → SPEC 09 (hasta entonces se llama al migrar).
- El archivado de particiones antiguas en almacenamiento frío → sin spec asignada.
- El motivo de step-up y break-the-glass en la auditoría → SPEC 08. Esta spec deja la columna `motivo`.
- La notificación en tiempo real al oficial de privacidad → SPEC 08 y SPEC 10.
- La auditoría de los listados registro por registro → descartada (ver Decisiones).
- La configuración de cabeceras reenviadas (`X-Forwarded-For`) → SPEC 13. Hasta entonces, `ip` es la IP de la conexión.

---

## Modelo de datos

### Tabla `auditoria.accesos`

Particionada por `RANGE (ocurrido_en)`, una partición por mes (`accesos_2026_10`…), más `accesos_default`.

| Columna | Tipo | Contenido |
| --- | --- | --- |
| `id` | `uuid` | UUID v7. La PK es `(id, ocurrido_en)` porque la partición lo exige. |
| `ocurrido_en` | `timestamptz` | Del `IClock` del servidor |
| `tenant_id` | `varchar(20)` null | Tenant **del recurso**, no del actor. Es `null` si no se conoce (por ejemplo, un denegado antes del handler). |
| `actor_id` | `varchar(64)` | `sub` |
| `actor_tipo` | `varchar(20)` | `arl`, `ips`, `paciente` o `integracion` |
| `actor_tenant_id`, `actor_ips_id` | null | Contexto del actor (SPEC 01) |
| `actor_roles` | `text[]` | Permisos efectivos en el momento del acceso |
| `client_id` | `varchar(100)` | Claim `azp` |
| `ip` | `inet` | IP remota |
| `user_agent` | `varchar(300)` | Truncado |
| `correlation_id` | `varchar(64)` | El mismo que usa `RequestContextLoggingMiddleware` |
| `accion` | `varchar(20)` | `LEER`, `CREAR`, `ACTUALIZAR`, `ELIMINAR`, `CORREGIR`, `EXPORTAR` |
| `resultado` | `varchar(20)` | `EXITO`, `DENEGADO`, `NO_ENCONTRADO`, `ERROR` |
| `recurso_tipo` | `varchar(30)` | `CASO`, `REMISION`, `ASIGNACION`, `REGISTRO_CLINICO`, `AUDITORIA`, `INCAPACIDAD`, `SOPORTE` |
| `recurso_id` | `varchar(64)` null | |
| `caso_medico_id` | `uuid` null | |
| `motivo` | `varchar(500)` null | Lo usa la SPEC 08 (break-the-glass) |
| `acr` | `varchar(10)` null | |
| `hash_registro` | `bytea` | SHA-256 de la forma canónica JCS (RFC 8785) de todas las columnas anteriores |

Índices: `(tenant_id, caso_medico_id, ocurrido_en)` y `(tenant_id, actor_id, ocurrido_en)`.

### Tipos en la aplicación (`Halsa.Backend.Application.Abstractions.Auditoria`)

```csharp
public enum AccionAuditoria { Leer, Crear, Actualizar, Eliminar, Corregir, Exportar }
public enum ResultadoAuditoria { Exito, Denegado, NoEncontrado, Error }
public enum RecursoAuditoria { Caso, Remision, Asignacion, RegistroClinico, Auditoria, Incapacidad, Soporte, Plazo }   // Incapacidad y Soporte: SPEC 07. Plazo: SPEC 09

public sealed record ObjetivoAuditoria(RecursoAuditoria Tipo, string? RecursoId, Guid? CasoMedicoId);

public interface IAuditedQuery { AccionAuditoria Accion { get; } ObjetivoAuditoria Objetivo { get; } }
// IClinicalQuery (SPEC 03) pasa a heredar de IAuditedQuery.

public interface IAuditScope { void TenantDelRecurso(TenantId tenant); }   // el handler informa el tenant del recurso

public interface IAuditTrail         { void Registrar(AccionAuditoria, ObjetivoAuditoria, TenantId?, ResultadoAuditoria = Exito); }
public interface IClinicalAuditTrail { void Registrar(AccionAuditoria, ObjetivoAuditoria, TenantId,  ResultadoAuditoria = Exito); }
```

- `IAuditTrail.Registrar` agrega la fila al *change tracker* de `ApplicationDbContext`, así que se confirma en el mismo `SaveChangesAsync` que el cambio.
- `IClinicalAuditTrail.Registrar` hace lo mismo con `ClinicalDbContext`.
- La entidad `AccesoAuditado` y su configuración viven en `Infrastructure/Database/Auditoria/`. Ambos contextos la mapean, y `ClinicalDbContext` la excluye de sus migraciones.
- Los datos del actor salen de `IAuditActorProvider` (`ITenantContext`, `ICurrentUser` y `HttpContext`).

### Comportamiento de las lecturas (`AuditedQueryBehavior`)

1. Ejecuta el handler.
2. Calcula el `resultado`: un `Result` exitoso da `EXITO`, `ErrorType.NotFound` da `NO_ENCONTRADO`, `ErrorType.Forbidden` da `DENEGADO`, y cualquier otro error da `ERROR`.
3. Inserta la fila en un `ApplicationDbContext` de un *scope* propio (rol `halsa_app`) y lo confirma.
4. Si el insert falla, descarta la respuesta y devuelve `Auditoria.NoRegistrada` (`ErrorType.Failure`, `500`). Si no, devuelve la respuesta.

### Intentos denegados en autorización

`AuditingAuthorizationResultHandler` implementa `IAuthorizationMiddlewareResultHandler` y envuelve al handler por defecto.
Cuando el resultado es *forbidden* o *challenge* en una ruta con metadata `AuditableAttribute(RecursoAuditoria)`, registra `resultado=DENEGADO` con el `casoId` de la ruta y `tenant_id=null`.
La extensión `.Auditable(RecursoAuditoria.Caso)` se aplica a todos los endpoints de `/v1/casos`.

### Permisos, RLS e inmutabilidad

| Rol | `auditoria.accesos` |
| --- | --- |
| `halsa_app` | `INSERT, SELECT` |
| `halsa_clinico` | `INSERT` |
| `halsa_owner` | Dueño, sujeto a `FORCE ROW LEVEL SECURITY` y a los triggers |

```sql
CREATE POLICY p_aud_insert ON auditoria.accesos FOR INSERT WITH CHECK (true);
CREATE POLICY p_aud_select ON auditoria.accesos FOR SELECT
    USING (tenant_id = current_setting('app.tenant_id', true));
-- Triggers BEFORE UPDATE OR DELETE (por fila) y BEFORE TRUNCATE (por sentencia): RAISE EXCEPTION.
```

### Endpoint de consulta

`GET /v1/auditoria/accesos?casoId=&actorId=&accion=&resultado=&desde=&hasta=&page=&pageSize=`

- Exige `auditoria:read`, solo responde a un actor `Arl`, y la RLS lo limita a su tenant.
- `desde` y `hasta` son obligatorios, con un rango máximo de 31 días.
- Responde `200 PagedList<AccesoAuditadoResponse>`, que contiene todas las columnas excepto `hash_registro`.
- La consulta se audita a sí misma (`LEER` sobre `AUDITORIA`).

### Qué se audita

| Origen | Acción | Recurso | Vía |
| --- | --- | --- | --- |
| `GET /v1/casos/{id}` (`GetCasoByIdQuery`) | `LEER` | `CASO` | `IAuditedQuery` |
| `GetRegistrosClinicosQuery` | `LEER` | `REGISTRO_CLINICO` | `IClinicalQuery` |
| `RemitirCasoCommand` | `CREAR` | `REMISION` | `IAuditTrail` |
| `RevocarRemisionCommand` | `ACTUALIZAR` | `REMISION` | `IAuditTrail` |
| `AsignarCasoCommand` | `CREAR` | `ASIGNACION` | `IAuditTrail` (también cuando la asignación ya existía) |
| `DesasignarCasoCommand` | `ELIMINAR` | `ASIGNACION` | `IAuditTrail` |
| `RegistrarRegistroClinicoCommand` | `CREAR` | `REGISTRO_CLINICO` | `IClinicalAuditTrail` |
| `CorregirRegistroClinicoCommand` | `CORREGIR` | `REGISTRO_CLINICO` | `IClinicalAuditTrail` |
| `GET /v1/auditoria/accesos` | `LEER` | `AUDITORIA` | `IAuditedQuery` |
| `403` de una política en una ruta `.Auditable` | La de la ruta | La de la ruta | `AuditingAuthorizationResultHandler` |

`GET /v1/casos` (el listado) **no** se audita.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Crear los tipos e interfaces de `Application/Abstractions/Auditoria`. Hacer que `IClinicalQuery` herede de `IAuditedQuery`, e implementar `Accion` y `Objetivo` en `GetRegistrosClinicosQuery`.
2. Crear la migración `AuditoriaAccesos` en `ApplicationDbContext`: esquema, tabla particionada, partición `DEFAULT`, índices, `asegurar_particiones`, triggers, `GRANT`s y RLS. `ApplyMigrationsAsync` y los fixtures llaman a `SELECT auditoria.asegurar_particiones(3)` después de migrar. Tests de integración de inmutabilidad y de particiones.
3. Crear `CanonicalAuditHasher` (JCS restringido a la forma fija del registro: cadenas, enteros, nulos y arreglos de cadenas) y `HttpAuditActorProvider`. Tests unitarios: un hash *golden* fijo, y el orden de las propiedades no altera el hash.
4. Crear `AccesoAuditado`, su configuración compartida, `AuditTrail` y `ClinicalAuditTrail`. Usarlos en los 4 comandos administrativos y en los 2 clínicos, informando el tenant del recurso. Tests de integración: la escritura y su fila de auditoría se confirman juntas, y si el insert de auditoría falla (simulado con una restricción) el cambio no queda.
5. Crear `AuditScope` y `AuditedQueryBehavior`, y registrarlo después de `ValidationBehavior`. `GetCasoByIdQuery` implementa `IAuditedQuery` y su handler informa el tenant. Test: con un escritor de auditoría que lanza, la consulta devuelve `Auditoria.NoRegistrada` y no devuelve datos.
6. Crear `AuditableAttribute`, la extensión `.Auditable(...)` y `AuditingAuthorizationResultHandler`. Marcar los endpoints de `/v1/casos`. Test funcional: un `analista-arl` sin `casos:write` que intenta remitir deja una fila `DENEGADO`.
7. Crear la consulta `ListAccesosAuditadosQuery`, su validador y el endpoint `GET /v1/auditoria/accesos` en `Endpoints/Auditoria/`. Tests funcionales.
8. Añadir en `ArchitectureTests` la regla de que todo `ICommandHandler` de `Halsa.Backend.Application.Clinico` dependa de `IClinicalAuditTrail`.

---

## Criterios de aceptación

- [x] `UPDATE`, `DELETE` y `TRUNCATE` sobre `auditoria.accesos` fallan como `halsa_app`, como `halsa_clinico` y como `halsa_owner`.
- [x] Como `halsa_clinico`, `SELECT 1 FROM auditoria.accesos` falla con `permission denied`.
- [x] Después de migrar existen las particiones del mes actual y de los 3 siguientes, y llamar dos veces a `asegurar_particiones(3)` no falla.
- [x] Una fila con `ocurrido_en` fuera de las particiones mensuales cae en `accesos_default` sin error.
- [x] `RemitirCasoCommand` exitoso deja exactamente una fila `CREAR / REMISION / EXITO` con `tenant_id` igual al del caso.
- [x] Si el insert de auditoría de `RemitirCasoCommand` falla, la remisión no existe en `atel.remisiones`.
- [x] `RegistrarRegistroClinicoCommand` ejecutado por la IPS sobre un caso de `ARL_B` deja una fila con `tenant_id='ARL_B'`, `actor_tipo='ips'` y `actor_ips_id='110011234501'`.
- [x] `GetRegistrosClinicosQuery` exitosa deja una fila `LEER / REGISTRO_CLINICO / EXITO` antes de devolver los datos.
- [x] `GetRegistrosClinicosQuery` sobre un caso no visible deja una fila `LEER / REGISTRO_CLINICO / NO_ENCONTRADO`.
- [x] Con un escritor de auditoría que lanza, `GET /v1/casos/{id}` responde `500` con el código `Auditoria.NoRegistrada`, y el cuerpo no contiene el radicado del caso.
- [x] `GET /v1/casos` (el listado) no crea filas en `auditoria.accesos`.
- [x] `POST /v1/casos/{id}/remisiones` sin `casos:write` responde `403` y deja una fila con `resultado='DENEGADO'` y el `caso_medico_id` de la ruta.
- [x] `GET /v1/auditoria/accesos` como `privacidad.arla` devuelve solo filas con `tenant_id='ARL_A'`, incluidas las generadas por `medico.ips` sobre casos de `ARL_A`.
- [x] `GET /v1/auditoria/accesos` sin `auditoria:read` responde `403`, y con un rango de 32 días responde `400`.
- [x] Recalcular `hash_registro` con `CanonicalAuditHasher` sobre cualquier fila leída devuelve el mismo valor guardado.
- [x] El test de arquitectura falla si un handler de comando de `Application.Clinico` no depende de `IClinicalAuditTrail`.

---

## Decisiones

- **Sí:** se auditan los accesos clínicos, las escrituras de casos y el detalle de un caso (`GET /v1/casos/{id}`), porque contiene datos personales (Ley 1581/2012).
- **No:** auditar cada fila de un listado. El volumen crece por el número de filas devueltas, y el detalle ya es la puerta de acceso a los datos completos.
- **Sí:** `tenant_id` es el **tenant del recurso**, no el del actor. Así el oficial de privacidad de una ARL ve los accesos de las IPS a sus casos.
- **Sí:** `DENEGADO` se mueve de `accion` a `resultado`, y se añaden `NO_ENCONTRADO` y `ERROR`. Así se registra qué se intentó y cómo terminó por separado. **Cambio respecto a ARQ §3.3.**
- **Sí:** se añaden las acciones `ACTUALIZAR` y `ELIMINAR` para las escrituras administrativas (revocar y desasignar).
- **Sí:** las escrituras se auditan en el mismo `DbContext` y la misma transacción que el cambio, con dos interfaces explícitas (`IAuditTrail` e `IClinicalAuditTrail`). **No:** una interfaz única que adivine el contexto, porque oculta en qué transacción cae la fila.
- **Sí:** las lecturas se auditan con un `ApplicationDbContext` de un *scope* propio, confirmado antes de devolver la respuesta. `halsa_clinico` solo inserta, y no necesita leer la auditoría.
- **Sí:** la RLS de inserción es `WITH CHECK (true)`, porque una IPS registra filas de casos de varios tenants. La protección contra la manipulación es el trigger, más el sellado de la SPEC 11.
- **Sí:** particiones con una función SQL propia. **No:** `pg_partman`, porque obligaría a una imagen de PostgreSQL propia en el VPS.
- **Sí:** partición `DEFAULT` como red de seguridad. Un insert nunca falla porque falte una partición.
- **Sí:** un hash por registro con JCS restringido a la forma fija del registro. **No:** una librería JCS general, para lo que necesita esta tabla.
- **Sí:** un endpoint de consulta para el oficial de privacidad desde esta spec, con un rango máximo de 31 días. La exportación firmada queda para la SPEC 11.
- **Sí:** los denegados de política se capturan de forma genérica en las rutas `.Auditable`. **No:** auditar cada `401` de toda la API, porque el ruido taparía la señal.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| El dueño puede hacer `DROP` o `DETACH` de una partición y borrar la auditoría de un mes. | Los triggers no cubren DDL. `pgaudit` registra el DDL del dueño (ARQ §3.1), y el sellado de la SPEC 11 detecta el hueco. Las migraciones se revisan en el PR. |
| El volumen de auditoría de lecturas crece. | Particiones mensuales, sin auditar los listados, e índices por `(tenant_id, caso_medico_id)`. El archivado queda pendiente con su propia spec. |
| La fila de auditoría de una lectura se confirma pero la respuesta no llega (el cliente corta la conexión). | Se acepta: la auditoría registra el acceso **intentado y servido** por el servidor. Es el sesgo correcto (de más, no de menos). |
| Un handler de consulta administrativa olvida implementar `IAuditedQuery`. | Para lo clínico lo garantiza la herencia de `IClinicalQuery`. Para el detalle de casos hay un test funcional. Una consulta administrativa nueva con datos personales debe declararse en su spec. |
| La IP registrada es la de Nginx en lugar de la del cliente. | Es conocido y temporal, hasta que la SPEC 13 configure `ForwardedHeaders` con los proxies de confianza. |

---

## Qué **no** está en esta spec

- El sellado Merkle, la exportación firmada y el WORM (SPEC 11).
- El job de particiones y el archivado (SPEC 09 y sin spec).
- El motivo del step-up y del break-the-glass (SPEC 08).
- La notificación al oficial de privacidad (SPEC 08 y SPEC 10).
- `ForwardedHeaders` (SPEC 13).
- La auditoría de los listados.

Cada uno, si se aborda, va en su propia spec.

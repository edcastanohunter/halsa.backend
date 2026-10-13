# SPEC 05 — Ingesta de FURAT y FUREP desde el core de la ARL

> **Estado:** Implementado
> **Depende de:** SPEC 01, SPEC 02, SPEC 03, SPEC 04
> **Fecha:** 2026-10-09
> **Objetivo:** Que el core de una ARL radique un FURAT o un FUREP con `POST /v1/integraciones/{furat|furep}`, de forma idempotente, y que el caso, sus datos administrativos, su reporte clínico cifrado y el mensaje crudo queden en una sola transacción, seguida de una validación asíncrona.

---

## Por qué existe esta spec

El FURAT y el FUREP (Res. 156/2005) son la puerta de entrada de todos los casos.
Traen datos administrativos y datos clínicos mezclados.
La ingesta tiene que separarlos sin que exista un momento en que el caso exista sin su reporte, o al revés.

Las SPEC 02 y 03 pusieron `atel` y `clinico` en roles y conexiones distintos, así que no pueden compartir transacción.
Esta spec introduce el rol `halsa_ingesta`, que **escribe pero no lee** el esquema clínico.
Con él, la radicación completa es atómica sin abrirle a nadie la lectura de la HC.

La validación asíncrona (deduplicación y afiliación) necesita consultar casos fuera de un request HTTP.
Por eso esta spec trae el **contexto de tenant de sistema**, que la SPEC 01 había diferido.

---

## Alcance

**Incluido:**

- El rol `halsa_ingesta` y su `IngestaDbContext`.
- Los endpoints `POST /v1/integraciones/furat`, `POST /v1/integraciones/furep`, `GET /v1/integraciones/furat/{solicitudId}` y `GET /v1/integraciones/furep/{solicitudId}`.
- La idempotencia con `Idempotency-Key` (72 h) y el hash del cuerpo.
- La validación sintáctica con FluentValidation, según `docs/api/atel-api.openapi.yaml`.
- El modelo canónico (`Application.Integraciones.Canonico`) y los adaptadores `FuratV1Adapter` y `FurepV1Adapter`.
- La separación por campo: lo administrativo va a `atel`, lo clínico a `clinico` (`REPORTE_INICIAL`) y el mensaje crudo cifrado a `integracion`.
- El radicado `ATEL-{tenant}-{año}-{seq6}`, con secuencia por tenant y año.
- La validación asíncrona: deduplicación real, afiliación vía `IAfiliacionGateway` (un stub en desarrollo) y las transiciones `Radicado → Validado | Rechazado`.
- `InProcessEventBus`, que reemplaza a `LoggingEventBus` y entrega los eventos del outbox a handlers dentro del proceso.
- El contexto de tenant de sistema (`ISystemTenantScope`).
- La auditoría de la radicación y de la validación.

**Fuera de alcance (specs futuras):**

- La carga de conceptos médicos desde las IPS → SPEC 06.
- Los plazos que abre la radicación (el arreglo `plazos` sale vacío) → SPEC 09.
- La entrega de los webhooks `caso.validado` y `caso.rechazado` (aquí solo llegan al outbox) → SPEC 10.
- El cliente real del core de la ARL para la afiliación (SOAP/REST) → una spec por ARL integrada.
- La validación de los códigos contra catálogos (CIIU, DIVIPOLA, Res. 156): aquí solo se valida el formato → spec propia.
- El token ligado a certificado (`cnf.x5t#S256`) y mTLS → SPEC 13.
- El rate limiting (`429`) → SPEC 13 (Nginx).
- La purga de llaves de idempotencia vencidas → SPEC 09 (job). Hasta entonces, una llave vencida se ignora al consultar.
- La adaptación FHIR (`FhirBundleAdapter`) → diferida.

---

## Modelo de datos

### Rol `halsa_ingesta`

Se añade a `docker/postgres/init/01-roles.sql`, lo que exige `docker compose down -v`.

| Esquema / tabla | Privilegios |
| --- | --- |
| `atel.casos_medicos`, `atel.reportes_evento` | `SELECT, INSERT` |
| `atel.secuencias_radicado` | `SELECT, INSERT, UPDATE` |
| `integracion.*` | `SELECT, INSERT, UPDATE` |
| `clinico.historia_clinica_detalle` | **Solo `INSERT`** (no lee la HC) |
| `atel.tenant_del_caso_remitido` | `EXECUTE` (lo usa la SPEC 06 para la RLS de las IPS) |
| `atel.remisiones` | `SELECT`, acotado por su RLS a la IPS de la sesión (SPEC 06: lo exige la política de lectura de `casos_medicos`) |
| `clinico.registro_del_caso_visible` | `EXECUTE` (SPEC 06: valida `corrigeA` sin leer la HC) |
| `clinico.llaves_tenant` | `SELECT, INSERT` (DEK envueltas) |
| `outbox.outbox_messages`, `auditoria.accesos` | `INSERT` |

`IngestaDbContext` usa la cadena de conexión `Ingesta` y el `TenantSessionInterceptor`.
Mapea solo las entidades que necesita, y no aplica migraciones.
El actor `Integracion` de una ARL trae `tenant_id`, así que la RLS de todas las tablas se cumple con `app.tenant_id`.

### Tablas nuevas

| Tabla | Columnas | Notas |
| --- | --- | --- |
| `atel.reportes_evento` | `caso_medico_id` (PK, FK), `tenant_id`, `tipo` (`FURAT`/`FUREP`), `numero_reporte_origen`, `version`, `datos jsonb`, `recibido_en` | `datos` es el payload **sin** los campos `x-clasificacion: clinico`. Único `(tenant_id, tipo, numero_reporte_origen)`. RLS por tenant. |
| `atel.secuencias_radicado` | `tenant_id`, `anio`, `ultimo` | PK `(tenant_id, anio)`. Se incrementa con `INSERT … ON CONFLICT DO UPDATE SET ultimo = ultimo + 1 RETURNING ultimo`. RLS por tenant. |
| `integracion.solicitudes` | `id` (`solicitudId`), `tenant_id`, `tipo`, `caso_medico_id`, `radicado`, `recibido_en`, `client_id`, `correlation_id` | Permanente. RLS por tenant. `halsa_app` tiene `SELECT` (para la consulta de estado). |
| `integracion.solicitudes_idempotentes` | `emisor varchar(80)`, `idempotency_key uuid`, `tenant_id`, `cuerpo_sha256 bytea`, `solicitud_id`, `respuesta jsonb`, `creada_en`, `expira_en` | PK `(emisor, idempotency_key)`, donde `emisor` vale `tenant:{ARL}`, `ips:{REPS}` (SPEC 06) o `sub:{sub}` (los humanos, SPEC 07). RLS: `emisor` es igual a `'tenant:' \|\| app.tenant_id`, a `'ips:' \|\| app.ips_id` o a `'sub:' \|\| app.sub`. |
| `integracion.mensajes_entrantes` | `solicitud_id` (PK), `tenant_id`, `tipo`, `cuerpo_ct`, `cuerpo_nonce`, `cuerpo_tag`, `cuerpo_key_id`, `recibido_en`, `retener_hasta` | Cuerpo cifrado con la DEK del tenant, propósito `INTEGRACION`, y AAD `"{tenant}|{solicitudId}|mensaje"`. `retener_hasta` es `recibido_en` + 15 años. Trigger de inmutabilidad. **Sin acceso para `halsa_app`.** |

Columnas nuevas en `atel.casos_medicos`: `motivo_rechazo varchar(30)` (null) y `duplicado_de uuid` (null).
El índice de deduplicación es `(tenant_id, trabajador_tipo, trabajador_numero, fecha_evento)`.

### Modelo canónico (`Halsa.Backend.Application.Integraciones.Canonico`)

```csharp
public sealed record ReporteCanonico(
    TipoReporte Tipo,                        // Furat | Furep
    string NumeroReporteOrigen,
    Identificacion Trabajador, Identificacion Empleador,
    DateTimeOffset FechaEvento,              // FURAT: evento.fechaHora. FUREP: fechaDiagnostico a las 00:00 -05:00
    string MunicipioDivipola, bool EsGraveOMortal,   // esGrave || causoMuerte (en el FUREP, false)
    JsonObject DatosAdministrativos,         // el payload sin los campos clínicos
    ParteClinica Clinica);

public sealed record ParteClinica(
    string? Relato,                          // FURAT: evento.descripcion
    IReadOnlyList<CodigoClinico> Codigos,    // FURAT: tiposLesion + partesCuerpo. FUREP: diagnosticos (CIE-10)
    ProfesionalRef Autor);                   // FURAT: informante (sin RETHUS). FUREP: medicoDiagnostica
```

Correspondencia con `ContenidoClinico` (SPEC 03) para el registro `REPORTE_INICIAL`:

- `Motivo = "Reporte inicial FURAT"` (o `"Reporte inicial FUREP"`).
- `Anamnesis = Relato`.
- `AtendidoEn = FechaEvento`.
- `Diagnosticos = Codigos`.
- `DiagnosticoPrincipal` es el primer código CIE-10, si existe. Si no, es el primer código.

### Cambios en `CasoMedico`

```csharp
public enum MotivoRechazo { Duplicado = 1, NoAfiliado = 2 }

Result Validar(DateTimeOffset ahora);                                          // solo desde Radicado. Emite CasoValidadoDomainEvent.
Result Rechazar(MotivoRechazo motivo, CasoMedicoId? duplicadoDe, DateTimeOffset ahora);   // solo desde Radicado. Emite CasoRechazadoDomainEvent.
```

Cualquier otra transición devuelve `Caso.TransicionInvalida`.

### Flujo de `POST /v1/integraciones/furat` (el del FUREP es igual)

1. El endpoint exige el permiso `integracion:furat:write` y un actor `Integracion` con `tenant_id`. Si no, responde `403` con `Integracion.ActorNoAutorizado`.
2. Si falta `Idempotency-Key` o no es un UUID, responde `400` con `Integracion.IdempotencyKeyInvalida`.
3. Se calcula el SHA-256 del cuerpo crudo y se busca `(emisor, key)` vigente:
   - Si tiene el mismo hash, devuelve la `respuesta` guardada con `202`.
   - Si tiene otro hash, responde `409` con `Integracion.IdempotenciaConflicto`.
4. Se valida con FluentValidation. Si falla, responde `400` con `errors` por ruta JSON, como en el contrato.
5. El adaptador construye el `ReporteCanonico`.
6. En **una sola transacción** de `IngestaDbContext`:
   - la secuencia y el radicado;
   - `CasoMedico.Radicar`;
   - `reportes_evento`;
   - `mensajes_entrantes` (cifrado);
   - `HistoriaClinicaDetalle` `REPORTE_INICIAL` (cifrado);
   - `solicitudes` y `solicitudes_idempotentes`;
   - el outbox (`CasoRadicadoIntegrationEvent`);
   - la auditoría (`CREAR / CASO` y `CREAR / REGISTRO_CLINICO`).
7. Si la transacción choca en la PK de idempotencia (dos requests concurrentes con la misma llave), se relee la fila ganadora y se aplica el paso 3.
8. Si choca en `(tenant_id, tipo, numero_reporte_origen)` con otra llave, responde `409` con `Integracion.ReporteYaRadicado` y el `radicado` existente.
9. Responde `202 RadicacionResponse` (`estado=RADICADO`, `plazos=[]`) y el header `Location: /v1/integraciones/furat/{solicitudId}`.

### Consulta de estado

`GET /v1/integraciones/furat/{solicitudId}` usa el mismo permiso y el mismo actor, y lee con `halsa_app`.
Mapea el estado del caso así:

| Estado del caso | `estado` de la respuesta |
| --- | --- |
| `Radicado` | `RADICADO` |
| `Validado` (o posterior) | `VALIDADO` |
| `Rechazado` con motivo `Duplicado` | `DUPLICADO` |
| `Rechazado` con motivo `NoAfiliado` | `RECHAZADO`, con `motivoRechazo` |

Una solicitud de otro tenant responde `404`.

### Validación asíncrona

- `OutboxProcessor` entrega `CasoRadicadoIntegrationEvent` a `InProcessEventBus`. Este abre un *scope* por mensaje y publica a los `IIntegrationEventHandler<T>` registrados (además de registrarlo en el log, como hacía `LoggingEventBus`).
- `ValidarCasoRadicadoHandler` entra en `ISystemTenantScope.Entrar(tenantId)` y trabaja con `ApplicationDbContext` (`halsa_app`):
  1. **Deduplicación.** Busca otro caso del mismo tenant, tipo y trabajador, con `|fecha_evento − fecha| ≤ 24 h`, que no esté `Rechazado` y que se haya radicado antes. Si existe, rechaza con `Duplicado` y `duplicado_de`.
  2. **Afiliación.** `IAfiliacionGateway.VerificarAsync(tenant, trabajador, empleador, fechaEvento)` devuelve `Afiliado`, `NoAfiliado` o lanza una excepción. Con `NoAfiliado`, el caso se rechaza. Si lanza, el mensaje del outbox se reintenta.
  3. Si no aplica ninguna de las dos, el caso pasa a `Validado`.
  4. Los eventos de dominio se convierten en `CasoValidadoIntegrationEvent` y `CasoRechazadoIntegrationEvent` en el outbox (sin consumidor hasta la SPEC 10), y se audita `ACTUALIZAR / CASO` con el actor de sistema.
- `DevAfiliacionGateway` aprueba siempre, salvo los trabajadores cuyo número termina en `000`, que rechaza para poder probar ese camino. Solo se registra en `Development` y `Testing`.

### Contexto de sistema (`ISystemTenantScope`)

```csharp
public interface ISystemTenantScope   // Application/Abstractions/Tenancy
{
    IDisposable Entrar(TenantId tenant);   // ITenantContext = { Resuelto, Tipo = Sistema, Sub = "sistema", TenantId = tenant }
}
```

- `HttpTenantContext` se reemplaza por `TenantContext`, que lee un `TenantContextHolder` *scoped*. El holder lo llenan el middleware (HTTP) o `ISystemTenantScope` (background).
- Si se entra en un scope de sistema dentro de un request HTTP, lanza una excepción.
- `IAuditActorProvider` registra `actor_id='sistema'`, `actor_tipo='sistema'` y el `client_id` del proceso.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Añadir `halsa_ingesta` al script de init y la cadena `Ingesta` a la configuración y a los fixtures. Crear la migración `IntegracionEsquema`, con las tablas nuevas, las columnas de `casos_medicos`, el trigger de `mensajes_entrantes`, los `GRANT` de `halsa_ingesta` y `halsa_app`, y la RLS. Tests de integración: `halsa_ingesta` no puede hacer `SELECT` sobre `clinico.historia_clinica_detalle`.
2. Añadir el propósito `INTEGRACION` al `DekStore` (SPEC 03) y la AAD del mensaje. Test de integración: el mensaje cifrado se descifra con la AAD correcta y falla con otro `solicitudId`.
3. Crear `TenantContextHolder`, `TenantContext` e `ISystemTenantScope`, que reemplazan a `HttpTenantContext`. Tests unitarios: un scope de sistema resuelve `Sistema`, y entrar en uno dentro de HTTP lanza.
4. Crear `MotivoRechazo`, `Validar` y `Rechazar` en `CasoMedico`, con sus eventos. Tests unitarios de las transiciones.
5. Crear el modelo canónico, `FuratV1Adapter`, `FurepV1Adapter` y sus validadores. Tests unitarios con el ejemplo `FuratCaidaAltura` del contrato: los campos clínicos no aparecen en `DatosAdministrativos`.
6. Crear `IngestaDbContext`, `IdempotencyStore`, `RadicadoGenerator` y `RadicarReporteCommand` con su handler transaccional. Tests de integración del flujo completo, de la idempotencia y de la concurrencia con la misma llave.
7. Crear los endpoints de `src/Halsa.Backend.Api/Endpoints/Integraciones/` y `Tags.Integraciones`. Tests funcionales.
8. Crear `InProcessEventBus` e `IIntegrationEventHandler<T>`, y retirar `LoggingEventBus`. Crear `IAfiliacionGateway`, `DevAfiliacionGateway` y `ValidarCasoRadicadoHandler`. Tests de integración: duplicado, no afiliado, válido y gateway que lanza (se reintenta).
9. Añadir el test de arquitectura: solo `Halsa.Backend.Application.Integraciones` (y la infraestructura) puede depender de `IIngestaDbContext`.

---

## Criterios de aceptación

- [x] `POST /v1/integraciones/furat` con el ejemplo `FuratCaidaAltura` y el token de `int-arl-ARL_A` responde `202` con `radicado` `ATEL-ARL_A-2026-000001` (en una base limpia), `estado=RADICADO` y el header `Location`.
- [x] Después de esa radicación existen, en una misma transacción, una fila en `casos_medicos`, una en `reportes_evento`, una en `mensajes_entrantes`, una `REPORTE_INICIAL` en `historia_clinica_detalle`, una en `solicitudes`, una en `solicitudes_idempotentes`, un mensaje en el outbox y dos filas de auditoría.
- [x] Si se fuerza un fallo al insertar en `historia_clinica_detalle` (con una restricción de test), no queda ninguna de esas filas.
- [x] `reportes_evento.datos` no contiene las claves `descripcion`, `tiposLesion` ni `partesCuerpo` (en el FUREP, `diagnosticos`).
- [x] `mensajes_entrantes.cuerpo_ct` no contiene la cadena `"andamio"` del ejemplo, y como `halsa_app`, `SELECT 1 FROM integracion.mensajes_entrantes` falla con `permission denied`.
- [x] Como `halsa_ingesta`, `SELECT 1 FROM clinico.historia_clinica_detalle` falla con `permission denied`.
- [x] Repetir el POST con la misma `Idempotency-Key` y el mismo cuerpo responde `202` con el mismo `solicitudId` y el mismo `radicado`, y sigue habiendo un solo caso.
- [x] La misma `Idempotency-Key` con otro cuerpo responde `409` con `Integracion.IdempotenciaConflicto`.
- [x] Dos POST concurrentes con la misma llave y el mismo cuerpo dejan un solo caso, y ambos responden `202` con el mismo `radicado`.
- [x] Otra llave con el mismo `numeroReporteOrigen` responde `409` con `Integracion.ReporteYaRadicado`.
- [x] Sin `Idempotency-Key`, el POST responde `400`. Con `evento.fechaHora` futura, responde `400` con un error en `evento.fechaHora`.
- [x] El POST con un token de `analista.arla` responde `403`, y con el de `int-ips-110011234501` también responde `403`.
- [x] Las secuencias de `ARL_A` y `ARL_B` son independientes: el primer caso de cada tenant termina en `000001`.
- [x] Después de procesar el outbox, el caso del ejemplo queda `Validado` y `GET /v1/integraciones/furat/{solicitudId}` devuelve `estado=VALIDADO`.
- [x] Un segundo FURAT del mismo trabajador con un evento 3 h después queda `Rechazado` (`Duplicado`, `duplicado_de` informado), y la consulta devuelve `DUPLICADO`.
- [x] Un trabajador con un número terminado en `000` queda `Rechazado` (`NoAfiliado`).
- [x] Si el gateway lanza, el mensaje del outbox queda con `attempts > 0` y el caso sigue `Radicado`.
- [x] La validación deja una fila de auditoría `ACTUALIZAR / CASO` con `actor_tipo='sistema'`.
- [x] `GET /v1/integraciones/furat/{solicitudId}` de una solicitud de `ARL_B`, con un token de `ARL_A`, responde `404`.
- [x] `POST /v1/integraciones/furep` con un diagnóstico `J45.0` crea un caso `EnfermedadLaboral` y un `REPORTE_INICIAL` cuyo `diagnostico_indice_ciego` coincide con el de `CIE-10|J45.0` del tenant.

---

## Decisiones

- **Sí:** el rol `halsa_ingesta`, con `INSERT` sin `SELECT` sobre `clinico`, para que la radicación sea atómica. **No:** dos fases con reconciliación, que da consistencia eventual, registros huérfanos y más código.
- **Sí:** el mensaje crudo se guarda cifrado durante 15 años. Es la evidencia de lo que reportó la ARL. Solo `halsa_ingesta` lo lee.
- **Sí:** la DEK tiene un propósito propio `INTEGRACION`, distinto de `CONTENIDO`. Así se puede rotar o revocar por separado (se ajustó la SPEC 03).
- **Sí:** los datos administrativos del reporte van en `atel.reportes_evento.datos` (jsonb), y `CasoMedico` sigue siendo mínimo. **No:** un modelo ancho acoplado a la Res. 156.
- **Sí:** el radicado es `ATEL-{tenant}-{año}-{seq6}`, con una secuencia por tenant y año en una tabla con RLS. **No:** una `SEQUENCE` de PostgreSQL, que es global y no tiene RLS: revelaría el volumen de las otras ARL.
- **Sí:** `radicado` crece a `varchar(40)`, porque `ATEL-` + un tenant de 20 + `-AAAA-` + 6 = 37 (se ajustó la SPEC 02).
- **Sí:** en un `REPORTE_INICIAL` de FURAT, el autor es el informante, y `RegistroRethus` es nullable solo en ese tipo (se ajustó la SPEC 03). En el FUREP, el autor es `medicoDiagnostica`, con RETHUS.
- **Sí:** la idempotencia es por `(emisor, key)`, así que dos emisores pueden usar la misma llave sin chocar. Además, se protege la clave de negocio `(tenant, tipo, numeroReporteOrigen)`.
- **Sí:** se responde `202` en cuanto la transacción confirma, y la validación es asíncrona (ARQ §4.2).
- **Sí:** la deduplicación es real y la afiliación usa un stub con una regla determinista para las pruebas. El cliente del core de cada ARL va en su propia spec.
- **Sí:** `InProcessEventBus` reemplaza a `LoggingEventBus`. Sin broker externo en el VPS, el outbox ya garantiza la entrega *at-least-once*.
- **Sí:** el contexto de tenant de sistema se adelanta a esta spec, porque la validación lo necesita (antes estaba previsto para la SPEC 09; se ajustó la SPEC 01).
- **Sí:** se valida solo el formato de los códigos. **No:** validar contra catálogos oficiales en esta spec.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| `halsa_ingesta` acumula privilegios sobre varios esquemas. | No tiene `SELECT` en la HC, ni `UPDATE`/`DELETE` en `clinico` ni en `auditoria`. Un test de arquitectura limita su contexto a `Application.Integraciones`. |
| EF Core usa `INSERT … RETURNING` y falla porque `halsa_ingesta` no tiene `SELECT` sobre la HC. | Las claves se generan en el cliente (UUID v7) y la entidad no tiene columnas generadas por la base. El test del paso 6 lo cubre. |
| Un FURAT legítimo se rechaza como duplicado (dos accidentes del mismo trabajador el mismo día). | La ventana de ±24 h es un parámetro (`Integraciones:VentanaDuplicado`). El webhook `caso.rechazado` lleva `duplicado_de` para que la ARL lo revise. Reabrir el caso se resuelve en otra spec. |
| El stub de afiliación aprueba casos reales si se registra fuera de desarrollo. | Se registra solo en `Development` y `Testing`. En `Production`, el arranque falla si no hay un gateway real. |
| La validación del FUREP construye la fecha del evento con la zona `-05:00` fija. | Colombia no tiene horario de verano. Se documenta en el adaptador. |
| Reintentos infinitos si el core de la ARL está caído. | `Outbox:MaxAttempts` limita los intentos. El mensaje queda con su error y la SPEC 09 añade la alerta. |

---

## Qué **no** está en esta spec

- Los conceptos médicos de las IPS (SPEC 06).
- Los plazos legales (SPEC 09).
- La entrega de webhooks (SPEC 10).
- Los clientes reales de afiliación por ARL.
- La validación contra catálogos oficiales.
- mTLS, el token ligado a certificado y el rate limiting (SPEC 13).
- La purga de idempotencia (SPEC 09).
- La adaptación FHIR.

Cada uno, si se aborda, va en su propia spec.

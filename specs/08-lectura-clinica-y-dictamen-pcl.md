# SPEC 08 — Lectura clínica, dictamen PCL y break-the-glass

> **Estado:** Implementado
> **Depende de:** SPEC 01, SPEC 02, SPEC 03, SPEC 04, SPEC 06, SPEC 07
> **Fecha:** 2026-10-09
> **Objetivo:** Abrir la lectura de la historia clínica, la descarga de soportes y el registro y la consulta del dictamen PCL, solo para los profesionales asignados al caso y con MFA, auditando cada acceso. La excepción es el break-the-glass, que queda justificado y sometido a revisión.

---

## Por qué existe esta spec

Hasta aquí la HC solo se escribe.
Leerla es el acceso más sensible del sistema (Res. 1995/1999 y Ley 23/1981).
Esta spec añade la barrera que faltaba de ARQ §6.2: **el ABAC por recurso**.
No basta con el permiso: hay que estar asignado al caso.

El dictamen PCL es el ejemplo extremo.
Su motivación es la información más sensible, así que exige un step-up reciente (15 min).
Su resultado administrativo (porcentaje y origen) sí lo necesita la ARL para liquidar.

---

## Alcance

**Incluido:**

- `AccesoClinicoRequirement` y `AccesoClinicoHandler` (ABAC por tenant o remisión, más la asignación).
- `ICasoAccessReader`.
- Las políticas `ClinicoLectura`, `DictamenPclLectura` y `DictamenPclEscritura`, que reutilizan el step-up de la SPEC 07.
- `GET /v1/casos/{id}/registros-clinicos` y `GET /v1/casos/{id}/registros-clinicos/{registroId}`.
- `GET /v1/casos/{id}/incapacidades/{incapacidadId}/soporte` (el PDF descifrado).
- `GET /v1/casos/{id}/dictamen-pcl`, `POST /v1/casos/{id}/dictamen-pcl` y `POST /v1/casos/{id}/dictamen-pcl/notificar`.
- La tabla `atel.dictamenes_pcl` y la actualización de `CasoMedico.Calificacion`.
- El break-the-glass: el permiso `clinico:emergencia`, el header `Halsa-Motivo-Acceso`, la tabla `auditoria.revisiones_emergencia` y sus endpoints de revisión.
- La asignación de médicos por parte del personal de la IPS (se ajustan las SPEC 01 y 02).

**Fuera de alcance (specs futuras):**

- El plazo de controversia y el paso a `EN_FIRME` → SPEC 09.
- Registrar una controversia y la integración con las Juntas de Calificación → fuera de ARQ 01.
- Un dictamen nuevo después de la notificación (recalificación) → spec propia.
- La notificación en tiempo real al oficial de privacidad → SPEC 10.
- El acceso del paciente a su HC → SPEC 12 (solo la solicitud de copia).
- La exportación de la HC (copia formal) → spec propia.
- La firma digital del dictamen → fuera de ARQ 01.

---

## Modelo de datos

### ABAC

```csharp
public sealed record AccesoClinicoRequirement : IAuthorizationRequirement;   // el recurso es CasoMedicoId

public sealed record CasoAcceso(TenantId TenantId, IReadOnlySet<IpsId> RemisionesVigentes,
    IReadOnlyList<(string Sub, IpsId? IpsId)> Asignados);

public interface ICasoAccessReader { Task<CasoAcceso?> ObtenerAsync(CasoMedicoId id, CancellationToken ct); }   // halsa_app, con RLS
```

Reglas de `AccesoClinicoHandler`:

| Actor | Condición para `Succeed` |
| --- | --- |
| `Arl` | `caso.TenantId == actor.TenantId` y `(actor.Sub, null)` está asignado |
| `Ips` | `caso.RemisionesVigentes ∋ actor.IpsId` y `(actor.Sub, actor.IpsId)` está asignado |
| `Arl` o `Ips` con `clinico:emergencia` y `Halsa-Motivo-Acceso` de 20 caracteres o más | La misma condición de tenant o remisión, **sin** exigir la asignación |
| `Integracion`, `Paciente`, `Sistema` | Nunca |

- Si `ICasoAccessReader` devuelve `null` (el caso no existe o no es visible), el endpoint responde `404` y no `403`.
- Si el caso es visible pero el actor no está asignado, responde `403` con `Clinico.NoAsignado`.
- Todo resultado negativo se audita como `DENEGADO` con `caso_medico_id`, a través de `IClinicalAuthorization.AutorizarAsync(user, casoId, politica)`. Este helper de los endpoints llama a `IAuthorizationService` y audita.

### Políticas

| Política | Permiso | Step-up | ABAC |
| --- | --- | --- | --- |
| `ClinicoLectura` | `clinico:read` | `acr ≥ 2` | Sí |
| `DictamenPclLectura` | `pcl:dictamen:read` | `acr ≥ 2` y `auth_time` de 15 min o menos | Sí |
| `DictamenPclEscritura` | `pcl:dictamen:write` | `acr ≥ 2` y `auth_time` de 15 min o menos | Sí |

### Lectura de la HC

- `GET /v1/casos/{id}/registros-clinicos` devuelve `RegistroClinicoResponse[]`: `id`, `tipo`, `atendidoEn`, `registradoEn`, `profesional` (`sub`, documento y RETHUS; el nombre no se guarda en el registro), `contenido` (`ContenidoClinico`), `corrigeA` y `corregidoPor`. **Excluye** los registros `DICTAMEN_PCL`, que solo salen por el endpoint del dictamen.
- `GET …/registros-clinicos/{registroId}` devuelve un registro. Un registro `DICTAMEN_PCL` responde `404` por esta ruta.
- Los dos usan `GetRegistrosClinicosQuery` y `GetRegistroClinicoQuery` (`IClinicalQuery`, con auditoría que falla cerrada).

### Descarga del soporte

`GET /v1/casos/{id}/incapacidades/{incapacidadId}/soporte` usa la política `ClinicoLectura`.

1. `GetSoporteQuery` (`IClinicalQuery`) lee `clinico.soportes` con `halsa_clinico`.
2. Se audita `LEER / SOPORTE`.
3. Abre el archivo descifrado con `IFileStore.AbrirAsync`.
4. Verifica el SHA-256 contra `sha256_claro`. Si no coincide, responde `500` con `Archivos.Corrupto` y no entrega bytes.
5. Responde `200 application/pdf`, con `Content-Disposition: attachment; filename="incapacidad-{numeroCertificado}.pdf"` y `Cache-Control: no-store`.

### Dictamen PCL

Tabla `atel.dictamenes_pcl` (la parte administrativa):

| Columna | Notas |
| --- | --- |
| `id`, `tenant_id`, `caso_medico_id`, `registro_clinico_id` | Un dictamen por caso en esta spec. Único `(caso_medico_id) WHERE corregido_por IS NULL`. |
| `porcentaje_pcl numeric(5,2)`, `origen` (`LABORAL`/`COMUN`/`MIXTO`), `fecha_estructuracion date`, `fecha_dictamen date` | |
| `calificador_sub`, `calificador_nombre`, `calificador_registro_rethus` | |
| `estado_notificacion` (`PENDIENTE`/`NOTIFICADO`/`EN_CONTROVERSIA`/`EN_FIRME`), `notificado_en`, `medio_notificacion` | |
| `corrige_a`, `corregido_por` (null) | |

La parte clínica es un registro de HC `DICTAMEN_PCL` cuyo `ContenidoClinico` se llena así:

- `Analisis = fundamentos`.
- `Diagnosticos = diagnosticos`.
- `Observaciones` = JSON con los `componentes`.

`POST /v1/casos/{id}/dictamen-pcl` (política `DictamenPclEscritura`):

- El cuerpo es `{ porcentajePcl, origen, fechaEstructuracion, fechaDictamen, componentes?, fundamentos, diagnosticos[], corrigeA? }`. El calificador sale de los claims (`IProfesionalActual`).
- Reglas:
  - `0 ≤ porcentajePcl ≤ 100`, con 2 decimales.
  - Si hay `componentes`, `deficiencias + rolLaboralYOtrasAreas = porcentajePcl` (±0,01).
  - `fechaEstructuracion ≤ fechaDictamen ≤ hoy`.
  - `fundamentos` tiene entre 50 y 20 000 caracteres.
  - Hay al menos un diagnóstico CIE-10.
- Si ya existe un dictamen vigente: sin `corrigeA` responde `409` con `Dictamen.YaExiste`. Con `corrigeA`, solo se permite si el estado es `PENDIENTE` (si no, `409` con `Dictamen.YaNotificado`).
- Se escribe en una transacción de `IngestaDbContext`:
  - el registro de HC;
  - `dictamenes_pcl` (y `corregido_por` del anterior);
  - `CasoMedico.Calificacion = (porcentaje, fechaEstructuracion, origen)`;
  - el estado del caso pasa a `EnCalificacionPcl`;
  - la auditoría (`CREAR` o `CORREGIR` sobre `REGISTRO_CLINICO`).
- `halsa_ingesta` recibe `UPDATE` solo sobre las columnas `estado` y `calificacion_*` de `atel.casos_medicos`, y sobre `corregido_por` de `atel.dictamenes_pcl`.
- Responde `201 DictamenPclResponse`.

`POST /v1/casos/{id}/dictamen-pcl/notificar` (`DictamenPclEscritura`):

- El cuerpo es `{ notificadoEn, medio: CORREO_CERTIFICADO | ELECTRONICO | PERSONAL }`.
- Solo se permite desde `PENDIENTE` (si no, `409`). `notificadoEn` no puede ser futuro ni anterior a `fechaDictamen`.
- Escribe con `halsa_app` (es un dato administrativo) y emite `DictamenPclNotificadoIntegrationEvent` al outbox.
- Responde `204`.

`GET /v1/casos/{id}/dictamen-pcl` (`DictamenPclLectura`) devuelve el `DictamenPclResponse` del contrato: la parte administrativa más la clínica descifrada.
`venceControversiaEn` es `null` hasta que lo calcule la SPEC 09.

### Break-the-glass

Tabla `auditoria.revisiones_emergencia`:

| Columna | Notas |
| --- | --- |
| `id`, `tenant_id` (del caso), `acceso_id` (FK lógica a `accesos`), `actor_id`, `caso_medico_id`, `motivo`, `ocurrido_en` | Se insertan en la misma operación que la fila de auditoría. |
| `vence_revision_en` | `ocurrido_en + 72 h` |
| `revisado_en`, `revisado_por`, `conclusion` (`JUSTIFICADO` / `INJUSTIFICADO`), `observaciones` | `halsa_app` tiene `UPDATE` solo sobre estas columnas. Un trigger impide cambiarlas una vez cerradas. |

- RLS de lectura y actualización por tenant.
- `GET /v1/auditoria/revisiones-emergencia?estado=PENDIENTE|CERRADA` y `POST /v1/auditoria/revisiones-emergencia/{id}/cerrar` (`{ conclusion, observaciones }`) exigen el permiso nuevo `auditoria:revisar` (que se añade a `oficial-privacidad`).
- Cada lectura con break-the-glass registra `motivo` en `auditoria.accesos` y crea una revisión. Si hay varias del mismo actor sobre el mismo caso en 1 h, se agrupan en una sola revisión.

### Ajustes a specs anteriores (incluidos en esta spec)

- **SPEC 01:**
  - Permisos nuevos: `casos:asignar` (en `analista-arl` y en un compuesto nuevo, `coordinador-ips`) y `auditoria:revisar` (en `oficial-privacidad`).
  - Usuario semilla `coordinador.ips` (`id` `01999000-0000-7000-8000-000000000c02`, `ips_id=110011234501`, rol `coordinador-ips`).
  - `calificador.arla` recibe `clinico:emergencia` en el realm de desarrollo.
- **SPEC 02:**
  - Los endpoints de asignación exigen `casos:asignar` en lugar de `casos:write`, y admiten actores `Ips` sobre los casos que tienen remitidos.
  - `atel.asignaciones` gana `ips_id` (null para el personal de la ARL). Una IPS solo crea y borra asignaciones con su propio `ips_id`.
  - Su política de RLS de escritura admite `ips_id = app.ips_id` con una remisión vigente.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Aplicar los ajustes a la SPEC 01 (realm y `Permissions.cs`) y a la SPEC 02 (la migración `AsignacionesIps`, el comando, el endpoint y la RLS). Tests funcionales de la asignación por la IPS.
2. Crear `ICasoAccessReader`, `AccesoClinicoRequirement`, `AccesoClinicoHandler`, las tres políticas y `IClinicalAuthorization`, con auditoría `DENEGADO`. Tests unitarios del handler, uno por fila de la tabla de reglas.
3. Crear las consultas `GetRegistroClinicoQuery` y `GetRegistrosClinicosQuery` (con exclusión de `DICTAMEN_PCL`) y sus dos endpoints. Tests funcionales.
4. Crear `GetSoporteQuery` y el endpoint de descarga con la verificación del SHA-256. Tests funcionales.
5. Crear la migración `AtelDictamenesPcl` (tabla, RLS, `GRANT` por columnas a `halsa_ingesta`), `RegistrarDictamenPclCommand` y su endpoint. Tests de integración y funcionales.
6. Crear `NotificarDictamenPclCommand`, `GetDictamenPclQuery` y sus endpoints, y el evento en el outbox.
7. Añadir el break-the-glass al handler. Crear la migración `RevisionesEmergencia` y la escritura de la revisión desde la auditoría de lecturas clínicas. Tests.
8. Crear los endpoints de revisión de emergencias.
9. Actualizar `docs/api/atel-api.openapi.yaml` con las rutas nuevas, el `POST` del dictamen, `notificar` y las revisiones.

---

## Criterios de aceptación

- [x] Un token `analista-arl` recibe `403` en `GET /v1/casos/{id}/dictamen-pcl` y en `GET /v1/casos/{id}/registros-clinicos`, y cada intento deja una fila `DENEGADO`.
- [x] `calificador.arla` **asignado** al caso, con `acr=1`, recibe `401` con `error="insufficient_user_authentication"` en el dictamen.
- [x] El mismo usuario con `acr=2` y `auth_time` de hace 20 min recibe `401` con `max_age=900` en el `WWW-Authenticate`.
- [x] El mismo usuario con `acr=2` y `auth_time` de hace menos de 15 min recibe `200`, y queda una fila `LEER / REGISTRO_CLINICO / EXITO` en `auditoria.accesos`.
- [x] `calificador.arla` **no asignado** al caso, con `acr=2` reciente, recibe `403` con `Clinico.NoAsignado`.
- [x] `calificador.arla` sobre un caso de `ARL_B` recibe `404`.
- [x] `medico.ips` asignado por `coordinador.ips` a un caso remitido lee la HC (`200`). Al revocar la remisión, recibe `404`.
- [x] `coordinador.ips` no puede asignar sobre un caso que no está remitido a su IPS (`404`), y su asignación queda con `ips_id='110011234501'`.
- [x] La lista de la HC no incluye registros `DICTAMEN_PCL`, y `GET …/registros-clinicos/{idDelDictamen}` responde `404`.
- [x] La descarga del soporte devuelve exactamente los bytes del PDF original, con `Content-Type: application/pdf` y `Cache-Control: no-store`, y deja `LEER / SOPORTE`.
- [x] Si se altera un byte del archivo en disco, la descarga responde `500` con `Archivos.Corrupto` (falla el tag de GCM o el hash).
- [x] `POST /v1/casos/{id}/dictamen-pcl` con 32,50 % y componentes 20,00 + 12,50 responde `201`, y el caso queda en `EnCalificacionPcl` con `Calificacion.PorcentajePcl=32.50`.
- [x] Ese mismo POST con componentes que no suman responde `400`.
- [x] Un segundo POST sin `corrigeA` responde `409` con `Dictamen.YaExiste`. Con `corrigeA`, estando `PENDIENTE`, responde `201` y el anterior queda con `corregido_por`.
- [x] Después de `notificar`, el estado es `NOTIFICADO`, hay un `DictamenPclNotificadoIntegrationEvent` en el outbox, y un POST con `corrigeA` responde `409` con `Dictamen.YaNotificado`.
- [x] `GET /v1/casos/{id}` como `analista.arla` devuelve `porcentajePcl=32.50`, y ni ese endpoint ni ningún otro accesible para `analista-arl` contiene `fundamentos`.
- [x] `calificador.arla` no asignado, con `clinico:emergencia` y un `Halsa-Motivo-Acceso` de 25 caracteres, recibe `200`. Queda una fila con `motivo` y una revisión `PENDIENTE` con `vence_revision_en` a 72 h.
- [x] El mismo intento con un motivo de 10 caracteres recibe `403`. Con `clinico:emergencia` sobre un caso de `ARL_B`, recibe `404`.
- [x] `privacidad.arla` lista la revisión pendiente y la cierra como `JUSTIFICADO`. Un segundo cierre responde `409`.
- [x] Un token de `int-ips-110011234501` recibe `403` en todos los endpoints de lectura clínica.

---

## Decisiones

- **Sí:** la HC, el soporte y el dictamen pasan por ABAC (asignación) y step-up. El dictamen exige además un `auth_time` de 15 min o menos (ARQ §6.2).
- **Sí:** un caso invisible responde `404`, y uno visible sin asignación responde `403`. No se revela que existen casos de otro tenant, y el profesional legítimo entiende qué le falta.
- **Sí:** la IPS necesita remisión y asignación (ARQ). Para que sea operable, la IPS asigna a su propio personal con `casos:asignar` (rol `coordinador-ips`).
- **Sí:** la asignación guarda el `ips_id` de quien asigna. Un `sub` asignado por una IPS solo sirve con un token de esa misma IPS, así que no hace falta consultar Keycloak.
- **Sí:** los registros `DICTAMEN_PCL` se excluyen de la lectura general de la HC. Su motivación solo sale con `pcl:dictamen:read` y un step-up reciente.
- **Sí:** el dictamen se escribe con `halsa_ingesta` (escritura mixta), con `UPDATE` limitado por columnas sobre `casos_medicos`.
- **Sí:** un solo dictamen vigente por caso, y solo se corrige en `PENDIENTE`. La recalificación y la controversia formal quedan fuera.
- **Sí:** la notificación del dictamen se registra como dato administrativo y emite el evento del que colgará el plazo de controversia (SPEC 09).
- **Sí:** el break-the-glass salta solo la asignación, nunca el tenant ni la remisión. Exige un motivo y crea una revisión obligatoria a 72 h.
- **Sí:** las revisiones del mismo actor y caso en 1 h se agrupan, para no inundar al oficial durante una urgencia real.
- **Sí:** el permiso `auditoria:revisar` separa revisar de consultar.
- **Sí:** la descarga verifica el hash antes de entregar bytes. Un archivo alterado nunca llega al usuario.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| `ICasoAccessReader` se ejecuta en cada acceso clínico (latencia). | Es una consulta indexada por `caso_medico_id`, sin caché (los permisos cambian al revocar). Se mide en la SPEC 13. |
| El `auth_time` de Keycloak no se refresca en el *refresh token* y obliga a reautenticar seguido. | Es el comportamiento buscado para el dictamen. La SPA maneja el `401` con step-up (ARQ §6.2). |
| Abuso de `clinico:emergencia`. | Se otorga usuario por usuario. Cada uso se audita y queda en revisión, y la notificación en tiempo real llega con la SPEC 10. |
| Un coordinador de IPS se asigna casos a sí mismo. | Se acepta: la IPS responde por su personal y la acción queda auditada (`CREAR / ASIGNACION`). |
| La descarga de PDFs grandes consume memoria. | El archivo se descifra en un buffer de 10 MB como máximo (el límite de la SPEC 07). |

---

## Qué **no** está en esta spec

- El plazo de controversia y `EN_FIRME` (SPEC 09).
- La controversia formal, las Juntas y la recalificación.
- La notificación en tiempo real (SPEC 10).
- El acceso del paciente (SPEC 12).
- La copia formal de la HC.
- La firma digital del dictamen.

Cada uno, si se aborda, va en su propia spec.

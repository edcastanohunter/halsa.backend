# SPEC 09 — Worker con Quartz y motor de términos legales

> **Estado:** Implementado
> **Depende de:** SPEC 01, SPEC 02, SPEC 04, SPEC 05, SPEC 07, SPEC 08
> **Fecha:** 2026-10-09
> **Objetivo:** Separar un proceso Worker que ejecute con Quartz los jobs en background, entre ellos un motor que abre, alerta y vence los plazos legales de cada caso como filas calculadas sobre el calendario laboral colombiano.

---

## Por qué existe esta spec

El Decreto 1072/2015, la Res. 1401/2007, el Decreto-ley 019/2012 y la Ley 776/2002 fijan plazos cuyo incumplimiento acarrea sanciones a la ARL.
ARQ §3.4 los modela como **datos** (las reglas y los festivos son filas) con **un solo job barredor**, en lugar de un job por plazo.

Además, varias specs anteriores dejaron tareas para "el Worker":

- drenar el outbox fuera de la API;
- crear las particiones de auditoría (SPEC 04);
- purgar la idempotencia (SPEC 05);
- borrar archivos huérfanos (SPEC 07);
- poner en firme el dictamen al vencer la controversia (SPEC 08).

> ⚠️ Los valores de `sst.reglas_termino` son el punto de partida de ARQ §3.4, **pendientes de validación jurídica** antes de producción.

---

## Alcance

**Incluido:**

- El rol de proceso `Halsa:Rol` = `Api` | `Worker` | `Ambos`. Es la misma imagen; el rol decide qué se registra.
- Quartz.NET con JobStore en PostgreSQL (esquema `quartz`), clustered.
- El rol de base de datos `halsa_worker`, con políticas de RLS propias solo en las tablas de mantenimiento.
- El esquema `sst`, con las tablas `reglas_termino`, `plazos` y `festivos`.
- `CalendarioColombia` (el cálculo de festivos) y `CalculadoraTerminos` (días hábiles, calendario y horas).
- La apertura de plazos por eventos: radicación de AT y de EL, incapacidad inicial y notificación del dictamen.
- `BarridoPlazosJob`: alertas y vencimientos, con eventos en el outbox.
- El cumplimiento automático por evento, y manual con `POST …/plazos/{id}/cumplir`.
- Al vencer `CONTROVERSIA_DICTAMEN`, el dictamen pasa a `EN_FIRME`, y `venceControversiaEn` se expone en el GET del dictamen.
- Los jobs de mantenimiento: festivos, particiones, purga de idempotencia y archivos huérfanos. El `OutboxProcessor` se ejecuta solo en el Worker.
- `GET /v1/casos/{id}/plazos` y `GET /v1/plazos`. El arreglo `plazos` se llena en `GET /v1/integraciones/{furat|furep}/{solicitudId}`.

**Fuera de alcance (specs futuras):**

- La entrega de los eventos `plazo.*` como webhooks → SPEC 10.
- La administración de reglas y festivos por API (aquí se hace por migración o SQL) → spec propia.
- Los contenedores de la API y el Worker en `docker-compose.yml` (y en el VPS) → SPEC 13. En desarrollo se ejecutan con `dotnet run`.
- El registro de una controversia (`EN_CONTROVERSIA`) → fuera de ARQ 01. Sin ella, todo dictamen notificado queda `EN_FIRME` al vencer el plazo.
- La alerta operativa por mensajes del outbox agotados → SPEC 13 (observabilidad).
- Los plazos de reglas que no tienen un evento en el sistema (por ejemplo, la entrega del informe de investigación) → se abren con su evento, en la spec que lo cree.

---

## Modelo de datos

### Proceso

| `Halsa:Rol` | Qué registra |
| --- | --- |
| `Api` | Los endpoints, el middleware y `/health`. Sin hosted services de background. |
| `Worker` | `OutboxProcessor`, Quartz y sus jobs, y solo `/health`. Sin endpoints de negocio. |
| `Ambos` | Todo. Es el valor por defecto solo en `Development`. En `Production`, `Halsa:Rol` es obligatorio. |

> **Ampliado por la SPEC 13:** el valor `Migrador` (aplica las migraciones y termina) y `ProductionGuard`, que rechaza `Ambos` en producción.

### Rol `halsa_worker` (se añade al script de init)

| Objeto | Privilegio | Política de RLS |
| --- | --- | --- |
| `sst.plazos` | `SELECT, UPDATE` | `TO halsa_worker USING (true)`: barrido entre tenants |
| `integracion.solicitudes_idempotentes` | `SELECT, DELETE` | `TO halsa_worker USING (expira_en < now())`: solo las vencidas |
| `clinico.soportes` | `SELECT (id, ruta)` | `TO halsa_worker USING (true)`. `p_soportes` pasa a `TO halsa_owner, halsa_clinico, halsa_ingesta`: llama a `atel.tenant_del_caso_remitido`, que `halsa_worker` no puede ejecutar. |
| `outbox.outbox_messages` | `SELECT, INSERT, UPDATE` | sin RLS |
| `quartz.*` | `SELECT, INSERT, UPDATE, DELETE` | sin RLS |
| `sst.festivos` | `SELECT, INSERT` | sin RLS |
| `sst.reglas_termino` | `SELECT` | sin RLS (global): la necesita `RecalcularPlazosJob` |
| `auditoria.asegurar_particiones` | `EXECUTE` (la función pasa a `SECURITY DEFINER`) | |

Los handlers que reaccionan a eventos (la validación de la SPEC 05, la apertura de plazos y la puesta en firme) siguen usando `halsa_app` con `ISystemTenantScope`, porque el tenant viene en el evento.
Solo los barridos entre tenants usan `halsa_worker` (cadena de conexión `Worker`).

### Tablas `sst`

| Tabla | Columnas | Notas |
| --- | --- | --- |
| `reglas_termino` | `id`, `codigo`, `evento_disparador`, `cantidad`, `unidad` (`HABILES`/`CALENDARIO`/`HORAS`), `alerta_cantidad`, `alerta_unidad`, `base_legal`, `vigente_desde`, `vigente_hasta` | Global, sin tenant. Un `codigo` puede tener varias versiones por vigencia. `halsa_app` tiene `SELECT`. |
| `plazos` | `id`, `tenant_id`, `caso_medico_id`, `regla_id`, `regla_codigo`, `referencia_id` (null), `inicia_en`, `vence_en`, `alerta_en`, `estado` (`ABIERTO`/`CUMPLIDO`/`VENCIDO`/`SUSPENDIDO`), `alertado_en`, `cumplido_en`, `cumplido_por`, `nota_cumplimiento`, `extemporaneo bool`, `radicado`, `ultima_fecha_fin` (null) | `radicado` y `ultima_fecha_fin` (solo `TOPE_IT_180`) se copian del caso y de la cadena al abrir o recalcular, porque `halsa_worker` no lee `atel`. Único `(caso_medico_id, regla_codigo, referencia_id)`. Índice parcial `(vence_en) WHERE estado='ABIERTO'`. RLS por tenant. `halsa_app` tiene `SELECT, INSERT, UPDATE`. |
| `festivos` | `fecha` (PK), `descripcion`, `origen` (`CALCULADO`/`MANUAL`) | Global. El job no pisa las filas `MANUAL`. `halsa_app` tiene `SELECT` (calcula al abrir). |

Reglas semilla (migración; **pendientes de validación jurídica**):

| Código | Disparador | Plazo | Alerta previa | Base legal |
| --- | --- | --- | --- | --- |
| `REPORTE_FURAT` | `CasoRadicado` (AT); inicia en `fecha_evento` | 2 hábiles | 1 hábil | Decreto 1295/1994, art. 62; Res. 156/2005 |
| `REPORTE_FUREP` | `CasoRadicado` (EL); inicia en `fecha_evento` | 2 hábiles | 1 hábil | Ídem |
| `INVESTIGACION_AT` | `CasoRadicado` (AT) | 15 calendario | 3 calendario | Res. 1401/2007; Decreto 1072/2015, art. 2.2.4.6.32 |
| `REMISION_INVESTIGACION_GRAVE` | `CasoRadicado` (AT, grave o mortal) | 15 calendario | 3 calendario | Res. 1401/2007 |
| `CONTROVERSIA_DICTAMEN` | `DictamenPclNotificado`; inicia en `notificado_en` | 10 hábiles | 3 hábiles | Decreto-ley 019/2012, art. 142 |
| `TOPE_IT_180` | `IncapacidadRegistrada` (de la cadena) | 180 días acumulados | 30 días (= día 150) | Ley 776/2002, art. 3 |

### `CalculadoraTerminos`

- La zona horaria es `America/Bogota`. Un plazo **vence al final del día** (23:59:59, hora local) del último día que cuenta.
- `HABILES`: se cuenta desde el día hábil siguiente a `inicia_en`, saltando los sábados, los domingos y `sst.festivos`.
- `CALENDARIO`: se cuenta desde el día siguiente.
- `HORAS`: `inicia_en + n` horas exactas.
- `alerta_en = vence_en − alerta previa`, en la misma unidad de la alerta.
- Ejemplo de ARQ: un evento el viernes antes de un lunes festivo, con 2 hábiles, vence el miércoles a las 23:59:59.

### `CalendarioColombia`

Calcula para un año dado:

- Los festivos fijos: 1 ene, 1 may, 20 jul, 7 ago, 8 dic y 25 dic.
- Los trasladables al lunes (Ley 51/1983): 6 ene, 19 mar, 29 jun, 15 ago, 12 oct, 1 nov y 11 nov.
- Los que dependen de la Pascua: Jueves y Viernes Santo, y Ascensión, Corpus Christi y Sagrado Corazón (que se trasladan al lunes).

### Apertura y cumplimiento

| Evento (outbox) | Acción |
| --- | --- |
| `CasoRadicadoIntegrationEvent` | Abre `REPORTE_FURAT` o `REPORTE_FUREP` y **los evalúa enseguida**: si `radicado_en ≤ vence_en` pasan a `CUMPLIDO`; si no, a `VENCIDO` con `extemporaneo=true` y se emite `PlazoVencido`. Si es AT, abre también `INVESTIGACION_AT` y, si es grave o mortal, `REMISION_INVESTIGACION_GRAVE`. |
| `IncapacidadRegistradaIntegrationEvent` | Abre o recalcula `TOPE_IT_180` (`referencia_id = cadena_id`): `vence_en = fecha_fin + (180 − dias_acumulados)` días, y `alerta_en = fecha_fin + (150 − dias_acumulados)` días. |
| `DictamenPclNotificadoIntegrationEvent` | Abre `CONTROVERSIA_DICTAMEN` (`referencia_id = dictamen_id`). |
| `CasoRechazadoIntegrationEvent` | Pasa los plazos `ABIERTO` del caso a `SUSPENDIDO`. |

- La apertura es idempotente por el índice único: un evento repetido no duplica plazos.
- La regla aplicada es la vigente en `inicia_en`.

### `BarridoPlazosJob` (cada minuto, `[DisallowConcurrentExecution]`, rol `halsa_worker`)

1. En lotes de 500, con `FOR UPDATE SKIP LOCKED`:
   - `ABIERTO`, con `alerta_en ≤ now()` y `alertado_en` nulo → fija `alertado_en` y emite `PlazoProximoAVencerIntegrationEvent`.
   - `ABIERTO`, con `vence_en ≤ now()` → pasa a `VENCIDO` y emite `PlazoVencidoIntegrationEvent`.
2. `TOPE_IT_180` con `ABIERTO` y sin incapacidad nueva más de 30 días después del último `fecha_fin` de la cadena → pasa a `CUMPLIDO`, con la nota `"cadena cerrada antes del tope"`.
3. Los eventos llevan `tenant_id`, `caso_medico_id`, `radicado` y el `PlazoResumen`.

`PlazoVencidoHandler` para `CONTROVERSIA_DICTAMEN`: con `ISystemTenantScope`, pasa el dictamen a `EN_FIRME` y audita `ACTUALIZAR / CASO` como sistema.

`RecalcularPlazosJob` (diario, y después de cargar festivos): recalcula los `ABIERTO` con las reglas y festivos actuales.
**Nunca acorta un plazo**: `vence_en = max(actual, recalculado)`.

### Jobs de mantenimiento

| Job | Programación | Acción |
| --- | --- | --- |
| `FestivosJob` | Diario 02:00 | Asegura los festivos `CALCULADO` del año actual y los 2 siguientes. Si inserta alguno, dispara `RecalcularPlazosJob`. |
| `ParticionesAuditoriaJob` | Diario 03:00 | `SELECT auditoria.asegurar_particiones(3)` |
| `PurgaIdempotenciaJob` | Cada hora | `DELETE` de las llaves con `expira_en < now()` |
| `ArchivosHuerfanosJob` | Cada hora | Borra los archivos de `Archivos:Raiz` de más de 1 h sin fila en `clinico.soportes`. Nunca borra un archivo con metadata. |
| `OutboxProcessor` | Continuo (hosted service) | Igual que hoy, pero solo con el rol `Worker` |

### Endpoints

| Método y ruta | Permiso | Respuesta |
| --- | --- | --- |
| `GET /v1/casos/{id}/plazos` | `plazos:read` | `PlazoResponse[]`: regla, base legal, `iniciaEn`, `venceEn`, estado, `extemporaneo` y `cumplidoEn` |
| `GET /v1/plazos?estado=&venceAntesDe=&regla=&page=` | `plazos:read`, actor `Arl` | Paginado y limitado al tenant |
| `POST /v1/casos/{id}/plazos/{plazoId}/cumplir` | `plazos:write` (permiso nuevo, en `analista-arl`) | `{ nota (≥ 10 caracteres), cumplidoEn? }`. Solo desde `ABIERTO` o `VENCIDO` (un plazo `VENCIDO` cumplido tarde queda `extemporaneo=true`). Responde `204` y audita `ACTUALIZAR / PLAZO`. |

`RecursoAuditoria` gana el valor `Plazo` (se ajusta la SPEC 04).

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Añadir `Halsa:Rol` y el registro condicional de los hosted services, los endpoints y Quartz (mover `OutboxProcessor` al rol `Worker`). Tests: con `Api` no hay `IHostedService` de outbox; con `Worker`, `/v1/casos` responde `404`.
2. Crear `CalendarioColombia` y `CalculadoraTerminos` en `Domain/Terminos`. Tests unitarios: los festivos de 2026 y 2027 contra la lista oficial, y el caso del viernes y el lunes festivo de ARQ.
3. Crear la migración `SstTerminos` (tablas, reglas semilla, RLS y `GRANT`), el rol `halsa_worker` en el script de init, sus políticas y la cadena `Worker`.
4. Crear la migración `Quartz`, con el script oficial de tablas de Quartz para PostgreSQL en el esquema `quartz`. Configurar Quartz (clustered, JobStore en PostgreSQL, serialización `System.Text.Json`) y `FestivosJob`. Test de integración: el job llena 3 años de festivos y no pisa uno `MANUAL`.
5. Crear los handlers de apertura de plazos para los cuatro eventos, y el evento `CasoRechazado`. Tests de integración, incluida la idempotencia.
6. Crear `BarridoPlazosJob` y los eventos `PlazoProximoAVencer` y `PlazoVencido`. Tests de integración con un `TimeProvider` falso.
7. Crear `PlazoVencidoHandler` (dictamen a `EN_FIRME`), `RecalcularPlazosJob` y `venceControversiaEn` en el GET del dictamen.
8. Crear `ParticionesAuditoriaJob`, `PurgaIdempotenciaJob` y `ArchivosHuerfanosJob`. Tests de integración.
9. Crear los endpoints de plazos y el permiso `plazos:write`. Llenar `plazos` en la consulta de estado del FURAT/FUREP. Tests funcionales. Actualizar `docs/api/atel-api.openapi.yaml`.

---

## Criterios de aceptación

- [x] `CalculadoraTerminos`: un plazo de 2 días hábiles que inicia el viernes anterior a un lunes festivo vence el miércoles a las 23:59:59 `-05:00`.
- [x] `CalendarioColombia.Festivos(2026)` coincide con la lista oficial de 18 festivos de 2026.
- [x] Un FURAT de un evento del lunes, radicado el martes, deja `REPORTE_FURAT` `CUMPLIDO`. Uno radicado el jueves siguiente lo deja `VENCIDO` con `extemporaneo=true`.
- [x] Un FURAT con `esGrave=true` abre `REMISION_INVESTIGACION_GRAVE`, y uno con `esGrave=false` no lo abre.
- [x] Procesar dos veces el mismo `CasoRadicadoIntegrationEvent` deja un solo plazo por regla.
- [x] Con el reloj en `alerta_en`, el barrido emite exactamente un `PlazoProximoAVencerIntegrationEvent`. Un segundo barrido no emite otro.
- [x] Con el reloj después de `vence_en`, el plazo pasa a `VENCIDO` y se emite un `PlazoVencidoIntegrationEvent`.
- [x] Al vencer `CONTROVERSIA_DICTAMEN`, el dictamen queda `EN_FIRME`, y `GET …/dictamen-pcl` devolvía antes `venceControversiaEn` con la fecha del plazo.
- [x] Una cadena de incapacidades con 120 días acumulados tiene `TOPE_IT_180` con `vence_en = fecha_fin + 60 días`. Al cargar una prórroga de 30, se recalcula a `fecha_fin' + 30`.
- [x] Insertar un festivo `MANUAL` dentro de un plazo abierto y ejecutar `RecalcularPlazosJob` alarga ese `vence_en` un día. Borrar el festivo y recalcular no lo acorta.
- [x] Como `halsa_app` con `app.tenant_id='ARL_A'`, no se ven plazos de `ARL_B`. Como `halsa_worker`, el barrido sí los procesa.
- [x] Como `halsa_worker`, `SELECT` sobre `atel.casos_medicos` falla con `permission denied`.
- [x] Con dos instancias del Worker en paralelo, cada plazo vencido genera exactamente un evento.
- [x] `PurgaIdempotenciaJob` borra una llave con `expira_en` vencida y conserva una vigente.
- [x] `ArchivosHuerfanosJob` borra un archivo sin metadata de hace 2 h y conserva uno con metadata y uno sin metadata de hace 10 min.
- [x] Con `Halsa:Rol=Api`, la API no procesa el outbox. Con `Worker`, sí.
- [x] `POST …/plazos/{id}/cumplir` con una nota de 5 caracteres responde `400`. Con una nota válida responde `204`, el plazo queda `CUMPLIDO` y hay una fila `ACTUALIZAR / PLAZO`.
- [x] `GET /v1/integraciones/furat/{solicitudId}` incluye los plazos abiertos de ese caso.

---

## Decisiones

- **Sí:** Quartz.NET con JobStore en PostgreSQL, clustered (ARQ). Con una réplica en el VPS no cuesta nada, y escalar después no cambia el código.
- **No:** Hangfire (ARQ §9).
- **Sí:** la misma imagen con `Halsa:Rol`. Escalar la API no multiplica los jobs.
- **Sí:** el rol `halsa_worker`, con políticas `TO halsa_worker` solo en las tablas que se barren entre tenants. **No:** `BYPASSRLS`, ni recorrer los tenants uno por uno sin un catálogo de tenants.
- **Sí:** los handlers de eventos usan `halsa_app` con `ISystemTenantScope`, porque el tenant viene en el evento. Solo los barridos usan `halsa_worker`.
- **Sí:** los festivos se calculan con un algoritmo, se guardan en tabla y admiten overrides `MANUAL`. **No:** cargarlos a mano cada año, porque un olvido silencioso corrompe todos los plazos.
- **Sí:** los plazos vencen al final del día local (America/Bogota), como se interpretan los términos en días.
- **Sí:** los plazos se abren de forma asíncrona desde el outbox, de forma idempotente. **No:** abrirlos dentro de la transacción de radicación, que obligaría a dar a `halsa_ingesta` permisos sobre `sst`. **Cambio respecto a ARQ §3.4** (que los abría en la misma transacción). El `202` del FURAT devuelve `plazos=[]`, y la consulta de estado los muestra.
- **Sí:** los plazos de reporte se evalúan al abrirse. Si la radicación ya llegó tarde, el plazo nace `VENCIDO` con `extemporaneo=true`.
- **Sí:** `TOPE_IT_180` se recalcula con cada incapacidad de la cadena y se cierra si la cadena se corta más de 30 días.
- **Sí:** al vencer la controversia, el dictamen pasa a `EN_FIRME`. No existe un registro de controversia en ARQ 01.
- **Sí:** un plazo nunca se acorta retroactivamente (ARQ §3.4).
- **Sí:** el cumplimiento manual exige una nota y queda auditado. El permiso nuevo `plazos:write` se añade a `analista-arl` (se ajusta la SPEC 01).

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Reglas o festivos mal parametrizados generan sanciones por vencimientos no alertados. | Validación jurídica antes de producción, un test por regla, un test de los festivos contra la lista oficial y overrides `MANUAL`. |
| El Worker está caído y no se alertan vencimientos. | `/health` del Worker, y Quartz recupera los *misfires* al volver. El barrido procesa todo lo vencido, sin importar cuánto tiempo estuvo caído. La alerta de disponibilidad va en la SPEC 13. |
| `halsa_worker` tiene visibilidad entre tenants sobre `sst.plazos`. | Solo `SELECT/UPDATE` de esa tabla y ningún acceso a `atel`, `clinico` (salvo `id` y `ruta` de soportes) ni a la auditoría. |
| El cálculo de `TOPE_IT_180` por días acumulados no refleja interrupciones parciales. | Está documentado como aproximación. La alerta exacta por cada carga se mantiene en la respuesta de la SPEC 07. |
| `ArchivosHuerfanosJob` borra un archivo cuya transacción todavía está en curso. | El umbral es de 1 h, muy por encima de la duración de una transacción de carga. |

---

## Qué **no** está en esta spec

- Los webhooks de plazos (SPEC 10).
- La administración de reglas y festivos por API.
- Los contenedores de la API y el Worker en el compose del VPS (SPEC 13).
- La controversia formal.
- Las alertas operativas (SPEC 13).

Cada uno, si se aborda, va en su propia spec.

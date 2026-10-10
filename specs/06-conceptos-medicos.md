# SPEC 06 — Carga de conceptos médicos desde las IPS y el backoffice

> **Estado:** Borrador
> **Depende de:** SPEC 01, SPEC 02, SPEC 03, SPEC 04, SPEC 05
> **Fecha:** 2026-10-09
> **Objetivo:** Que una IPS (por integración) o un médico autorizado (por el backoffice) registren conceptos médicos, evoluciones y conceptos de rehabilitación sobre un caso visible para ellos, cifrados, auditados y firmados con la identidad verificada del profesional.

---

## Por qué existe esta spec

ARQ 01 pide "endpoints para la carga de conceptos médicos", pero el roadmap original no los cubría.
Es la primera vez que la API **escribe** HC por fuera de la radicación.
Reutiliza todo lo anterior: la RLS por remisión, el cifrado, la auditoría transaccional y la idempotencia de las integraciones.

La lectura de la HC no está en esta spec.
Exige ABAC por asignación y step-up, y va en la SPEC 08.

---

## Alcance

**Incluido:**

- `POST /v1/integraciones/conceptos-medicos`: la service account de una IPS, idempotente, en `IngestaDbContext`.
- `POST /v1/casos/{casoId}/registros-clinicos`: personal médico con `clinico:write`, en `ClinicalDbContext`.
- Los tipos admitidos: `CONCEPTO_MEDICO`, `EVOLUCION` y `CONCEPTO_REHABILITACION`, y la corrección de un registro anterior (`corrigeA`).
- La identidad del profesional del backoffice, tomada de claims de Keycloak (`rethus`, `tipo_documento` y `numero_documento`).
- La validación de fechas contra el caso y de los códigos CIE-10 por formato.
- La actualización de `docs/api/atel-api.openapi.yaml` con los dos contratos.

**Fuera de alcance (specs futuras):**

- La lectura de la HC (`GET /v1/casos/{id}/registros-clinicos`) → SPEC 08.
- La exigencia de estar asignado al caso → SPEC 08, solo para la lectura.
- El dictamen PCL (`DICTAMEN_PCL`) → SPEC 08.
- Los adjuntos PDF de los conceptos → se añaden después de la SPEC 07, reutilizando su almacenamiento de archivos.
- La notificación a la ARL de un concepto nuevo → SPEC 10.
- La deduplicación de negocio por número de documento de la IPS: aquí solo hay idempotencia por llave.
- Un formato FHIR (`Composition`) en la entrada → diferido.

---

## Modelo de datos

Esta spec no crea tablas.
Escribe en `clinico.historia_clinica_detalle` (SPEC 03), `integracion.solicitudes_idempotentes` (SPEC 05) y `auditoria.accesos` (SPEC 04).

### Contrato de integración: `POST /v1/integraciones/conceptos-medicos`

Headers: `Idempotency-Key` (obligatorio, UUID) y `X-Correlation-Id` (opcional).

```json
{
  "version": "1.0",
  "casoId": "0192f1e4-7c1a-7b2e-9f00-3c1d2e4f5a6b",
  "tipo": "CONCEPTO_MEDICO",
  "atendidoEn": "2026-10-09T10:20:00-05:00",
  "corrigeA": null,
  "profesional": {
    "identificacion": { "tipo": "CC", "numero": "79111222" },
    "nombreCompleto": "Ana Ruiz",
    "registroRethus": "RM-12345",
    "especialidad": "Ortopedia"
  },
  "contenido": {
    "motivo": "Control postquirúrgico",
    "anamnesis": "…", "examenFisico": "…", "analisis": "…", "plan": "…",
    "diagnosticos": [{ "sistema": "CIE-10", "codigo": "S62.6", "descripcion": "Fractura de otro dedo de la mano" }],
    "observaciones": null
  }
}
```

Respuesta `201`:

```json
{ "registroId": "…", "casoId": "…", "registradoEn": "…" }
```

Va con el header `Location: /v1/casos/{casoId}/registros-clinicos/{registroId}`.
Esa ruta todavía no responde: llega con la SPEC 08.

### Contrato del backoffice: `POST /v1/casos/{casoId}/registros-clinicos`

El cuerpo es igual al de la integración, pero **sin** `version`, `casoId` ni `profesional`.
El profesional sale del token.
La respuesta es la misma (`201`).

### Reglas comunes

| Regla | Error |
| --- | --- |
| `tipo ∈ { CONCEPTO_MEDICO, EVOLUCION, CONCEPTO_REHABILITACION }` | `400`, error en `tipo` |
| Al menos uno entre `motivo`, `anamnesis`, `examenFisico`, `analisis` y `plan` tiene texto. Cada campo tiene 8000 caracteres como máximo. | `400` |
| `diagnosticos[].sistema = "CIE-10"` y `codigo` cumple `^[A-Z][0-9]{2}(\.[0-9A-Z]{1,4})?$` | `400` |
| `atendidoEn` no es futuro | `400` |
| `atendidoEn ≥ fecha_evento` del caso (menos 1 día de tolerancia) | `400` con `Clinico.FechaAtencionAnteriorAlEvento` |
| El caso existe y es visible para el actor (por tenant o por remisión vigente) | `404` con `Clinico.CasoNoEncontrado` |
| El caso no está `Rechazado` ni `Cerrado` | `409` con `Clinico.CasoNoAdmiteRegistros` |
| `corrigeA`, si viene, existe en el mismo caso y es visible | `404` con `Clinico.RegistroNoEncontrado` |
| `registroRethus` no está vacío (en la integración) | `400` |

### Actores y permisos

| Canal | Permiso | Actor válido | Contexto de BD |
| --- | --- | --- | --- |
| Integración | `integracion:clinico:write` | `Integracion` con `ips_id` | `IngestaDbContext` (`halsa_ingesta`) |
| Backoffice | `clinico:write` | `Arl` (médico de la ARL del caso) o `Ips` (con remisión vigente) | `ClinicalDbContext` (`halsa_clinico`) |

En los dos canales, un actor válido que no es del tipo esperado recibe `403` con `Clinico.ActorNoAutorizado`.

### Identidad del profesional (backoffice)

Se añaden al realm `halsa` los mappers *User Attribute* `rethus`, `tipo_documento` y `numero_documento`, como claims planos.
Datos semilla:

| Usuario | `tipo_documento` | `numero_documento` | `rethus` |
| --- | --- | --- | --- |
| `calificador.arla` | `CC` | `52000111` | `RM-DEV-0001` |
| `medico.ips` | `CC` | `80000222` | `RM-DEV-0002` |

`IProfesionalActual` (en Infrastructure) construye el `ProfesionalRef` a partir de `sub` y esos claims.
Si falta alguno, el comando falla con `Clinico.ProfesionalSinRegistro` (`403`).

### Flujo de la integración

1. Hay idempotencia por `(emisor = "ips:{REPS}", key)`, con la misma semántica que la SPEC 05 (mismo cuerpo → misma respuesta; otro cuerpo → `409`).
2. En una transacción de `IngestaDbContext`:
   - se lee el caso (la RLS por `app.ips_id` solo devuelve los casos remitidos);
   - se aplican las reglas;
   - se cifra con la DEK del **tenant del caso**;
   - se inserta el registro;
   - se inserta la fila de idempotencia (`tenant_id` del caso);
   - se audita `CREAR` (o `CORREGIR`) sobre `REGISTRO_CLINICO`.
3. La política de RLS de la HC (`WITH CHECK` con `atel.tenant_del_caso_remitido`) impide escribir con un tenant que no sea el del caso.

### Flujo del backoffice

El endpoint obtiene el `ProfesionalRef` y envía `RegistrarRegistroClinicoCommand` o `CorregirRegistroClinicoCommand` (SPEC 03).
Esos comandos ya cifran, auditan (SPEC 04) y validan la visibilidad con `IClinicalCaseGate`.
Esta spec les añade dos reglas: la fecha contra el caso y el estado del caso. Para eso, `IClinicalCaseGate` pasa a devolver `(TenantId, DateTimeOffset FechaEvento, EstadoCaso)`.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Añadir los mappers y los atributos de profesional a `docker/keycloak/halsa-realm.json`, y crear `IProfesionalActual`. Tests unitarios con un principal con y sin esos claims.
2. Ampliar `IClinicalCaseGate` con la fecha del evento y el estado, y añadir a los comandos de la SPEC 03 las reglas de fecha y de estado. Tests de integración.
3. Crear el endpoint `POST /v1/casos/{casoId}/registros-clinicos` en `Endpoints/Clinico/`, con `.Auditable(RecursoAuditoria.RegistroClinico)`, y `Tags.Clinico`. Tests funcionales del backoffice.
4. Crear `RecibirConceptoMedicoCommand`, su validador y su handler en `Application/Integraciones/Conceptos`, sobre `IngestaDbContext`. Tests de integración, incluida la idempotencia.
5. Crear el endpoint `POST /v1/integraciones/conceptos-medicos`. Tests funcionales.
6. Añadir a `docs/api/atel-api.openapi.yaml` los schemas `ConceptoMedicoRequest`, `RegistroClinicoRequest`, `ContenidoClinico` y `RegistroClinicoCreadoResponse`, y las dos rutas. Verificar que coinciden con `/openapi/v1.json`.

---

## Criterios de aceptación

- [ ] Con el token de `int-ips-110011234501`, `POST /v1/integraciones/conceptos-medicos` sobre un caso de `ARL_B` con remisión vigente responde `201`, y el registro queda con `tenant_id='ARL_B'` y `contenido_key_id` de `ARL_B`.
- [ ] El mismo POST sobre un caso sin remisión para esa IPS responde `404` con `Clinico.CasoNoEncontrado`.
- [ ] Después de revocar la remisión, un POST nuevo sobre ese caso responde `404`.
- [ ] Repetir el POST con la misma `Idempotency-Key` y el mismo cuerpo responde `201` con el mismo `registroId`, y deja una sola fila en la HC.
- [ ] La misma llave con otro cuerpo responde `409` con `Integracion.IdempotenciaConflicto`.
- [ ] La llave `K` usada por `int-arl-ARL_A` en un FURAT y por `int-ips-110011234501` en un concepto no choca: ambas responden con éxito.
- [ ] `POST /v1/casos/{id}/registros-clinicos` como `medico.ips` (con remisión) responde `201`, y el registro tiene `profesional_registro_rethus='RM-DEV-0002'` y `profesional_sub` igual al `sub` del token.
- [ ] El mismo POST con un principal con `clinico:write` pero sin el claim `rethus` responde `403` con `Clinico.ProfesionalSinRegistro`.
- [ ] El mismo POST como `analista.arla` (sin `clinico:write`) responde `403` y deja una fila de auditoría `DENEGADO`.
- [ ] Como `calificador.arla`, el POST sobre un caso de `ARL_B` responde `404`.
- [ ] `tipo=DICTAMEN_PCL` o `tipo=REPORTE_INICIAL` responde `400`.
- [ ] `atendidoEn` dos días antes de la `fecha_evento` del caso responde `400` con `Clinico.FechaAtencionAnteriorAlEvento`.
- [ ] Un caso `Rechazado` responde `409` con `Clinico.CasoNoAdmiteRegistros`.
- [ ] Los diagnósticos `S62` y `S62.6` son válidos, y `ABC.1` y `S626` responden `400`.
- [ ] Un POST con `corrigeA` crea un registro nuevo con `corrige_a` informado, deja el original intacto y audita `CORREGIR`.
- [ ] Cada registro exitoso deja exactamente una fila de auditoría `CREAR` o `CORREGIR` sobre `REGISTRO_CLINICO`, con el `tenant_id` del caso.
- [ ] `/openapi/v1.json` contiene las dos rutas nuevas, y sus schemas coinciden con los de `docs/api/atel-api.openapi.yaml`.

---

## Decisiones

- **Sí:** dos canales, integración IPS y backoffice. **No:** la lectura de la HC en esta spec, porque sin ABAC por asignación ni step-up sería una puerta más ancha de lo que permite la Res. 1995/1999.
- **Sí:** la integración usa `IngestaDbContext` (`halsa_ingesta`), así que la idempotencia y el registro son atómicos. **No:** dar a `halsa_clinico` acceso a `integracion`.
- **Sí:** la tabla de idempotencia tiene la clave `(emisor, key)`, y las IPS se identifican como `ips:{REPS}` (se ajustó la SPEC 05).
- **Sí:** la identidad del profesional del backoffice sale de claims de Keycloak. **No:** que viaje en el payload, porque cualquier usuario con `clinico:write` podría firmar como otro médico.
- **Sí:** en la integración, el profesional viene en el payload. La IPS responde por su personal, y la service account queda en `profesional_sub` y en la auditoría.
- **Sí:** para escribir basta con el tenant o la remisión vigente. **No:** exigir la asignación también para escribir. La asignación protege la lectura (SPEC 08).
- **Sí:** se añade `AtendidoEn` a `HistoriaClinicaDetalle`, separado de `RegistradoEn` (se ajustó la SPEC 03). La HC registra cuándo se atendió, no solo cuándo se cargó.
- **Sí:** respuesta síncrona `201`. No hay enriquecimiento posterior que justifique un `202`.
- **No:** adjuntos PDF ahora. El almacenamiento de archivos se diseña una sola vez en la SPEC 07.
- **No:** deduplicación por número de documento de la IPS. La idempotencia por llave cubre los reintentos, y la HC admite varios conceptos legítimos el mismo día.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Una IPS sube conceptos a un caso justo después de que se revoca su remisión (carrera). | La RLS evalúa la remisión dentro de la transacción del insert. Si la revocación confirmó antes, el insert falla. |
| Los atributos de profesional en Keycloak quedan desactualizados (un RETHUS cambia). | El registro guarda el valor del momento. La verificación contra RETHUS queda fuera de alcance y se anota para una spec de alta de profesionales. |
| `halsa_ingesta` escribe HC para cualquier caso remitido a la IPS del token. | Es la regla esperada, y la RLS con `WITH CHECK` impide desviar el tenant. Cada escritura queda auditada con `client_id`. |
| El CIE-10 colombiano admite códigos que la regex rechaza. | La regex es deliberadamente amplia (de 1 a 4 caracteres después del punto). La validación contra catálogo va en su propia spec. |

---

## Qué **no** está en esta spec

- La lectura de la HC, el ABAC por asignación y el step-up (SPEC 08).
- El dictamen PCL (SPEC 08).
- Los adjuntos PDF (después de la SPEC 07).
- La notificación a la ARL (SPEC 10).
- La deduplicación de negocio de documentos de la IPS.
- La entrada en FHIR.

Cada uno, si se aborda, va en su propia spec.

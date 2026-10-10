# SPEC 07 — Carga de incapacidades con soporte PDF y step-up

> **Estado:** Borrador
> **Depende de:** SPEC 01, SPEC 02, SPEC 03, SPEC 04, SPEC 05, SPEC 06
> **Fecha:** 2026-10-09
> **Objetivo:** Que una IPS o un médico con MFA reciente carguen un certificado de incapacidad temporal con su PDF a `POST /v1/casos/{casoId}/incapacidades`. El PDF se analiza con antivirus y se guarda cifrado; las reglas de prórroga se validan sin descifrar diagnósticos; y la ARL ve solo los datos administrativos y las alertas de días acumulados.

---

## Por qué existe esta spec

La incapacidad temporal es lo que más consultan las ARL para pagar prestaciones (Ley 776/2002).
Es el primer flujo que mezcla un documento binario, datos clínicos y reglas legales.

También es el primer endpoint que exige **MFA a los usuarios humanos** (`acr ≥ 2`).
Por eso esta spec construye la base genérica de step-up (RFC 9470), y la SPEC 08 la reutiliza para el dictamen PCL.

---

## Alcance

**Incluido:**

- **Step-up genérico:** `StepUpRequirement` (`acr` y, opcionalmente, la antigüedad de `auth_time`), la respuesta `401` con `WWW-Authenticate` y el OTP con el mapeo ACR→LoA en el realm `halsa` de desarrollo.
- **Archivos:** `IFileStore` y `LocalEncryptedFileStore` (un volumen de Docker, AES-GCM con la DEK del tenant y propósito `SOPORTES`), más la tabla `clinico.soportes`.
- **Antivirus:** el servicio ClamAV (`clamd`) en `docker-compose.yml`, más `IAntivirusScanner`, `ClamAvScanner` (síncrono y que falla cerrado) y su health check.
- **Dominio:** el agregado `Incapacidad`, con sus reglas de solapamiento, prórroga y estado del caso, y las alertas de acumulado y de carga extemporánea.
- `POST /v1/casos/{casoId}/incapacidades` (multipart, idempotente) para el personal y para las integraciones IPS.
- `GET /v1/casos/{casoId}/incapacidades` (solo datos administrativos).
- El diagnóstico y las observaciones como un registro de HC `CERTIFICADO_INCAPACIDAD`.
- El evento `IncapacidadRegistradaIntegrationEvent` en el outbox.
- La actualización de `docs/api/atel-api.openapi.yaml`.

**Fuera de alcance (specs futuras):**

- La descarga del PDF soporte (es un acceso clínico) → SPEC 08.
- El ABAC por asignación y el dictamen PCL → SPEC 08.
- El plazo `TOPE_IT_180` y la alerta por job → SPEC 09. Aquí las alertas solo se calculan en la respuesta.
- La limpieza de archivos huérfanos → SPEC 09 (job).
- Los estados `EN_REVISION`, `APROBADA_PAGO` y `RECHAZADA`, la liquidación y el pago → fuera de ARQ 01.
- El webhook de incapacidad registrada → SPEC 10.
- La validación jurídica de los umbrales y las reglas → antes de producción (igual que `sst.reglas_termino`).
- Migrar los archivos a MinIO o S3 → spec propia, si el volumen local se queda corto.
- La verificación de la firma digital del PDF → fuera de ARQ 01.

---

## Modelo de datos

### Step-up

```csharp
public sealed record StepUpRequirement(int AcrMinimo, TimeSpan? MaxAntiguedadAuth) : IAuthorizationRequirement;
public sealed class StepUpRequerido(IAuthorizationHandler h, int acr, TimeSpan? maxAge) : AuthorizationFailureReason(h, "…");
```

- `StepUpHandler` solo aplica a actores `Arl` e `Ips`. Los actores `Integracion` lo cumplen siempre.
- Si `acr` (de `"1"` o `"2"`) es menor que el mínimo, o si `auth_time` es más antiguo que `MaxAntiguedadAuth`, falla con `StepUpRequerido`.
- `AuditingAuthorizationResultHandler` (SPEC 04) convierte `StepUpRequerido` en `401`, con el header `WWW-Authenticate: Bearer error="insufficient_user_authentication", acr_values="2"` (más `max_age=…` si aplica), el código `Auth.StepUpRequerido` y una fila `DENEGADO` con `motivo='step-up'`.
- La política `Policies.IncapacidadEscritura` es `incapacidades:write` + `StepUpRequirement(2, null)`.

Realm `halsa` de desarrollo:

- Un flujo *browser* con condiciones por LoA: nivel 1 es la contraseña y nivel 2 es OTP. El mapeo ACR→LoA es `{"1":1,"2":2}`.
- `calificador.arla` y `medico.ips` tienen una credencial OTP precargada, con un secreto documentado en el README solo para desarrollo.
- En el flujo de desarrollo, `analista.arla` no tiene OTP.

### Archivos

```csharp
public interface IFileStore   // Application/Abstractions/Archivos
{
    Task<ArchivoGuardado> GuardarAsync(TenantId tenant, Guid archivoId, Stream contenido, CancellationToken ct);
    Task<Stream> AbrirAsync(TenantId tenant, Guid archivoId, string keyId, CancellationToken ct);
}
public sealed record ArchivoGuardado(string Ruta, string KeyId, byte[] Sha256Claro, long Tamano);
```

- La ruta es `{Archivos:Raiz}/{tenant}/{yyyy}/{MM}/{archivoId}.bin`. El contenido es `nonce ‖ ciphertext ‖ tag` (AES-256-GCM), con la AAD `"{tenant}|{archivoId}|soporte"` y la DEK de propósito `SOPORTES` (se añade a la SPEC 03).
- En desarrollo, `Archivos:Raiz` es `./.data/archivos` (en `.gitignore`). En el VPS es el volumen `halsa.backend-archivos`.

Tabla `clinico.soportes` (migración de `ClinicalDbContext`):

| Columna | Notas |
| --- | --- |
| `id`, `tenant_id`, `caso_medico_id`, `tipo` (`INCAPACIDAD`) | |
| `ruta`, `key_id`, `sha256_claro bytea`, `tamano bigint`, `subido_en`, `retener_hasta` | `retener_hasta` es `subido_en` + 15 años |

RLS igual a la de la HC. Hay trigger de inmutabilidad.
`halsa_ingesta` tiene `INSERT`, y `halsa_clinico` tiene `SELECT` (lo usa la SPEC 08).

### Antivirus

- El servicio `halsa.backend.antivirus` usa la imagen `clamav/clamav:1.4` y expone el puerto 3310 solo a la red interna.
- `ClamAvScanner` implementa el protocolo `INSTREAM` de `clamd` sobre TCP, con su propio cliente en `Infrastructure/Archivos/`. Tiene un timeout de 30 s.
- El resultado es `Limpio`, `Infectado(firma)` o `NoDisponible`. El health check `antivirus` reporta `Degraded`.

### Incapacidad (`Halsa.Backend.Domain.Incapacidades`)

Tabla `atel.incapacidades`:

| Columna | Notas |
| --- | --- |
| `id`, `tenant_id`, `caso_medico_id` | |
| `numero_certificado varchar(40)`, `ips_emisora_reps`, `ips_emisora_nombre` | Único `(tenant_id, ips_emisora_reps, numero_certificado)` |
| `tipo` (`INICIAL` / `PRORROGA`), `prorroga_de` (null), `cadena_id` | `cadena_id` es el id de la `INICIAL` de la cadena |
| `fecha_inicio date`, `dias int` (1–30), `fecha_fin date` | `fecha_fin = fecha_inicio + dias − 1`, calculada por el servidor |
| `dias_acumulados int` | La suma de la cadena, incluida esta incapacidad |
| `modalidad` (`PRESENCIAL` / `TELEMEDICINA`), `estado` (`REGISTRADA`) | |
| `profesional_tipo_documento`, `profesional_numero_documento`, `profesional_nombre`, `profesional_registro_rethus` | |
| `diagnostico_categoria_indice bytea` | HMAC (con la DEK `INDICE` del tenant) de `"CIE-10|{3 primeros caracteres}"` |
| `soporte_id`, `registro_clinico_id` | Ids en `clinico` (sin FK) |
| `registrada_en`, `registrada_por` (`sub`), `client_id` | |

RLS igual a la de `casos_medicos` (tenant o remisión vigente).
`halsa_app` tiene `SELECT`, y `halsa_ingesta` tiene `SELECT, INSERT`.

### Reglas (`422`, pendientes de validación jurídica)

| Código | Regla |
| --- | --- |
| `CASO_CERRADO` | El caso está `Cerrado` o `Rechazado`. |
| `INCAPACIDAD_SOLAPADA` | `[fecha_inicio, fecha_fin]` se cruza con otra incapacidad del mismo caso. |
| `PRORROGA_SIN_INICIAL` | `tipo=PRORROGA` sin `prorrogaDe`, o `prorrogaDe` no existe en el caso. |
| `PRORROGA_NO_ES_ULTIMA` | `prorrogaDe` no es la última incapacidad de su cadena. |
| `PRORROGA_DISCONTINUA` | Hay más de 30 días entre `fecha_fin` de `prorrogaDe` y `fecha_inicio` (D. 1427/2022). Es un parámetro: `Incapacidades:MaxDiasEntrePrórrogas`. |
| `PRORROGA_DIAGNOSTICO_NO_RELACIONADO` | `diagnostico_categoria_indice` es distinto del de la `INICIAL` de la cadena. |
| `FECHA_ANTERIOR_AL_EVENTO` | `fecha_inicio` es anterior a la fecha del evento del caso. |

Alertas en la respuesta (no bloquean):

| Alerta | Condición |
| --- | --- |
| `ACUMULADO_150_DIAS` | `dias_acumulados ≥ 150` |
| `ACUMULADO_180_DIAS` | `dias_acumulados ≥ 180` |
| `ACUMULADO_540_DIAS` | `dias_acumulados ≥ 540` |
| `CARGA_EXTEMPORANEA` | `registrada_en − fecha_inicio > Incapacidades:DiasCargaExtemporanea` (30 por defecto) |

### Parte clínica

Se crea un registro de HC de tipo `CertificadoIncapacidad` (un tipo nuevo, que se añade a la SPEC 03) con:

- `Diagnosticos` = el principal más los relacionados (hasta 3).
- `Observaciones` = el valor del request.
- `AtendidoEn` = `fecha_inicio` a las 00:00 `-05:00`.
- `Profesional` = el del payload si la carga viene por integración, o el de los claims (`IProfesionalActual`, SPEC 06) si viene del backoffice.

### Endpoint de carga

`POST /v1/casos/{casoId}/incapacidades` recibe `multipart/form-data` con exactamente dos partes:

- `datos` (`application/json`, con el `IncapacidadRequest` del contrato).
- `soporte` (`application/pdf`, de 10 MB como máximo).

| Paso | Detalle | Error |
| --- | --- | --- |
| 1 | Política `IncapacidadEscritura` (permiso + step-up para humanos). Actor `Arl`, `Ips` o `Integracion` con `ips_id`. | `403` / `401` step-up |
| 2 | `Idempotency-Key`. Emisor: `tenant:{ARL}`, `ips:{REPS}` o `sub:{sub}` (para los humanos). | `400` / `409` |
| 3 | Las partes y los tamaños (límite del request: 12 MB; la parte `soporte`: 10 MB). | `400` / `413` |
| 4 | Los primeros bytes son `%PDF-`. | `415` con `Archivos.NoEsPdf` |
| 5 | Se valida `datos` con FluentValidation. `profesional` es obligatorio solo en la integración. | `400` |
| 6 | ClamAV analiza el archivo. | `422` con `Archivos.SoporteInfectado` / `503` con `Archivos.AntivirusNoDisponible` |
| 7 | Se cifra y se guarda el archivo (fuera de la transacción). | `500` |
| 8 | Transacción en `IngestaDbContext`: se lee el caso (RLS), se aplican las reglas y se insertan la HC, `soportes`, `incapacidades`, la idempotencia, la auditoría (`CREAR / INCAPACIDAD` y `CREAR / REGISTRO_CLINICO`) y el outbox. | `404` / `422` |
| 9 | Responde `201 IncapacidadResponse` con el header `Location`. | |

### Endpoint de consulta

`GET /v1/casos/{casoId}/incapacidades` exige `incapacidades:read`, con actor `Arl` o `Ips` (por remisión).
Devuelve `IncapacidadResponse[]` ordenadas por `fecha_inicio`, con las alertas calculadas, sin diagnósticos y sin datos del profesional.
Se audita como `LEER / INCAPACIDAD` (`IAuditedQuery`).

`RecursoAuditoria` gana los valores `Incapacidad` y `Soporte` (se ajusta la SPEC 04).

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. **Step-up.** Configurar en `halsa-realm.json` el flujo por LoA, el mapeo ACR y el OTP de los usuarios médicos. Crear `StepUpRequirement`, `StepUpHandler` y `StepUpRequerido`, y adaptar `AuditingAuthorizationResultHandler` para la respuesta `401`. Tests unitarios del handler y un test funcional con un endpoint de prueba (que existe solo en el ensamblado de tests).
2. **Antivirus.** Añadir el servicio a `docker-compose.yml`. Crear `IAntivirusScanner`, `ClamAvScanner` y el health check. Tests unitarios contra un servidor TCP falso que responde `OK`, `FOUND` o se cuelga.
3. **Archivos.** Añadir el propósito `SOPORTES`. Crear `IFileStore` y `LocalEncryptedFileStore`. Crear la migración clínica con `clinico.soportes`, su RLS, el trigger y los `GRANT`. Tests de integración: ida y vuelta, el archivo en disco no contiene la cadena `%PDF-`, y una AAD con otro tenant falla.
4. **Dominio.** Crear `Domain/Incapacidades` con `Incapacidad`, sus reglas y alertas, y `TipoRegistroClinico.CertificadoIncapacidad`. Tests unitarios: uno por regla y uno por alerta, incluida la cadena con una prórroga discontinua.
5. **Persistencia.** Crear la migración `AtelIncapacidades` (tabla, RLS y `GRANT` a `halsa_app` y `halsa_ingesta`) y su mapeo en `IngestaDbContext` y `ApplicationDbContext`.
6. **Comando.** Crear `RegistrarIncapacidadCommand` y su handler en `Application/Incapacidades`, con el índice ciego de la categoría, la HC, el soporte, la idempotencia, la auditoría y el outbox. Tests de integración.
7. **Endpoint de carga.** Crear el endpoint multipart en `Endpoints/Incapacidades/`, con los límites de tamaño, la validación de la firma mágica y la traducción de errores. Tests funcionales con un `IAntivirusScanner` falso.
8. **Endpoint de consulta.** Crear `ListIncapacidadesQuery` y su endpoint. Tests funcionales.
9. **Contrato.** Actualizar `docs/api/atel-api.openapi.yaml`: `profesional` opcional para el personal, los códigos `422` nuevos, `503`, el `GET` y `CERTIFICADO_INCAPACIDAD`.

---

## Criterios de aceptación

- [ ] Con el token de `int-ips-110011234501`, la carga de una `INICIAL` de 10 días sobre un caso remitido responde `201` con `fechaFin = fechaInicio + 9`, `diasAcumulados=10` y `estado=REGISTRADA`.
- [ ] El archivo en `Archivos:Raiz` no empieza por `%PDF-` ni contiene el texto del PDF de prueba, y `clinico.soportes.sha256_claro` coincide con el SHA-256 del PDF original.
- [ ] La respuesta `201` no contiene el código del diagnóstico ni las observaciones, y `atel.incapacidades` tampoco contiene el código en claro.
- [ ] Con un principal `medico.ips` con `acr=1`, la carga responde `401`, con `WWW-Authenticate` que incluye `error="insufficient_user_authentication"` y `acr_values="2"`, y deja una fila de auditoría `DENEGADO` con `motivo='step-up'`.
- [ ] El mismo principal con `acr=2` responde `201`, y el registro de HC lleva el RETHUS de los claims.
- [ ] Manual: en el realm de desarrollo, un *authorization code flow* de `halsa-backoffice` con `acr_values=2` pide OTP a `medico.ips` y emite un token con `acr="2"`.
- [ ] Un archivo que no empieza por `%PDF-` responde `415`. Uno de 11 MB responde `413`.
- [ ] Un PDF con la firma EICAR responde `422` con `Archivos.SoporteInfectado` y no deja ni archivo ni filas (verificado contra un escáner falso, y de forma manual con ClamAV real).
- [ ] Con ClamAV detenido, la carga responde `503` con `Archivos.AntivirusNoDisponible`, y `/health` reporta `antivirus: Degraded`.
- [ ] Una incapacidad que se cruza con otra del mismo caso responde `422` con `INCAPACIDAD_SOLAPADA`.
- [ ] Una `PRORROGA` sin `prorrogaDe` responde `422` con `PRORROGA_SIN_INICIAL`.
- [ ] Una prórroga que inicia 31 días después del fin de la anterior responde `422` con `PRORROGA_DISCONTINUA`. Si inicia 30 días después, se acepta.
- [ ] Una prórroga con un diagnóstico `S62.6` sobre una inicial con `S62.0` se acepta. Con `M54.5`, responde `422` con `PRORROGA_DIAGNOSTICO_NO_RELACIONADO`.
- [ ] Una cadena que llega a 150 días devuelve la alerta `ACUMULADO_150_DIAS`, y una que llega a 180 devuelve las alertas de 150 y de 180.
- [ ] Repetir la carga con la misma `Idempotency-Key` y el mismo cuerpo responde `201` con el mismo `id`, y no crea un segundo archivo ni una segunda fila.
- [ ] El mismo `numeroCertificado` de la misma IPS con otra llave responde `409`.
- [ ] `GET /v1/casos/{id}/incapacidades` como `analista.arla` devuelve la lista sin diagnósticos ni profesional, y deja una fila `LEER / INCAPACIDAD`.
- [ ] Con un token de `ARL_B`, sobre un caso de `ARL_A`, el `GET` y el `POST` responden `404`.
- [ ] Cada carga exitosa deja un `IncapacidadRegistradaIntegrationEvent` en el outbox.

---

## Decisiones

- **Sí:** archivos en un volumen local cifrados con AES-GCM y la DEK del tenant, detrás de `IFileStore`. **No:** MinIO todavía, porque es otro servicio que operar en el VPS. **No:** `bytea`, porque con 10 MB por archivo hincha la base y sus backups.
- **Sí:** la metadata del soporte va en `clinico`, porque el PDF contiene el diagnóstico y es un documento clínico.
- **Sí:** ClamAV síncrono que falla cerrado (`503`). **No:** aceptar un archivo sin analizar.
- **Sí:** el step-up genérico nace en esta spec y la SPEC 08 lo reutiliza. **No:** intercambiar el orden de las specs.
- **Sí:** las integraciones quedan exentas del step-up, porque no tienen sesión humana. Su garantía es mTLS más `private_key_jwt` (SPEC 13).
- **Sí:** `MaxAntiguedadAuth` es nulo para las incapacidades (basta con `acr=2` en la sesión). El dictamen PCL exige 15 min (SPEC 08).
- **Sí:** se reutiliza `halsa_ingesta` en los dos canales para escribir la parte administrativa, la clínica y la idempotencia en una sola transacción. Su papel pasa a ser "escritura mixta".
- **Sí:** el archivo se escribe antes de la transacción. Un fallo deja un archivo huérfano (cifrado), que limpia la SPEC 09. **No:** escribir el archivo después del commit, porque podría quedar un registro sin soporte.
- **Sí:** la prórroga exige continuidad (≤ 30 días) y la misma categoría CIE-10, comparada con un índice ciego de categoría sin descifrar.
- **Sí:** el índice ciego de la categoría va en `atel.incapacidades`. Revela solo la igualdad de categoría dentro del tenant, y sin la DEK `INDICE` no es reversible.
- **Sí:** las alertas se calculan en la respuesta y en la consulta, y no se guardan. La alerta por tiempo (sin carga nueva) es del motor de plazos (SPEC 09).
- **Sí:** solo existe el estado `REGISTRADA`. Los demás estados pertenecen a la liquidación, fuera de ARQ 01.
- **Sí:** el emisor de idempotencia de un humano es `sub:{sub}` (se amplía el formato de la SPEC 05).

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Las firmas de ClamAV se descargan al arrancar (varios minutos) y la API responde `503` mientras tanto. | El contenedor tiene un volumen persistente para las firmas, y el health check lo refleja. Se documenta en el runbook de la SPEC 13. |
| El volumen de archivos y la KEK quedan en el mismo VPS. | Los archivos están cifrados con DEK envueltas por la KEK, que vive fuera de los backups del volumen (SPEC 03). |
| El disco del VPS se llena con PDFs. | La métrica de uso del volumen y el límite de 10 MB. La migración a almacenamiento de objetos es una spec propia. |
| Las reglas de prórroga y los umbrales no coinciden con la interpretación jurídica vigente. | Son parámetros o están aislados en el dominio, con un test por regla. Hay validación jurídica antes de producción. |
| Los OTP precargados en el realm de desarrollo se filtran a producción. | Solo existen en `docker/keycloak/*.json` de desarrollo. El realm de producción no se importa de esos archivos. |
| El parseo multipart consume memoria con archivos grandes. | El límite del request es de 12 MB, y el archivo se lee una sola vez en un buffer acotado. |

---

## Qué **no** está en esta spec

- La descarga del PDF (SPEC 08).
- El ABAC y el dictamen PCL (SPEC 08).
- El plazo `TOPE_IT_180` y la limpieza de huérfanos (SPEC 09).
- El webhook (SPEC 10).
- La liquidación y los estados de pago.
- MinIO o S3.
- La firma digital del PDF.

Cada uno, si se aborda, va en su propia spec.

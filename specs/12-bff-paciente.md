# SPEC 12 — BFF y endpoints del portal del paciente

> **Estado:** Borrador
> **Depende de:** SPEC 01, SPEC 02, SPEC 04, SPEC 05, SPEC 07, SPEC 08, SPEC 09
> **Fecha:** 2026-10-09
> **Objetivo:** Que un trabajador, autenticado en `halsa-pacientes` a través de un BFF con cookie y vinculado a su documento con un código de activación de un solo uso, consulte sus casos, su línea de tiempo y sus incapacidades, y solicite la copia de su historia clínica sin acceder nunca al detalle clínico en línea.

---

## Por qué existe esta spec

El portal del paciente es la superficie más expuesta: es masiva, móvil y anónima hasta que el usuario se registra.
ARQ §4.3 fija tres reglas:

1. **Tokens fuera del navegador** (BFF con cookie), por la exposición a XSS.
2. **Ningún endpoint de paciente recibe ids de trabajador.** Todo se resuelve desde `sub`, lo que elimina el IDOR por diseño.
3. **El paciente ve hitos, no detalle clínico.** La copia de la HC sigue el flujo formal (Res. 1995/1999, art. 14).

Falta resolver **cómo se vincula una cuenta con un trabajador** sin confiar en lo que el usuario declara al registrarse.
La respuesta es un código de activación que entrega la ARL, que ya conoce al trabajador.

---

## Alcance

**Incluido:**

- El proyecto `src/Halsa.Bff.Paciente`, con YARP, OIDC confidencial contra `halsa-pacientes`, la cookie `__Host-` con `SameSite=Strict`, la sesión en Redis, el header anti-CSRF y una allowlist de rutas.
- Códigos de activación: emisión por la ARL (backoffice o integración), canje por el paciente, límites de intentos, y la tabla `atel.vinculos_paciente` por documento.
- RLS y query filter para el actor `Paciente` (por vínculo).
- Los endpoints `/v1/paciente/*`: vínculos, mis casos, resumen, línea de tiempo, incapacidades y solicitudes de copia de HC.
- La bandeja de solicitudes de copia de HC para la ARL y el plazo legal `SOLICITUD_COPIA_HC`.
- Las columnas `validado_en` y `rechazado_en` en `CasoMedico`, para la línea de tiempo.
- La configuración del realm `halsa-pacientes`: el client `halsa-bff-paciente` y el autorregistro.

**Fuera de alcance (specs futuras):**

- La verificación de identidad por KYC externo (Registraduría o biometría) → spec propia. El código de activación la reemplaza.
- El estado de los pagos en el resumen → fuera de ARQ 01 (liquidación).
- La entrega digital de la copia de la HC por el portal → spec propia. Aquí la entrega es por el canal formal.
- La app móvil nativa → no aplica: el BFF sirve a la web, y la app móvil usaría su propio flujo en otra spec.
- Las notificaciones push o por correo al paciente → spec propia.
- Desvincular una cuenta → spec propia.
- El frontend del portal → fuera del backend.

---

## Modelo de datos

### Códigos de activación y vínculos (esquema `atel`)

| Tabla | Columnas | Notas |
| --- | --- | --- |
| `codigos_activacion` | `id` (6 caracteres base32, PK), `tenant_id`, `caso_medico_id`, `trabajador_tipo`, `trabajador_numero`, `secreto_hash bytea`, `secreto_sal bytea`, `emitido_en`, `emitido_por`, `expira_en` (+30 días), `usado_en`, `usado_por`, `intentos_fallidos`, `estado` (`ACTIVO`/`USADO`/`BLOQUEADO`/`EXPIRADO`) | Formato visible: `ABC123-K7Q2M9XD4P` (id + 10 caracteres de secreto, unos 50 bits). `secreto_hash = SHA-256(sal ‖ secreto)`. RLS por tenant, más la función de canje. |
| `vinculos_paciente` | `paciente_sub` (PK, parte 1), `trabajador_tipo`, `trabajador_numero` (PK, partes 2 y 3), `codigo_id`, `vinculado_en` | El vínculo es **por documento** y vale para los casos de cualquier ARL. RLS: `paciente_sub = app.sub` con `app.actor_tipo = 'paciente'`. |
| `intentos_canje` | `paciente_sub`, `ocurrido_en`, `exitoso` | Limita los intentos fallidos a 5 en 24 h por paciente. |

- Emitir un código nuevo para el mismo caso invalida el código `ACTIVO` anterior.
- **Canje:** la función `atel.canjear_codigo_activacion(p_id, p_secreto, p_sub)` es `SECURITY DEFINER`, con `search_path` fijo, y su dueño es `halsa_owner`. Hace lo siguiente:
  1. Valida el estado y la expiración.
  2. Compara el hash.
  3. Si falla, incrementa `intentos_fallidos` (al llegar a 5, el código pasa a `BLOQUEADO`).
  4. Si acierta, crea el vínculo.
  5. Devuelve un código de resultado.

  Es necesaria porque el paciente no tiene tenant y no puede ver los códigos por RLS. `halsa_app` solo tiene `EXECUTE` sobre ella.
- **RLS de paciente** (se añade a las políticas de lectura de la SPEC 02 y de la SPEC 07):

```sql
OR (current_setting('app.actor_tipo', true) = 'paciente' AND EXISTS (
     SELECT 1 FROM atel.vinculos_paciente v
     WHERE v.paciente_sub = current_setting('app.sub', true)
       AND v.trabajador_tipo = casos_medicos.trabajador_tipo
       AND v.trabajador_numero = casos_medicos.trabajador_numero))
```

- El query filter de `ApplicationDbContext` gana la cláusula equivalente para `ActorTipo.Paciente`.

### Solicitudes de copia de HC

Tabla `atel.solicitudes_copia_hc`:

| Columna | Notas |
| --- | --- |
| `id`, `tenant_id` (del caso), `caso_medico_id`, `paciente_sub`, `medio_entrega` (`CORREO_CERTIFICADO`/`PRESENCIAL`/`ELECTRONICO`), `direccion_o_correo` (500 caracteres), `observaciones` (1000 caracteres), `solicitada_en` | |
| `estado` (`RECIBIDA`/`EN_TRAMITE`/`ENTREGADA`/`RECHAZADA`), `atendida_en`, `atendida_por`, `nota_atencion` | |

RLS: el tenant (para la ARL) o `paciente_sub = app.sub` (para el paciente).

- La regla nueva en `sst.reglas_termino` es `SOLICITUD_COPIA_HC`: 10 días hábiles, alerta 3 hábiles antes, base Ley 1581/2012, art. 14 (**pendiente de validación jurídica**).
- La abre `SolicitudCopiaHcCreadaIntegrationEvent`, y la cumple el paso a `ENTREGADA` o `RECHAZADA`.

### Endpoints de la API

Todos los de `/v1/paciente/*` aceptan solo el esquema `Paciente` y el actor `Paciente`. Un token de personal recibe `403`.

| Método y ruta | Respuesta |
| --- | --- |
| `POST /v1/paciente/vinculos` `{ codigo }` | `201 { trabajador: { tipo, numeroEnmascarado } }`. Errores: `400` (`Paciente.CodigoInvalido`, mismo mensaje para inexistente, vencido o usado), `423` (`Paciente.CodigoBloqueado`), `429` (`Paciente.DemasiadosIntentos`) y `409` (`Paciente.VinculoDistinto`, si la cuenta ya está vinculada a otro documento). |
| `GET /v1/paciente/mis-casos` | `[{ casoId, radicado, tipoEvento, estado, fechaEvento, arl, ultimoHitoEn }]` |
| `GET /v1/paciente/resumen` | `{ casosActivos[], incapacidadesVigentes[{ casoId, fechaInicio, fechaFin, dias }], proximosHitos[{ casoId, tipo, fecha }] }`. Hitos: el fin de la incapacidad vigente y `venceControversiaEn` de un dictamen notificado. |
| `GET /v1/paciente/casos/{id}/linea-tiempo` | `[{ tipo, fecha, descripcion }]`: `RADICADO`, `VALIDADO`, `RECHAZADO`, `REMITIDO_A_IPS`, `INCAPACIDAD_REGISTRADA`, `DICTAMEN_NOTIFICADO` y `DICTAMEN_EN_FIRME`. **Sin** diagnósticos, porcentajes ni fundamentos. Un caso no vinculado responde `404`. |
| `GET /v1/paciente/incapacidades` | Todas las incapacidades de los casos vinculados, sin diagnóstico |
| `POST /v1/paciente/casos/{id}/solicitudes-copia-hc` | `201`. Si ya hay una solicitud abierta para el caso, `409`. |
| `GET /v1/paciente/solicitudes-copia-hc` | Las del paciente |

Endpoints de la ARL:

| Método y ruta | Permiso | Uso |
| --- | --- | --- |
| `POST /v1/casos/{id}/codigos-activacion` | `pacientes:activar` (`analista-arl` e `integracion-arl`) | `201 { codigo, expiraEn }`. El código completo se muestra una sola vez. Se audita `CREAR / CODIGO_ACTIVACION`. Solo para casos `Validado` o posteriores. |
| `GET /v1/solicitudes-copia-hc?estado=` | `solicitudes-hc:gestionar` (`oficial-privacidad`) | La bandeja del tenant |
| `POST /v1/solicitudes-copia-hc/{id}/tramitar` | Ídem | De `RECIBIDA` a `EN_TRAMITE` |
| `POST /v1/solicitudes-copia-hc/{id}/cerrar` `{ resultado: ENTREGADA \| RECHAZADA, nota }` | Ídem | Cierra la solicitud y cumple el plazo |

`RecursoAuditoria` gana los valores `Vinculo`, `CodigoActivacion` y `SolicitudCopiaHc`.
Se auditan: el canje (con su resultado), la línea de tiempo (`LEER / CASO`), la creación de solicitudes y la gestión de solicitudes.

### BFF (`src/Halsa.Bff.Paciente`)

| Elemento | Valor |
| --- | --- |
| Autenticación | OIDC *code* + PKCE, client confidencial `halsa-bff-paciente` del realm `halsa-pacientes`, con `scope=openid` y audiencia `halsa-api` |
| Cookie | `__Host-halsa-paciente`, `HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/`. Solo contiene la llave de la sesión. |
| Sesión | Un `ITicketStore` sobre Redis. Los tickets se protegen con Data Protection, cuyas llaves se persisten en Redis. La sesión es deslizante, con 30 min de inactividad y 12 h como máximo. El access token se refresca en el servidor cuando le quedan menos de 60 s. |
| Rutas propias | `GET /paciente/bff/login?returnUrl=` (solo URLs relativas), `POST /paciente/bff/logout` (sesión local + *end session* de Keycloak), `GET /paciente/bff/usuario` (`{ nombre, correo, vinculado }`) |
| Proxy (YARP) | `/paciente/api/{**resto}` → `{Api}/v1/paciente/{**resto}`, con `Authorization: Bearer <access token>`. **Allowlist:** solo ese prefijo. Las cookies no se reenvían a la API. |
| Anti-CSRF | Todo request a `/paciente/api/*` y el `POST` de logout exigen `X-Halsa-Csrf: 1`. Si falta, responde `403`. |
| Sin sesión | `/paciente/api/*` responde `401` (nunca redirige, porque es una API). |
| Puerto de desarrollo | `https://localhost:5610` |

Realm `halsa-pacientes`: client `halsa-bff-paciente` (confidencial, `redirect_uri https://localhost:5610/paciente/bff/signin-oidc`, mapper de audiencia `halsa-api`).
Se habilita el autorregistro, con verificación de correo (en desarrollo, la verificación está desactivada porque no hay SMTP).

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Crear la migración `PacienteVinculos` (`codigos_activacion`, `vinculos_paciente`, `intentos_canje`, la función de canje, las cláusulas de paciente en la RLS de `casos_medicos`, `remisiones`, `incapacidades` y `dictamenes_pcl`, y `validado_en` y `rechazado_en`), y la cláusula de paciente en el query filter. Fijar `validado_en` y `rechazado_en` en `Validar` y `Rechazar` (SPEC 05). Tests de integración de la RLS de paciente.
2. Crear la emisión de códigos (comando, endpoint y permiso `pacientes:activar`) y el canje (`POST /v1/paciente/vinculos`) con los límites. Tests funcionales con el principal de paciente.
3. Crear las consultas y los endpoints de mis casos, resumen, línea de tiempo e incapacidades. Tests funcionales: un paciente sin vínculo ve listas vacías, y un caso no vinculado responde `404`.
4. Crear las solicitudes de copia de HC (paciente y bandeja de la ARL), la regla `SOLICITUD_COPIA_HC`, la apertura y el cumplimiento del plazo, y el permiso `solicitudes-hc:gestionar`. Tests.
5. Crear `src/Halsa.Bff.Paciente`: OIDC, cookie, `ITicketStore` sobre Redis, rutas `bff/*` y YARP con la allowlist y el anti-CSRF. Añadirlo a la solución y crear `tests/Halsa.Bff.Paciente.Tests`. Tests: sin el header CSRF responde `403`, `/paciente/api/../casos` no sale del prefijo, y la cookie tiene los atributos correctos.
6. Configurar el realm `halsa-pacientes` (client y autorregistro) y actualizar el README con el flujo de desarrollo del portal.
7. Actualizar `docs/api/atel-api.openapi.yaml` con `/v1/paciente/*`, los códigos de activación y las solicitudes de copia.

---

## Criterios de aceptación

- [ ] `analista.arla` emite un código para un caso `Validado` y recibe `ABC123-XXXXXXXXXX`. En la base solo quedan `secreto_hash` y `secreto_sal`, no el secreto.
- [ ] Emitir un código para un caso `Radicado` responde `409`.
- [ ] `paciente.dev` canjea el código y queda vinculado al documento del trabajador. `GET /v1/paciente/mis-casos` devuelve ese caso **y** cualquier otro caso de cualquier ARL con el mismo documento.
- [ ] Un paciente sin vínculo recibe `[]` en `mis-casos` e `incapacidades`, y `404` en la línea de tiempo de cualquier caso.
- [ ] Canjear un código ya usado responde `400` con `Paciente.CodigoInvalido`, el mismo mensaje que para un código inexistente.
- [ ] 5 secretos equivocados sobre el mismo código lo dejan `BLOQUEADO`, y el canje correcto posterior responde `423`.
- [ ] 5 canjes fallidos del mismo paciente en 24 h hacen que el sexto responda `429`, aunque el código sea válido.
- [ ] Como `halsa_app`, con `app.actor_tipo='paciente'` y `app.sub` de un paciente sin vínculo, `SELECT count(*) FROM atel.casos_medicos` devuelve `0`.
- [ ] La respuesta de la línea de tiempo de un caso con dictamen no contiene `porcentajePcl`, `fundamentos` ni ningún código CIE-10.
- [ ] Un token de personal (`analista.arla`) recibe `403` en `GET /v1/paciente/mis-casos`, y un token de paciente recibe `403` en `GET /v1/casos`.
- [ ] El paciente crea una solicitud de copia de HC (`201`) y una segunda para el mismo caso responde `409`. `privacidad.arla` la ve en su bandeja y la cierra como `ENTREGADA`, y el plazo `SOLICITUD_COPIA_HC` queda `CUMPLIDO`.
- [ ] Una solicitud sin cerrar abre el plazo de 10 días hábiles y, al vencer, genera el webhook `plazo.vencido` (si hay una suscripción, SPEC 10).
- [ ] BFF: `GET /paciente/api/mis-casos` sin `X-Halsa-Csrf` responde `403`. Con el header y sin sesión, responde `401`.
- [ ] BFF: `/paciente/api/%2e%2e/casos` y cualquier ruta fuera de `/v1/paciente/` nunca se reenvían (`404`).
- [ ] BFF: la cookie de sesión se llama `__Host-halsa-paciente`, tiene `HttpOnly`, `Secure` y `SameSite=Strict`, y su valor no contiene ningún JWT (no tiene tres segmentos base64url separados por puntos).
- [ ] Manual: en desarrollo, el flujo de login del BFF con `paciente.dev` termina en `GET /paciente/bff/usuario` con `vinculado=true` después de canjear un código. Después de `POST /paciente/bff/logout`, la sesión de Keycloak también se cierra.
- [ ] Ningún token aparece en `localStorage`, `sessionStorage` ni en las respuestas del BFF al navegador (revisión manual con las DevTools).

---

## Decisiones

- **Sí:** código de activación emitido por la ARL, de un solo uso, con 30 días de vigencia, como prueba de identidad. **No:** confiar en el documento declarado al registrarse. **Diferido:** KYC externo.
- **Sí:** el vínculo es por documento y vale para todas las ARL. Los datos son del propio trabajador y la ARL que emitió el código ya verificó su identidad.
- **Sí:** el canje por una función `SECURITY DEFINER`, porque el paciente no tiene tenant y no debe ver los códigos de nadie.
- **Sí:** el mismo error para un código inexistente, vencido o usado. Así no se puede enumerar códigos.
- **Sí:** límites por código (5, y luego bloqueo) y por paciente (5 fallos en 24 h).
- **Sí:** RLS y query filter de paciente por vínculo, usando `app.sub` y `app.actor_tipo`. El paciente nunca envía ids de trabajador (ARQ §4.3).
- **Sí:** un BFF separado, con YARP y la allowlist `/v1/paciente/*`. Aunque se comprometa el BFF, no alcanza los endpoints del personal, porque además el token de paciente es de otro emisor (SPEC 01).
- **Sí:** sesión en Redis, con revocación, refresh en el servidor y logout global. **No:** tokens dentro de la cookie.
- **Sí:** `SameSite=Strict` más un header anti-CSRF obligatorio en toda la API proxeada.
- **Sí:** la línea de tiempo solo muestra hitos administrativos. El detalle clínico sigue el flujo formal de copia (ARQ §4.3).
- **Sí:** la solicitud de copia de HC tiene un plazo legal en el motor (SPEC 09) y una bandeja para la ARL.
- **No:** el estado de los pagos en el resumen (la liquidación está fuera de ARQ 01). **Cambio respecto a ARQ §4.3.**

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| La ARL entrega el código a la persona equivocada. | El código solo vincula el documento del caso, el canje queda auditado, y la ARL puede emitir uno nuevo (que invalida el anterior). La desvinculación va en su propia spec. |
| Fuerza bruta distribuida sobre los códigos con muchas cuentas. | El secreto tiene unos 50 bits, el límite por código es de 5 intentos y Nginx (SPEC 13) añade un rate limit por IP en `/paciente/`. |
| La caída de Redis tumba todas las sesiones del portal. | Redis con persistencia AOF en el VPS. Al caer, el portal pide un nuevo login, pero no pierde datos. Se documenta en el runbook de la SPEC 13. |
| Desfase entre la ruta pública del BFF y la ruta del API. | La allowlist y el reescrito del prefijo están en la configuración de YARP y los cubren tests. |
| El autorregistro abierto genera cuentas basura. | Sin vínculo, una cuenta no ve nada. Hay verificación de correo en producción y rate limit en Keycloak. |

---

## Qué **no** está en esta spec

- KYC externo.
- El estado de los pagos.
- La entrega digital de la copia de la HC.
- La app móvil nativa.
- Las notificaciones al paciente.
- La desvinculación.
- El frontend del portal.

Cada uno, si se aborda, va en su propia spec.

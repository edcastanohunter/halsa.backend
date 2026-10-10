# SPEC 10 — Webhooks salientes firmados

> **Estado:** Borrador
> **Depende de:** SPEC 01, SPEC 02, SPEC 03, SPEC 05, SPEC 06, SPEC 07, SPEC 08, SPEC 09
> **Fecha:** 2026-10-09
> **Objetivo:** Entregar a las ARL y a las IPS suscritas los eventos del sistema (plazos, validación de casos, remisiones, cargas y accesos de emergencia) como webhooks firmados con HMAC, con reintentos durante 24 h, dead letter, reenvío y protección contra SSRF.

---

## Por qué existe esta spec

El core de la ARL no consulta a Halsa: espera que Halsa le avise (ARQ §3.4 y §4.2).
Varias specs anteriores dejaron eventos en el outbox sin entregar.
Esta spec los publica hacia afuera **sin datos clínicos**.

El Worker es el único componente que llama hacia afuera.
Por eso es donde se concentra la defensa contra SSRF.

---

## Alcance

**Incluido:**

- Las tablas `integracion.suscripciones_webhook` y `integracion.entregas_webhook`.
- La API de suscripciones para las service accounts con `webhooks:manage` (ARL e IPS): alta, consulta, cambio, baja, rotación de secreto, entregas y reintento.
- `WebhookFanOutHandler`: del outbox a una entrega por suscripción.
- `WebhookDispatcherJob`: envío con firma, reintentos con backoff y jitter, dead letter y suspensión automática.
- La firma `Halsa-Signature`, con doble firma durante la rotación.
- El cliente HTTP con protección SSRF en cada conexión.
- Los 10 tipos de evento públicos (ver la tabla) y los eventos de integración que faltaban en las SPEC 02, 06 y 08.
- La actualización de la sección `webhooks` de `docs/api/atel-api.openapi.yaml`.

**Fuera de alcance (specs futuras):**

- Notificaciones por correo o SMS → spec propia (requiere SMTP en el VPS).
- Un panel de administración de suscripciones para el personal de Halsa → spec propia.
- Webhooks hacia el empleador o el paciente → fuera de ARQ 01.
- Un broker de mensajería externo → no aplica en el VPS.
- mTLS saliente hacia los cores de las ARL → spec propia, si una ARL lo exige.

---

## Modelo de datos

### Catálogo de eventos públicos

| Tipo | Destinatario | Origen (evento de integración) | Datos (además de `eventoId`, `tipo` y `ocurridoEn`) |
| --- | --- | --- | --- |
| `plazo.proximo-a-vencer` | ARL | `PlazoProximoAVencer` (SPEC 09) | `casoId`, `radicado`, `plazo` (`PlazoResumen`) |
| `plazo.vencido` | ARL | `PlazoVencido` (SPEC 09) | Ídem |
| `caso.validado` | ARL | `CasoValidado` (SPEC 05) | `casoId`, `radicado`, `numeroReporteOrigen` |
| `caso.rechazado` | ARL | `CasoRechazado` (SPEC 05) | Ídem, más `motivo` y `duplicadoDe` |
| `incapacidad.registrada` | ARL | `IncapacidadRegistrada` (SPEC 07) | `casoId`, `radicado`, `incapacidadId`, `tipo`, `fechaInicio`, `fechaFin`, `diasAcumulados`, `alertas[]` |
| `concepto.registrado` | ARL | `RegistroClinicoCreado` (**nuevo**, SPEC 06) | `casoId`, `radicado`, `registroId`, `tipo`, `ipsEmisora` (si aplica) |
| `dictamen.notificado` | ARL | `DictamenPclNotificado` (SPEC 08) | `casoId`, `radicado`, `dictamenId`, `porcentajePcl`, `origen`, `notificadoEn` |
| `auditoria.acceso-emergencia` | ARL | `AccesoEmergenciaRegistrado` (**nuevo**, SPEC 08) | `casoId`, `radicado`, `revisionId`, `actorId`, `motivo`, `venceRevisionEn` |
| `caso.remitido` | IPS | `CasoRemitido` (**nuevo**, SPEC 02) | `casoId`, `radicado`, `remisionId`, `tipoEvento`, `fechaEvento`, `trabajador` (`Identificacion`) |
| `remision.revocada` | IPS | `RemisionRevocada` (**nuevo**, SPEC 02) | `casoId`, `radicado`, `remisionId` |

- **Ningún evento lleva diagnósticos, contenido clínico ni fundamentos.**
- `eventoId` es el id del mensaje del outbox, estable entre reintentos. El receptor deduplica por él.
- Una ARL solo puede suscribirse a eventos con destinatario ARL, y una IPS solo a los de destinatario IPS.

Esta spec añade los cuatro eventos de integración marcados como **nuevo** en los handlers que corresponden de las SPEC 02, 06 y 08.

### Tablas (esquema `integracion`)

| Tabla | Columnas | Notas |
| --- | --- | --- |
| `suscripciones_webhook` | `id`, `emisor` (`tenant:{ARL}` / `ips:{REPS}`), `tenant_id` (null), `ips_id` (null), `url`, `eventos text[]`, `estado` (`ACTIVA`/`PAUSADA`/`SUSPENDIDA`/`ELIMINADA`), `secreto_ct`, `secreto_key_id`, `secreto_anterior_ct` (null), `secreto_anterior_expira_en` (null), `fallos_consecutivos`, `creada_en`, `creada_por` | RLS por `emisor` (igual que la idempotencia). Política `TO halsa_worker USING (true)`. |
| `entregas_webhook` | `id`, `suscripcion_id`, `evento_id`, `tipo`, `payload jsonb`, `estado` (`PENDIENTE`/`ENTREGADA`/`DEAD_LETTER`/`DESCARTADA`), `intentos`, `primer_intento_en`, `proximo_intento_en`, `ultimo_status`, `ultimo_error` (500 caracteres), `entregada_en` | Única `(suscripcion_id, evento_id)`. Índice parcial `(proximo_intento_en) WHERE estado='PENDIENTE'`. RLS a través de la suscripción, más `TO halsa_worker`. |

- El secreto son 32 bytes aleatorios, que se devuelven en base64url **una sola vez**.
- Se guarda envuelto con `IKeyEncryptionProvider` (la KEK de la SPEC 03), con la AAD `"webhook|{suscripcionId}|{keyId}"`. No depende de un tenant porque las IPS no tienen tenant.

### Fan-out y envío

1. `WebhookFanOutHandler` (un `IIntegrationEventHandler` por cada tipo de origen, en el Worker) construye el payload público y, con `halsa_worker`, inserta una `entregas_webhook` por cada suscripción `ACTIVA` del destinatario (`tenant_id` o `ips_id` del evento) que incluya ese tipo. Es idempotente por `(suscripcion_id, evento_id)`.
2. `WebhookDispatcherJob` es un hosted service del Worker que da una vuelta cada 5 s y toma lotes de 50 con `FOR UPDATE SKIP LOCKED`. Para cada entrega:
   - `POST url` con `Content-Type: application/json`, el cuerpo exacto de `payload`, timeout de 10 s y sin seguir redirecciones;
   - los headers `Halsa-Signature`, `Halsa-Event-Id`, `Halsa-Event-Type` y `Halsa-Delivery-Attempt`.
3. Según la respuesta:
   - `2xx` → `ENTREGADA`, y `fallos_consecutivos = 0`.
   - `410 Gone` → `DESCARTADA` y la suscripción pasa a `SUSPENDIDA`.
   - Otro código, timeout o error de red → `intentos++`, y `proximo_intento_en = now + min(2^intentos × 30 s, 4 h) ± 20 %`. Si `now − primer_intento_en > 24 h`, pasa a `DEAD_LETTER`.
   - En cualquier fallo, `fallos_consecutivos++`. Al llegar a 50, la suscripción pasa a `SUSPENDIDA`.

### Firma

```text
Halsa-Signature: t=<unix>,v1=<hex(HMAC-SHA256(secreto, t + "." + cuerpo))>[,v1=<hex con el secreto anterior>]
```

- Durante las 24 h siguientes a una rotación se envían dos valores `v1`: primero el del secreto nuevo y después el del anterior.
- El receptor debe rechazar un `t` con más de 5 minutos de antigüedad (contrato).

### Protección SSRF

- La URL debe ser `https://`, sin credenciales, con 2048 caracteres como máximo. Se valida al registrar.
- En **cada conexión**, el `ConnectCallback` de `SocketsHttpHandler` resuelve el DNS y rechaza las direcciones de estos rangos: `0.0.0.0/8`, `10.0.0.0/8`, `100.64.0.0/10`, `127.0.0.0/8`, `169.254.0.0/16`, `172.16.0.0/12`, `192.168.0.0/16`, `224.0.0.0/4`, `::1`, `fc00::/7`, `fe80::/10` y las IPv4 mapeadas. Así se evita el *DNS rebinding*.
- `Webhooks:PermitirDestinosLocales=true` (que acepta `http://` y la loopback) solo se admite en `Development` y `Testing`. En `Production`, el arranque falla si está activado.

### API de suscripciones

Todas exigen `webhooks:manage` y un actor `Integracion` (de ARL o de IPS). Cada emisor solo ve sus propias suscripciones.

| Método y ruta | Comportamiento |
| --- | --- |
| `POST /v1/integraciones/suscripciones-webhook` `{ url, eventos[] }` | `201`, con `secreto` en la respuesta (única vez) |
| `GET /v1/integraciones/suscripciones-webhook` | La lista, sin secretos |
| `GET …/{id}` | El detalle, sin secreto |
| `PATCH …/{id}` `{ url?, eventos?, estado? (ACTIVA \| PAUSADA) }` | `200`. Reactivar una `SUSPENDIDA` pone `fallos_consecutivos = 0`. |
| `DELETE …/{id}` | `204`. Pasa a `ELIMINADA` y sus entregas pendientes a `DESCARTADA`. |
| `POST …/{id}/rotar-secreto` | `200`, con el `secreto` nuevo. El anterior vale 24 h más. |
| `GET …/{id}/entregas?estado=&page=` | El historial, sin `payload` |
| `POST …/{id}/entregas/{entregaId}/reintentar` | Solo desde `DEAD_LETTER` o `DESCARTADA`: vuelve a `PENDIENTE`, con `intentos = 0` y `primer_intento_en = now`. `202`. |

El permiso `webhooks:manage` se añade al compuesto `integracion-ips` (se ajusta la SPEC 01).

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Crear la migración `WebhooksSalientes` (tablas, RLS y `GRANT` a `halsa_app` y `halsa_worker`) y los tipos de dominio de la suscripción y la entrega.
2. Crear `WebhookSecretProtector` sobre `IKeyEncryptionProvider` y `WebhookSigner`. Tests unitarios: un vector de firma fijo y la doble firma en la rotación.
3. Crear `SafeWebhookHttpClient` con el `ConnectCallback` y la validación de URL. Tests unitarios: cada rango bloqueado, un nombre que resuelve a `127.0.0.1` y una redirección no seguida.
4. Crear los endpoints de suscripción, la rotación y la consulta de entregas en `Endpoints/Integraciones/Webhooks/`. Tests funcionales.
5. Crear los eventos de integración nuevos (`CasoRemitido`, `RemisionRevocada`, `RegistroClinicoCreado` y `AccesoEmergenciaRegistrado`) en los handlers de las SPEC 02, 06 y 08.
6. Crear `WebhookFanOutHandler` y la conversión de cada evento a su payload público. Tests de integración: un evento con dos suscripciones produce dos entregas, y un evento repetido no las duplica.
7. Crear `WebhookDispatcherJob`, con el backoff, el dead letter, la suspensión y el `410`, y el job de Quartz `PurgaEntregasWebhookJob` (diario: borra las `ENTREGADA` con más de 90 días). Tests de integración contra un receptor HTTP de prueba (Kestrel en el test) con `PermitirDestinosLocales`.
8. Crear el endpoint de reintento y actualizar `docs/api/atel-api.openapi.yaml` con los 10 webhooks y sus schemas.

---

## Criterios de aceptación

- [ ] `POST` de una suscripción con `url=https://10.0.0.5/hook` responde `400`. Con `http://ejemplo.com` (en `Production`) responde `400`.
- [ ] El `secreto` aparece en la respuesta del `POST` y en ninguna otra respuesta. `secreto_ct` no contiene el secreto en claro.
- [ ] Una IPS que intenta suscribirse a `plazo.vencido` recibe `400`. Una ARL que intenta suscribirse a `caso.remitido` también.
- [ ] Al vencer un plazo, el receptor de prueba suscrito por `ARL_A` recibe un POST, y su `Halsa-Signature` se verifica con el secreto compartido.
- [ ] El receptor de `ARL_B` no recibe los eventos de `ARL_A`.
- [ ] Al remitir un caso a `110011234501`, la suscripción de esa IPS recibe `caso.remitido` con la identificación del trabajador y sin datos clínicos.
- [ ] Ningún payload de ningún tipo contiene las claves `diagnosticos`, `fundamentos`, `contenido` ni `descripcion` (test sobre los 10 tipos).
- [ ] Si el receptor responde `500` tres veces y luego `200`, la entrega queda `ENTREGADA` con `intentos=4`, y el `Halsa-Event-Id` es el mismo en los cuatro envíos.
- [ ] Con el reloj 25 h después del primer intento fallido, la entrega pasa a `DEAD_LETTER`.
- [ ] `POST …/entregas/{id}/reintentar` sobre un `DEAD_LETTER` la entrega en el siguiente ciclo.
- [ ] 50 fallos seguidos suspenden la suscripción, y `PATCH estado=ACTIVA` la reactiva con `fallos_consecutivos=0`.
- [ ] Un `410` del receptor deja la suscripción `SUSPENDIDA`.
- [ ] Un nombre DNS que resuelve a `127.0.0.1` se bloquea en la conexión, aunque la URL haya pasado la validación al registrarse (sin `PermitirDestinosLocales`).
- [ ] Una respuesta `302` no se sigue y cuenta como fallo.
- [ ] Después de `rotar-secreto`, los envíos de las 24 h siguientes llevan dos `v1`, y los posteriores llevan uno solo.
- [ ] Con dos Workers en paralelo, cada entrega se envía una sola vez por intento.
- [ ] Al arrancar en `Production` con `Webhooks:PermitirDestinosLocales=true`, el proceso termina con un error de validación.

---

## Decisiones

- **Sí:** se entregan los 10 eventos, incluidos los operativos, la remisión a la IPS y el acceso de emergencia. Así se cumple la notificación en tiempo real que prometió la SPEC 08.
- **Sí:** los payloads son mínimos y sin datos clínicos. **No:** incluir el diagnóstico aunque la ARL lo pida, porque iría por un canal sin la auditoría de lectura clínica.
- **Sí:** `caso.remitido` incluye la identificación del trabajador. Es un dato personal, no clínico, y la IPS lo necesita para atender.
- **Sí:** la API de suscripciones es autoservicio para ARL e IPS, y el secreto se muestra una sola vez.
- **Sí:** el secreto se envuelve con la KEK directamente, no con una DEK de tenant, porque las IPS no tienen tenant y el volumen es mínimo.
- **Sí:** durante la rotación se envían dos firmas por 24 h. El receptor no pierde eventos mientras rota.
- **Sí:** la protección SSRF valida la IP en cada conexión, no solo al registrar. **No:** una allowlist de dominios, que exigiría operación manual por cliente.
- **Sí:** reintentos con backoff exponencial y jitter durante 24 h, luego dead letter y reenvío por API (ARQ §3.4).
- **Sí:** suspensión automática tras 50 fallos seguidos o un `410`. Un receptor caído no acumula trabajo infinito.
- **Sí:** el fan-out crea una fila por suscripción en la base. **No:** enviar directamente desde el handler del outbox, porque un receptor lento bloquearía el drenado del outbox.
- **Sí:** entrega *at-least-once*, deduplicada por `eventoId` en el receptor (contrato).

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Un receptor lento agota los hilos del dispatcher. | El timeout es de 10 s, los lotes son de 50 y la concurrencia por ciclo está acotada (`Webhooks:MaxParalelo`, 8 por defecto). |
| Una ARL registra una URL de un tercero y Halsa le envía datos. | Los datos solo van a la suscripción del propio emisor, y son mínimos. El registro de la URL queda en `creada_por`. |
| Se filtra el secreto de una suscripción. | Rotación por API con 24 h de transición. El secreto está envuelto con la KEK en reposo. |
| `auditoria.acceso-emergencia` llega a un sistema de la ARL que no lo trata como sensible. | Lleva solo los ids y el motivo, sin datos clínicos. Su manejo se documenta en el acuerdo de integración. |
| Crece la tabla de entregas. | `PurgaEntregasWebhookJob` borra a diario las `ENTREGADA` con más de 90 días (paso 7). |

---

## Qué **no** está en esta spec

- Correo y SMS.
- El panel interno de administración de suscripciones.
- Webhooks hacia el empleador o el paciente.
- Un broker externo.
- mTLS saliente.

Cada uno, si se aborda, va en su propia spec.

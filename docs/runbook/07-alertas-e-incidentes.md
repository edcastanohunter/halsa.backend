# Runbook 07 — Alertas e incidentes

> SPEC 13. Tres capas, porque el VPS no puede avisar de su propia caída:
>
> 1. **Seq** (dentro del VPS) convierte eventos del log en correos.
> 2. **Heartbeat** (Worker → servicio externo): si deja de llegar, alerta el servicio externo.
> 3. **Monitor de disponibilidad externo**: `/health/vivo` de `api`, `auth` y `pacientes`, cada minuto.

## 1. Seq

### Configuración inicial (por túnel, runbook 03)

1. Entrar con el admin del primer arranque (`HALSA_SEQ_ADMIN_HASH`) y cambiar la contraseña.
2. **Settings → SMTP**: el servidor de correo de alertas (el mismo proveedor que usa Keycloak), con el remitente
   `alertas@edcastdev.com` (o el que corresponda).
3. **Settings → API Keys**: una para la ingesta de `api`, `worker` y `bff` (`HALSA_SEQ_API_KEY`). Opcionalmente,
   exigir API key en la ingesta.
4. **Retención**: 30 días para `Information`, 180 para `Warning` o más.
5. Crear las señales y las alertas de la tabla de abajo. Destinatario: el correo de operaciones. Supresión: 1 h, salvo que la
   tabla diga otra cosa.

### Señales y alertas

Todos los eventos de alerta llevan la propiedad **`Alerta`** con un nombre estable
(`Halsa.Backend.Infrastructure.Observabilidad.Alertas`). Las señales filtran por ella, no por el texto del mensaje.

| Señal | Filtro de Seq | Quién lo emite | Alerta |
| --- | --- | --- | --- |
| Health degradado | `Alerta = 'HealthCheck'` | `HealthReportLogPublisher` (api y worker), cada minuto mientras no esté `Healthy` | `count(*) >= 5` en 5 min, por `Application`: **más de 5 min** en `Degraded`/`Unhealthy` |
| Outbox agotado | `Alerta = 'OutboxAgotado'` | `SenalesOperativasJob` (worker, cada 5 min) | `count(*) > 0` en 10 min |
| Webhook en dead letter | `Alerta = 'WebhookDeadLetter'` | `SenalesOperativasJob` | `count(*) > 0` en 10 min |
| Suscripción suspendida | `Alerta = 'WebhookSuspendida'` | `SenalesOperativasJob` | `count(*) > 0` en 10 min (supresión 24 h: avisa a diario mientras siga suspendida) |
| CRL por vencer | `Alerta = 'CrlPorVencer'` | `VigenciaCertificadosJob` (worker, cada hora) | `count(*) > 0` en 2 h, **< 48 h** para vencer, o ilegible |
| TLS por vencer | `Alerta = 'TlsPorVencer'` | `VigenciaCertificadosJob` | `count(*) > 0` en 2 h, **< 14 días**, o ilegible |
| Sello sin publicar | `Alerta = 'SelloSinPublicar'` | **pendiente (SPEC 11)** | crearla igual: `count(*) > 0` en 1 h. Hoy nada la emite |
| Exceso de 429 | `@RequestPath like '/v1/integraciones/%' and StatusCode = 429` | **pendiente**: el access log de NPM no llega a Seq | `> 100` en 5 min por cliente. Requiere enviar el log de Nginx a Seq (p. ej. `seq-input-gelf`) |
| Arranque rechazado | `@Message like 'ProductionGuard%'` | stderr del contenedor, no Seq | se ve en `desplegar.sh` (exit `1`) |

Para la verificación *staging*, detener ClamAV (`podman compose stop antivirus`). En menos de 10 minutos debe llegar el
correo "Health degradado" (`antivirus=Degraded`). Después, `podman compose start antivirus`.

## 2. Heartbeat del Worker (*dead man's switch*)

- Servicio: cualquier *dead man's switch* HTTP (Healthchecks.io, Better Stack Heartbeats, Cronitor).
- Check: período de **5 min** y gracia de **5 min**. Alerta si no llega un ping en 10 min, por correo y SMS al
  responsable de turno.
- La URL del ping va en `HALSA_HEARTBEAT_URL` (`.env`). `HeartbeatJob` hace el `GET` cada 5 minutos desde el
  Worker, que es el único con egreso.
- Si el ping deja de llegar, la causa puede ser el Worker, Quartz, el VPS o la red: empezar por `podman compose ps`.

Verificación *staging*: `podman compose stop worker`. La alerta debe llegar en menos de 15 minutos. Luego, `start worker`.

## 3. Monitor de disponibilidad externo

En un servicio externo (UptimeRobot, Better Stack Uptime o similar), tres monitores HTTP(S):

| Monitor | URL | Esperado | Intervalo |
| --- | --- | --- | --- |
| API | `https://halsa-api.edcastdev.com/health/vivo` | `200` | 1 min |
| Portal | `https://halsa-pacientes.edcastdev.com/health/vivo` | `200` | 1 min |
| Keycloak (realm de Halsa) | `https://auth.edcastdev.com/realms/halsa/.well-known/openid-configuration` | `200` | 1 min |

- Alertar tras **2 fallos seguidos**, por correo y SMS.
- Activar el aviso de vencimiento del certificado TLS (otra capa además de `TlsPorVencer`).
- `/health/vivo` es público y no da detalle: `200` o `503`. El `/health` detallado no se publica (NPM responde `404` con la configuración de Halsa).

## Incidentes

### Primeros 5 minutos

1. ¿Qué alerta? Heartbeat o uptime: el VPS o el borde. Seq: un componente.
2. `ssh vps` y `cd /opt/halsa/deploy && podman compose -f docker-compose.prod.yml ps`.
3. Logs: `podman compose logs --since 30m <servicio>`, y Seq por túnel.
4. Si hay datos de salud expuestos o un acceso indebido, es un **incidente de seguridad**: no tocar evidencia, avisar
   al oficial de privacidad y seguir el procedimiento de incidentes de la organización.

### Casos frecuentes

| Síntoma | Causa probable | Acción |
| --- | --- | --- |
| Todas las integraciones con `400` de NPM | CRL vencida | runbook 06: `generar-crl.sh` y `sincronizar-npm.sh` |
| `halsa-*` con `502` y los otros sitios de NPM bien | NPM se recreó y perdió `halsa_borde`, o un contenedor de Halsa está caído | `sincronizar-npm.sh` (runbook 03); si sigue, `podman compose ps` |
| Las integraciones no obtienen token: `Client Certification missing for MTLS HoK Token Binding` | el Keycloak compartido perdió las opciones `--spi-x509cert-lookup--*` | pedir al dueño que las restaure (README, "Keycloak") |
| Una integración con `401 Auth.CertificadoNoVinculado` | pide el token con un certificado y llama con otro, o lo llama por `api.` | revisar su configuración (runbook 06, "Lo que se le entrega") |
| `api` con `Degraded` | ClamAV actualizando firmas o caído; Redis caído | `podman compose restart antivirus` / `redis` |
| `worker` sin heartbeat | Quartz detenido, o sin conexión `halsa_worker` | logs del worker; `podman compose restart worker` |
| Navegador con error TLS | certificado vencido | runbook 05, "TLS: renovación manual" |
| Disco lleno | WAL sin archivar (bucket caído), logs | `pgbackrest check`; `podman system df` |

### Comunicación

- Integraciones (ARL/IPS): avisar si la caída de `integraciones.` dura más de 15 minutos. Los cores reintentan (las
  `Idempotency-Key` hacen seguro el reintento).
- Registro posterior: línea de tiempo, causa raíz, acciones. Se guarda junto al registro de ensayos.

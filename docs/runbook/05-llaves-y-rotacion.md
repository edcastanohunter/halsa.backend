# Runbook 05 — Llaves y rotación

> SPEC 13. Cuándo y cómo se rota cada llave, contraseña y certificado. Todo cambio de un secreto se registra
> (fecha, responsable, motivo).

## Calendario

| Qué | Cada cuánto | Además, si... |
| --- | --- | --- |
| Certificados TLS (Let's Encrypt) | automático (NPM) | alerta `TlsPorVencer` (< 14 días) |
| CRL de la intermedia | automático (cron, 7 días; vale 10) | alerta `CrlPorVencer` (< 48 h) |
| CRL de la raíz | anual (ceremonia) | se revoca la intermedia |
| Certificados cliente `int-*` | 1 año | compromiso → revocar ya (runbook 06) |
| Intermedia | 3 años (reemitir 3 meses antes) | compromiso (runbook 06) |
| Raíz | 10 años (nueva CA 1 año antes) | compromiso: nueva CA y reemisión de todo |
| Contraseñas de PostgreSQL, Redis, `kc_bff_secret` | anual | salida de alguien con acceso al VPS |
| KEK | según la política de la SPEC 03 | sospecha de exposición |
| Llave de sello | SPEC 11 (pendiente) | |

## TLS: renovación manual

Los certificados de `halsa-api`, `halsa-integraciones` y `halsa-pacientes` los emite y renueva **NPM**. Si no renueva
(alerta `TlsPorVencer`, o DNS o puerto 80 bloqueados):

1. En NPM → *SSL Certificates*, el certificado del host → *Renew Now*. El error (DNS, puerto 80, rate limit de Let's
   Encrypt) aparece ahí y en `podman logs <npm>`.
2. `curl -I http://halsa-api.edcastdev.com/.well-known/acme-challenge/prueba` debe llegar a NPM (un `404` de NPM, no
   un *timeout*).
3. Si el puerto 80 está bloqueado por un tercero, usar el *DNS challenge* de NPM con el proveedor de DNS.

Renovar no requiere `sincronizar-npm.sh`: el certificado es de NPM y la configuración de Halsa no cambia.

## Contraseñas de PostgreSQL

Sin cortar el servicio, un rol a la vez, como `edhunter`:

```bash
cd ~/.config/halsa/secretos && umask 077
openssl rand -base64 32 | tr -d '\n/+=' > pg_app_password.nuevo
podman exec -u postgres halsa-postgres psql -c "ALTER ROLE halsa_app PASSWORD '$(cat pg_app_password.nuevo)';"
mv pg_app_password.nuevo pg_app_password
systemctl --user restart halsa-api halsa-worker     # releen el secreto al arrancar
```

`mv` conserva el archivo nuevo (0600, de `edhunter`), y las unidades montan la ruta: el reinicio ve el valor nuevo.

Para `pg_owner_password` no hace falta recrear nada: el Migrador lo lee en cada despliegue.

## Redis

Cambiar `redis_password` y reiniciar todo junto: `systemctl --user restart halsa-redis halsa-api halsa-worker halsa-bff`.
Las sesiones del portal se mantienen, porque están en el AOF.

## `kc_bff_secret`

1. En la consola del Keycloak compartido: realm `halsa-pacientes` → client `halsa-bff-paciente` → Credentials →
   *Regenerate*.
2. Copiar el valor a `~/.config/halsa/secretos/kc_bff_secret` (`printf '%s' '<valor>' > …`, 0600).
3. `systemctl --user restart halsa-bff`.

## KEK (SPEC 03)

La KEK envuelve las DEK por tenant. Rotar = nueva KEK con un `KekId` nuevo y volver a envolver las DEK. Las DEK y los
datos no cambian.

1. Generar la nueva en el equipo sin red: `openssl rand -base64 32 | tr -d '\n' > halsa_kek.v2`. Guardarla en **las dos**
   custodias antes de usarla (runbook 02).
2. Con la KEK anterior todavía disponible, re-envolver las DEK (procedimiento de la SPEC 03).
3. Reemplazar `~/.config/halsa/secretos/halsa_kek` (0600), poner `HALSA_KEK_ID=local:v2` en `~/halsa/deploy/.env` y
   desplegar el mismo tag (`~/halsa/deploy/scripts/desplegar.sh <tag actual>`).
4. Conservar la KEK anterior en custodia hasta verificar que ninguna DEK la referencia.

## Llave de sello (SPEC 11)

Pendiente: la SPEC 11 define la rotación (`Auditoria:LlavesPublicasAnteriores` acepta las públicas retiradas). Hasta
entonces, `halsa_sello` se monta pero nada la lee.

# Runbook 03 — Despliegue

> SPEC 13. Las imágenes se construyen en el CI (`release.yml`, al empujar un tag `v*`) y se publican en GHCR. El VPS
> solo hace *pull*. El CI no tiene SSH al VPS: despliega una persona.

## Flujo normal

1. Mergear a `main` con el CI en verde (`ci.yml`: build, tests y `nginx -t` sobre `deploy/npm`).
2. Crear el tag: `git tag v1.4.0 && git push origin v1.4.0`. `release.yml` publica `halsa-backend`,
   `halsa-bff-paciente` y `halsa-postgres` con el tag y su digest (resumen del workflow).
3. En el VPS:
   ```bash
   /opt/halsa/deploy/scripts/desplegar.sh v1.4.0
   ```
   El script:
   1. `docker compose pull`;
   2. `docker compose run --rm migrador`, que sale con `0` o `1`. Si falla, no se toca nada más;
   3. `up -d api worker bff`;
   4. espera hasta 120 s a que `/health` de `api` y `worker` esté en `Healthy`;
   5. sincroniza la configuración de borde con NPM (`sincronizar-npm.sh`, que valida con `nginx -t` antes de recargar);
   6. si no llega a `Healthy`, **vuelve al tag anterior**. Termina con `1` en cualquier caso de fallo.
4. Revisar Seq: ningún `Error` nuevo desde el despliegue.

## Migraciones: solo hacia adelante

El rollback vuelve a la **imagen** anterior, no al **esquema** anterior. Por eso toda migración debe ser compatible
con la versión que corre (patrón *expand/contract*):

- **expand** (versión N): agregar columnas como *nullable* o con valor por defecto, tablas nuevas y vistas nuevas;
- **migrate**: la versión N escribe en los dos lados;
- **contract** (versión N+1 o posterior): borrar lo viejo, cuando ninguna versión desplegable lo usa.

El PR que trae una migración dice en su descripción en qué fase está. El revisor lo verifica. Si una migración rompió la
versión anterior y el rollback no levanta, el último recurso es el **restore PITR** a un instante previo al
despliegue (runbook 04), con la pérdida de los datos posteriores que eso implica.

## Rollback fallido

1. `docker compose -f docker-compose.prod.yml logs --since 15m api worker`.
2. Si es configuración (`ProductionGuard: ...` en el log y exit `1`), corregir `.env` o los secretos y repetir.
3. Si es el esquema: detener `api` y `worker` (NPM responde 502), avisar (runbook 07, "Comunicación") y decidir
   entre corregir hacia adelante (un `vX.Y.Z+1` con el arreglo) o el PITR del runbook 04.

## `ProductionGuard`

En `Production`, la API y el Worker **no arrancan** (exit `1`) si encuentran un atajo de desarrollo. El log nombra
la condición:

| Condición | Corrección |
| --- | --- |
| `Keycloak:RequireHttpsMetadata=false` | quitar el override; el compose lo fija en `true` |
| un emisor no es `https` | `HALSA_DOMINIO` mal escrito o vacío |
| `Halsa:Rol` vacío o `Ambos` | cada servicio tiene su rol en el compose |
| `Integraciones:ExigirCertificado=false` | quitar el override |
| `Webhooks:PermitirDestinosLocales=true` | quitar el override |
| `Clinico:KekBase64` tiene valor | la KEK viene solo del secreto `halsa_kek` |
| el stub de afiliación está registrado | build de desarrollo desplegado por error |
| `ConnectionStrings:Database` usa `halsa_owner` o `postgres` | la API usa `halsa_app` (`arrancar-dotnet.sh`) |

## NPM se recreó (actualización, reinicio) y Halsa responde 502

`docker network connect` no sobrevive a la recreación del contenedor de NPM: NPM pierde la red `halsa_borde`. El
monitor externo alerta en 2 minutos. Para recuperarlo:

```bash
/opt/halsa/deploy/scripts/sincronizar-npm.sh     # vuelve a unirlo con la IP 172.30.0.10; idempotente
```

Si después de una **actualización** de NPM los proxy hosts de Halsa quedan *Offline* con `duplicate location "/"`,
cambió cómo NPM detecta el `location /` de Advanced: runbook 01, "Actualizar NPM".

## Keycloak compartido

La consola y la operación de `auth.edcastdev.com` son de su dueño. Halsa solo:

- crea sus realms con `deploy/keycloak/importar-realms.sh` (runbook 01, paso 5);
- da de alta los clients `int-*` (runbook 06);
- rota `kc_bff_secret` (runbook 05).

Si el dueño actualiza o recrea Keycloak, hay que verificar que conserva las opciones `--spi-x509cert-lookup--*`.
Sin ellas, las integraciones reciben `invalid_request` ("Client Certification missing for MTLS HoK Token Binding")
al pedir el token. Ver README, "Keycloak → Shared instance".

## Interfaz de Seq (solo por túnel)

Seq no se publica. Para su interfaz, un proxy efímero en el loopback del VPS y un túnel SSH:

```bash
# En el VPS:
docker run --rm -d --name seq-tunel -p 127.0.0.1:8082:80 alpine/socat tcp-listen:80,fork,reuseaddr tcp-connect:seq:80
docker network connect halsa_app seq-tunel
# En tu equipo:
ssh -N -L 8082:127.0.0.1:8082 <usuario>@<vps>      # → http://localhost:8082
# Al terminar, en el VPS:
docker rm -f seq-tunel
```

> `halsa_app` es interna y Docker no publica puertos desde una red interna: el proxy efímero arranca en la red por
> defecto (que publica solo en el loopback del VPS) y luego se une a `halsa_app`. Existe solo mientras dura la sesión.

# Runbook 04 — Backups y restauración

> SPEC 13. pgBackRest, con un repositorio en un bucket S3 externo **distinto** del WORM de los sellos, cifrado con
> AES-256-CBC. Hay WAL continuo, así que se puede hacer PITR a cualquier instante retenido. La KEK, la llave de sello y
> la raíz de la CA **no** están en estos backups (runbook 02).

## Qué se respalda y qué no

| | Dónde | Recuperación |
| --- | --- | --- |
| PostgreSQL (`halsa`) | pgBackRest → bucket de backups | este runbook |
| Realms de Halsa (`halsa`, `halsa-pacientes`) | base del Keycloak compartido (respaldo de su dueño) | `importar-realms.sh` + runbook 06 |
| Sellos de auditoría | bucket WORM (SPEC 11) | no se borran (Object Lock) |
| KEK, llave de sello, raíz de la CA | custodia fuera de línea | runbook 02 |
| Soportes cifrados (`archivos`, SPEC 07) | volumen `archivos` del VPS | **sin backup externo todavía** (ver "Pendientes") |
| Firmas de ClamAV, datos de Seq, Redis | volúmenes del VPS | se regeneran solos (Redis y Seq pierden lo efímero) |

## Bucket

- Uno exclusivo para backups (`HALSA_BACKUPS_BUCKET`), sin acceso público, con versionado. Una credencial de mínimo
  privilegio: `ListBucket`, `GetObject`, `PutObject` y `DeleteObject` solo sobre ese bucket. pgBackRest borra lo que
  queda fuera de la retención.
- **No** es el bucket WORM: un compromiso de la credencial de backups no alcanza los sellos, y al revés.

## Programación

En el **crontab de `edhunter`** (`crontab -e` como `edhunter`, sin columna de usuario; hora de Colombia). Un cron
en `/etc/cron.d` como root no ve los contenedores rootless y los backups fallarían en silencio. `XDG_RUNTIME_DIR` es
`/run/user/` + `id -u edhunter`:

```cron
XDG_RUNTIME_DIR=/run/user/1001
CRON_TZ=America/Bogota
# Completo, domingo 01:00. Diferencial, lunes a sábado 01:00. Retención: 4 completos.
0 1 * * 0    podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa --type=full backup >> ~/halsa/backups.log 2>&1
0 1 * * 1-6  podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa --type=diff backup >> ~/halsa/backups.log 2>&1
# Ensayo de restauración: el primer lunes de cada mes, 03:00.
0 3 1-7 * 1  ~/halsa/deploy/scripts/restaurar-prueba.sh >> ~/halsa/restaurar-prueba.log 2>&1
```

El WAL se archiva continuamente (`archive_command`, `archive_timeout = 300`): como mucho se pierden 5 minutos si el
VPS muere de golpe.

Primera vez: `pgbackrest --stanza=halsa stanza-create`, `check` y un `--type=full backup` manual.

## Estado

```bash
C="podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa"
$C info          # backups, WAL archivado, tamaño
$C check         # archive_command funciona de punta a punta
```

## Ensayo mensual: `restaurar-prueba.sh`

```bash
~/halsa/deploy/scripts/restaurar-prueba.sh
```

Restaura el último backup en un contenedor y un volumen **temporales**, fuera de las redes de la aplicación. Espera a
que termine la recuperación y verifica que el conteo de `atel.casos_medicos` y un `SELECT` sobre `auditoria.accesos`
respondan. Después lo destruye todo. Sale con `0` si todo pasó y con `1` si no. Hay que registrar el resultado.

### Ensayo de PITR (criterio *staging*)

```bash
# 1. Insertar una fila marcadora y anotar la hora (UTC).
P="podman exec -u postgres halsa-postgres psql"
M=$(uuidgen)
$P -d halsa -c \
  "CREATE TABLE IF NOT EXISTS public.ensayo_pitr(id uuid primary key, en timestamptz default now());
   INSERT INTO public.ensayo_pitr(id) VALUES ('$M');"
T=$(date -u '+%Y-%m-%d %H:%M:%S+00'); sleep 2
$P -c "SELECT pg_switch_wal();"     # fuerza el archivo del WAL

# 2. Restaurar a ese instante y comprobar que la fila está.
~/halsa/deploy/scripts/restaurar-prueba.sh --hasta "$T" \
  --verificar-sql "SELECT 1 FROM public.ensayo_pitr WHERE id = '$M'"

# 3. Limpiar.
$P -d halsa -c "DROP TABLE public.ensayo_pitr;"
```

## Restauración real (incidente)

> Se pierden los datos posteriores al instante elegido. Requiere la aprobación del responsable de la plataforma.

1. Detener la escritura: `systemctl --user stop halsa-api halsa-worker halsa-bff`. NPM responde `502`.
2. Elegir el instante: el último bueno conocido, por ejemplo justo antes del despliegue que rompió el esquema.
3. Restaurar sobre el volumen de producción, con los mismos secretos, entorno y red que `halsa-postgres.container`:
   ```bash
   systemctl --user stop halsa-postgres
   set -a; source ~/halsa/deploy/.env; set +a
   V=(); for s in pg_postgres_password pg_owner_password pg_app_password pg_clinico_password pg_ingesta_password \
                  pg_worker_password backups_s3_key backups_s3_secret backups_cipher_pass; do
     V+=(-v "$HALSA_SECRETOS/$s:/run/secrets/$s:ro"); done
   podman run --rm --network halsa_egreso "${V[@]}" -e HALSA_BACKUPS_BUCKET -e HALSA_BACKUPS_REGION -e HALSA_BACKUPS_ENDPOINT \
     -v halsa_postgres-datos:/var/lib/postgresql/data --entrypoint bash "localhost/halsa-postgres:$HALSA_POSTGRES_TAG" -c '
       /usr/local/bin/halsa-arrancar.sh true &&
       su postgres -c "pgbackrest --stanza=halsa --delta --type=time \"--target=2026-10-14 15:30:00+00\" --target-action=promote restore"'
   systemctl --user start halsa-postgres
   ```
4. Verificar con SQL (`podman exec -u postgres halsa-postgres psql -d halsa`) y luego
   `~/halsa/deploy/scripts/desplegar.sh <tag correcto>`, que levanta `api`, `worker` y `bff`.
5. Tomar enseguida un backup completo (`--type=full backup`): la línea de tiempo cambió.

## VPS perdido (reconstrucción)

Runbook 01 en un VPS nuevo, con los secretos restaurados desde la custodia (runbook 02). En el paso 6, con la imagen
`halsa-postgres` ya cargada y **antes** de `instalar.sh`: si `instalar.sh` arrancara primero, la base vacía archivaría
WAL de otro sistema en la stanza. Restaura la base del bucket en el volumen, que todavía no existe:

```bash
podman volume create halsa_postgres-datos
# El paso 3 de arriba, desde "set -a", con dos cambios: sin --network halsa_egreso (la red aún no existe; la
# predeterminada tiene egreso) y --type=default en lugar de --type=time y --target (lo último del bucket).
```

Después `instalar.sh` **sin** `stanza-create`, `stanza-upgrade` si cambió la versión de PostgreSQL, y el primer
`desplegar.sh`.

## Pendientes conocidos

- Los soportes cifrados del volumen `archivos` (SPEC 07) no tienen copia fuera del VPS. Sin el VPS, los PDF se pierden
  aunque su metadata esté en la base. Necesita su propia spec (p. ej. replicar al bucket de backups, ya cifrados con
  la DEK de cada tenant).

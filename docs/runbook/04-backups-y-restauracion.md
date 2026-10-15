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

`/etc/cron.d/halsa-backups` en el VPS (hora de Colombia):

```cron
CRON_TZ=America/Bogota
C=docker compose -f /opt/halsa/deploy/docker-compose.prod.yml exec -T -u postgres postgres pgbackrest --stanza=halsa
# Completo, domingo 01:00. Diferencial, lunes a sábado 01:00. Retención: 4 completos.
0 1 * * 0    root  $C --type=full backup  >> /var/log/halsa-backups.log 2>&1
0 1 * * 1-6  root  $C --type=diff backup  >> /var/log/halsa-backups.log 2>&1
# Ensayo de restauración: el primer lunes de cada mes, 03:00.
0 3 1-7 * 1  root  /opt/halsa/deploy/scripts/restaurar-prueba.sh >> /var/log/halsa-restaurar-prueba.log 2>&1
```

El WAL se archiva continuamente (`archive_command`, `archive_timeout = 300`): como mucho se pierden 5 minutos si el
VPS muere de golpe.

Primera vez: `pgbackrest --stanza=halsa stanza-create`, `check` y un `--type=full backup` manual.

## Estado

```bash
C="docker compose -f /opt/halsa/deploy/docker-compose.prod.yml exec -T -u postgres postgres pgbackrest --stanza=halsa"
$C info          # backups, WAL archivado, tamaño
$C check         # archive_command funciona de punta a punta
```

## Ensayo mensual: `restaurar-prueba.sh`

```bash
/opt/halsa/deploy/scripts/restaurar-prueba.sh
```

Restaura el último backup en un contenedor y un volumen **temporales**, fuera de las redes de la aplicación. Espera a
que termine la recuperación y verifica que el conteo de `atel.casos_medicos` y un `SELECT` sobre `auditoria.accesos`
respondan. Después lo destruye todo. Sale con `0` si todo pasó y con `1` si no. Hay que registrar el resultado.

### Ensayo de PITR (criterio *staging*)

```bash
# 1. Insertar una fila marcadora y anotar la hora (UTC).
C="docker compose -f /opt/halsa/deploy/docker-compose.prod.yml"
M=$(uuidgen)
$C exec -T -u postgres postgres psql -d halsa -c \
  "CREATE TABLE IF NOT EXISTS public.ensayo_pitr(id uuid primary key, en timestamptz default now());
   INSERT INTO public.ensayo_pitr(id) VALUES ('$M');"
T=$(date -u '+%Y-%m-%d %H:%M:%S+00'); sleep 2
$C exec -T -u postgres postgres psql -c "SELECT pg_switch_wal();"     # fuerza el archivo del WAL

# 2. Restaurar a ese instante y comprobar que la fila está.
/opt/halsa/deploy/scripts/restaurar-prueba.sh --hasta "$T" \
  --verificar-sql "SELECT 1 FROM public.ensayo_pitr WHERE id = '$M'"

# 3. Limpiar.
$C exec -T -u postgres postgres psql -d halsa -c "DROP TABLE public.ensayo_pitr;"
```

## Restauración real (incidente)

> Se pierden los datos posteriores al instante elegido. Requiere la aprobación del responsable de la plataforma.

1. Detener la escritura: `docker compose stop api worker bff`. NPM responde `502`.
2. Elegir el instante: el último bueno conocido, por ejemplo justo antes del despliegue que rompió el esquema.
3. Restaurar sobre el volumen de producción:
   ```bash
   C="docker compose -f /opt/halsa/deploy/docker-compose.prod.yml"
   $C stop postgres
   $C run --rm --entrypoint bash postgres -c '
     /usr/local/bin/halsa-arrancar.sh true &&
     su postgres -c "pgbackrest --stanza=halsa --delta --type=time \"--target=2026-10-14 15:30:00+00\" --target-action=promote restore"'
   $C up -d postgres
   ```
4. Verificar con SQL y luego levantar `api`, `worker` y `bff` con la imagen correcta.
5. Tomar enseguida un backup completo (`--type=full backup`): la línea de tiempo cambió.

## VPS perdido (reconstrucción)

Runbook 01 en un VPS nuevo, con los secretos restaurados desde la custodia (runbook 02). Antes del primer
`desplegar.sh`, restaurar la base del bucket (paso 3 de arriba, sin `--type=time` para tomar lo último) y
correr `stanza-upgrade` si cambió la versión de PostgreSQL.

## Pendientes conocidos

- Los soportes cifrados del volumen `archivos` (SPEC 07) no tienen copia fuera del VPS. Sin el VPS, los PDF se pierden
  aunque su metadata esté en la base. Necesita su propia spec (p. ej. replicar al bucket de backups, ya cifrados con
  la DEK de cada tenant).

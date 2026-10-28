#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# restaurar-prueba.sh — ensayo de restauración (SPEC 13, runbook 04). Se ejecuta cada mes en el VPS.
#
# Restaura un backup de pgBackRest en un contenedor TEMPORAL (su propio volumen, sin red
# hacia la aplicación), lo arranca, verifica y lo destruye. Nunca toca el PostgreSQL de
# producción.
#
# Uso:
#   ./restaurar-prueba.sh                             # el último backup (la noche anterior)
#   ./restaurar-prueba.sh --hasta "2026-10-14 15:30:00+00"   # PITR a ese instante
#   ./restaurar-prueba.sh --hasta "..." --verificar-sql "SELECT count(*) FROM atel.casos_medicos WHERE id = '...'"
#
# Verificaciones (falla con 1 si alguna falla):
#   1. el servidor restaurado arranca y termina la recuperación;
#   2. el conteo de casos (atel.casos_medicos) responde;
#   3. un SELECT sobre la auditoría (auditoria.accesos) responde y muestra su última fila;
#   4. (opcional) --verificar-sql devuelve al menos una fila: el ensayo de PITR del runbook 04.
#
# Variables: HALSA_DIR (por defecto, la carpeta que contiene deploy/: /opt/halsa o ~/halsa), HALSA_POSTGRES_IMAGEN
# (la imagen que construye deploy/postgres/Dockerfile y carga el CI; por defecto localhost/halsa-postgres:17).
# Los secretos, de HALSA_SECRETOS en el .env.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

HALSA_DIR="${HALSA_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
IMAGEN="${HALSA_POSTGRES_IMAGEN:-localhost/halsa-postgres:17}"
NOMBRE="halsa-restaurar-prueba-$(date +%s)"
VOLUMEN="$NOMBRE-datos"

hasta=""
verificar_sql=""
while [[ $# -gt 0 ]]; do
    case "$1" in
        --hasta) hasta="$2"; shift 2 ;;
        --verificar-sql) verificar_sql="$2"; shift 2 ;;
        *) echo "argumento desconocido: $1" >&2; exit 2 ;;
    esac
done

set -a
# shellcheck source=/dev/null  # the VPS's .env: not in the repo
source "$HALSA_DIR/deploy/.env"
set +a
SECRETOS="${HALSA_SECRETOS:-/etc/halsa/secretos}"

limpiar() {
    docker rm -f "$NOMBRE" >/dev/null 2>&1 || true
    docker volume rm -f "$VOLUMEN" >/dev/null 2>&1 || true
}
trap limpiar EXIT

# All that halsa-arrancar.sh reads: the bucket's for pgBackRest, and the role passwords it copies before initdb.
secretos_montados=()
for s in backups_s3_key backups_s3_secret backups_cipher_pass pg_postgres_password pg_owner_password pg_app_password \
         pg_clinico_password pg_ingesta_password pg_worker_password; do
    secretos_montados+=(-v "$SECRETOS/$s:/run/secrets/$s:ro")
done

entorno=(-e HALSA_BACKUPS_BUCKET -e HALSA_BACKUPS_REGION -e HALSA_BACKUPS_ENDPOINT)

opciones_restore=(--stanza=halsa --delta --log-level-console=warn)
if [[ -n "$hasta" ]]; then
    opciones_restore+=(--type=time "--target=$hasta" --target-action=promote)
else
    opciones_restore+=(--type=default)
fi

echo "==> restaurando ${hasta:-el último backup} en el volumen temporal $VOLUMEN"
docker volume create "$VOLUMEN" >/dev/null
docker run --rm "${secretos_montados[@]}" "${entorno[@]}" \
    -v "$VOLUMEN:/var/lib/postgresql/data" \
    --entrypoint bash "$IMAGEN" -c '
        set -e
        /usr/local/bin/halsa-arrancar.sh true
        chown postgres:postgres /var/lib/postgresql/data
        su postgres -c "pgbackrest restore '"${opciones_restore[*]}"'"
    '

# Default bridge network: archive-get must read the WAL from the bucket to recover, but the temporary server is out of
# the application's networks. archive_mode=off: it never writes WAL to the production repository.
echo "==> arrancando el servidor restaurado (sin archive: no escribe WAL al bucket)"
docker run -d --name "$NOMBRE" "${secretos_montados[@]}" "${entorno[@]}" \
    -v "$VOLUMEN:/var/lib/postgresql/data" \
    "$IMAGEN" postgres -c config_file=/etc/postgresql/postgresql.conf \
        -c archive_mode=off -c archive_command=/bin/true >/dev/null

for _ in $(seq 1 120); do
    if docker exec -u postgres "$NOMBRE" pg_isready -q 2>/dev/null \
       && [[ "$(docker exec -u postgres "$NOMBRE" psql -tAc 'SELECT pg_is_in_recovery()' 2>/dev/null)" == "f" ]]; then
        break
    fi
    sleep 2
done

# As the postgres OS user: pg_hba.conf only admits it locally, by peer.
consulta() { docker exec -u postgres "$NOMBRE" psql -d halsa -v ON_ERROR_STOP=1 -tAc "$1"; }

echo "==> verificando"
[[ "$(docker exec -u postgres "$NOMBRE" psql -tAc 'SELECT pg_is_in_recovery()')" == "f" ]] \
    || { echo "FALLO: el servidor no terminó la recuperación" >&2; exit 1; }

casos="$(consulta 'SELECT count(*) FROM atel.casos_medicos')" \
    || { echo "FALLO: no se pudo contar atel.casos_medicos" >&2; exit 1; }
echo "    casos: $casos"

ultima="$(consulta 'SELECT max(ocurrido_en) FROM auditoria.accesos')" \
    || { echo "FALLO: no se pudo leer auditoria.accesos" >&2; exit 1; }
echo "    última fila de auditoría: ${ultima:-(vacía)}"

if [[ -n "$verificar_sql" ]]; then
    filas="$(consulta "$verificar_sql" | grep -c . || true)"
    [[ "$filas" -ge 1 ]] || { echo "FALLO: --verificar-sql no devolvió filas" >&2; exit 1; }
    echo "    --verificar-sql: $filas fila(s)"
fi

echo "==> OK: restauración verificada; el contenedor y el volumen temporales se destruyen"

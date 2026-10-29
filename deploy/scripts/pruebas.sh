#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# pruebas.sh — datos de la fase de pruebas en producción: sembrarlos, ver cuánto hay y borrarlos TODOS al terminar.
#
# Corre en el VPS, como el usuario dueño de los Quadlets (rootless), nunca como root. Habla con PostgreSQL como `postgres` por
# el socket local del contenedor (pg_hba: peer), igual que los demás scripts de operación.
#
#   pruebas.sh sembrar            crea las ARL PRUEBA_A/PRUEBA_B, las IPS ficticias y sus casos (deploy/pruebas/sembrar.sql).
#                                 Idempotente. Imprime el código de activación canjeable del portal del paciente.
#   pruebas.sh estado             cuántas filas de prueba hay por tabla (no cambia nada).
#   pruebas.sh purgar [--dry]     borra todo lo de prueba (deploy/pruebas/purgar.sql) y los archivos de soportes.
#                [--sin-parar]    --dry solo cuenta. Por defecto detiene halsa-api/worker/bff mientras borra, para que nadie
#                [--si]           cree filas a medias; --sin-parar lo omite. --si no pide confirmación (para automatizar).
#
# Los usuarios de Keycloak de prueba van aparte: deploy/keycloak/crear-usuarios-prueba.sh crear|eliminar.
# El procedimiento completo y los casos de prueba están en deploy/pruebas/README.md.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

[[ "$(id -u)" -ne 0 ]] || { echo "ERROR: ejecútalo como el usuario dueño de los Quadlets, no como root" >&2; exit 1; }
# systemctl --user needs it; sudo -u and cron do not set it.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

HALSA_DIR="${HALSA_DIR:-$(cd "$(dirname "$0")/../.." && pwd)}"
SQL="$HALSA_DIR/deploy/pruebas"
CONTENEDOR="${HALSA_POSTGRES_CONTENEDOR:-halsa-postgres}"
BASE="${HALSA_POSTGRES_BASE:-halsa}"
IMAGEN="${HALSA_POSTGRES_IMAGEN:-localhost/halsa-postgres:17}"
VOLUMEN_ARCHIVOS="${HALSA_VOLUMEN_ARCHIVOS:-halsa-archivos}"
SERVICIOS=(halsa-bff halsa-api halsa-worker)

# Los mismos tenants que sembrar.sql y purgar.sql (esta lista solo se usa para informar y para borrar sus carpetas de archivos).
TENANTS_PRUEBA=(PRUEBA_A PRUEBA_B)

info() { echo "==> $(date -u +%H:%M:%S) $*"; }
fallo() { echo "ERROR: $*" >&2; exit 1; }

psql_pg() { podman exec -i -u postgres "$CONTENEDOR" psql -d "$BASE" -v ON_ERROR_STOP=1 "$@"; }

verificar() {
    [[ -f "$SQL/sembrar.sql" && -f "$SQL/purgar.sql" ]] || fallo "faltan sembrar.sql/purgar.sql en $SQL"
    podman container exists "$CONTENEDOR" || fallo "no existe el contenedor $CONTENEDOR"
    psql_pg -tAc 'SELECT 1' >/dev/null || fallo "no puedo conectar a la base $BASE"
}

estado() {
    psql_pg -P pager=off <<'SQL'
SELECT t.tenant_id,
       (SELECT count(*) FROM atel.casos_medicos     WHERE tenant_id = t.tenant_id) AS casos,
       (SELECT count(*) FROM atel.incapacidades     WHERE tenant_id = t.tenant_id) AS incapacidades,
       (SELECT count(*) FROM atel.dictamenes_pcl    WHERE tenant_id = t.tenant_id) AS dictamenes,
       (SELECT count(*) FROM sst.plazos             WHERE tenant_id = t.tenant_id) AS plazos,
       (SELECT count(*) FROM atel.codigos_activacion WHERE tenant_id = t.tenant_id) AS codigos,
       (SELECT count(*) FROM atel.solicitudes_copia_hc WHERE tenant_id = t.tenant_id) AS copias_hc,
       (SELECT count(*) FROM clinico.historia_clinica_detalle WHERE tenant_id = t.tenant_id) AS hc,
       (SELECT count(*) FROM clinico.soportes       WHERE tenant_id = t.tenant_id) AS soportes,
       (SELECT count(*) FROM auditoria.accesos      WHERE tenant_id = t.tenant_id) AS auditoria
FROM (VALUES ('PRUEBA_A'), ('PRUEBA_B')) AS t(tenant_id);
SQL
}

sembrar() {
    verificar
    local existe secreto
    existe="$(psql_pg -tAc "SELECT 1 FROM atel.codigos_activacion WHERE id = 'PRB013'")"
    # Crockford base32 (sin I, L, O, U), 10 caracteres: lo que espera FormatoCodigoActivacion. Nunca se guarda en git.
    secreto="$(LC_ALL=C tr -dc '0123456789ABCDEFGHJKMNPQRSTVWXYZ' </dev/urandom | head -c 10 || true)"
    [[ ${#secreto} -eq 10 ]] || fallo "no pude generar el secreto del código de activación"

    info "Sembrando datos de prueba (PRUEBA_A, PRUEBA_B)"
    psql_pg -v "secreto_activo=$secreto" -P pager=off < "$SQL/sembrar.sql"

    echo
    if [[ -n "$existe" ]]; then
        info "El código PRB013 ya existía: se conserva su secreto original. Para uno nuevo, emítelo desde el backoffice sobre el caso A13."
    else
        info "Código de activación canjeable (caso A13, trabajador CC 99100003): PRB013-$secreto"
        echo "    Entrégaselo a paciente.3 por un canal aparte; vence en 29 días y se muestra solo ahora."
    fi
    info "Siguiente: deploy/keycloak/crear-usuarios-prueba.sh crear   (usuarios de Keycloak)"
}

# Detiene los servicios y garantiza que vuelvan a arrancar aunque la purga falle.
PARADOS=0
reanudar() {
    if [[ "$PARADOS" -eq 1 ]]; then
        PARADOS=0
        info "Arrancando ${SERVICIOS[*]}"
        systemctl --user start halsa-api halsa-worker halsa-bff || echo "ADVERTENCIA: no arrancaron todos los servicios; revisa systemctl --user status" >&2
    fi
}

purgar() {
    local dry=0 parar=1 confirmar=1
    for arg in "$@"; do
        case "$arg" in
            --dry) dry=1 ;;
            --sin-parar) parar=0 ;;
            --si) confirmar=0 ;;
            *) fallo "argumento desconocido: $arg" ;;
        esac
    done
    verificar

    info "Lo que se borraría (ensayo, no cambia nada):"
    psql_pg -v dry=1 -P pager=off < "$SQL/purgar.sql"

    if [[ "$dry" -eq 1 ]]; then
        info "--dry: terminado, no se borró nada"
        return 0
    fi

    if [[ "$confirmar" -eq 1 ]]; then
        [[ -t 0 ]] || fallo "sin terminal no puedo pedir confirmación: usa --si"
        echo
        echo "Esto borra DEFINITIVAMENTE todo lo de ${TENANTS_PRUEBA[*]} (incluida su auditoría) y sus archivos."
        echo "Los backups de pgBackRest conservarán lo anterior hasta que venza su retención."
        read -rp "Escribe PURGAR para continuar: " respuesta
        [[ "$respuesta" == "PURGAR" ]] || fallo "cancelado"
    fi

    if [[ "$parar" -eq 1 ]]; then
        trap reanudar EXIT
        info "Deteniendo ${SERVICIOS[*]} (evita filas nuevas durante la purga)"
        systemctl --user stop "${SERVICIOS[@]}"
        PARADOS=1
    fi

    info "Purgando la base de datos"
    psql_pg -P pager=off < "$SQL/purgar.sql"

    info "Borrando archivos de soportes de ${TENANTS_PRUEBA[*]} del volumen $VOLUMEN_ARCHIVOS"
    # {Raiz}/{tenant}/{yyyy}/{MM}/{archivoId}.bin (LocalEncryptedFileStore): la carpeta del tenant contiene solo sus archivos.
    local carpetas=() t
    for t in "${TENANTS_PRUEBA[@]}"; do
        [[ "$t" =~ ^PRUEBA_[A-Z0-9_]+$ ]] || fallo "tenant de prueba inválido: $t"
        carpetas+=("/archivos/$t")
    done
    if ! podman run --rm --network none -v "$VOLUMEN_ARCHIVOS:/archivos" --entrypoint /bin/sh "$IMAGEN" -c 'rm -rf -- "$@"' sh "${carpetas[@]}"; then
        echo "ADVERTENCIA: no pude borrar los archivos. Hazlo a mano: podman volume inspect $VOLUMEN_ARCHIVOS y rm -rf de las carpetas ${TENANTS_PRUEBA[*]}" >&2
    fi

    reanudar
    trap - EXIT

    info "Estado final (debe estar en ceros):"
    estado
    echo
    info "Pendiente a mano: usuarios de Keycloak (crear-usuarios-prueba.sh eliminar), integraciones mTLS de prueba (runbook 06) y,"
    info "si quedó algo, la caché: systemctl --user restart halsa-redis (caduca sola en minutos)."
}

case "${1:-}" in
    sembrar) sembrar ;;
    estado) verificar; estado ;;
    purgar) shift; purgar "$@" ;;
    *) sed -n '2,20p' "$0" | sed 's/^# \{0,1\}//' >&2; exit 2 ;;
esac

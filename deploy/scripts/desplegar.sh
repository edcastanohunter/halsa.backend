#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# desplegar.sh <tag> — despliega una versión en el VPS (SPEC 13, runbook 03).
#
#   1. docker compose pull                       (imágenes publicadas por release.yml en GHCR)
#   2. docker compose run --rm migrador          (Halsa:Rol=Migrador; sale con 0 o 1)
#   3. docker compose up -d api worker bff
#   4. espera hasta 120 s a que /health de api y worker esté en Healthy
#   5. sincroniza la configuración de borde con Nginx Proxy Manager (sincronizar-npm.sh)
#   6. si el paso 4 falla, vuelve al tag anterior. Las migraciones solo avanzan: deben ser compatibles con
#      la versión anterior (expand/contract, revisado en el PR). Si no lo son, el último
#      recurso es el restore PITR del runbook 04.
#
# Lo ejecuta una persona en el VPS (el CI no tiene SSH). Uso:
#   /opt/halsa/deploy/scripts/desplegar.sh v1.0.1
#
# Variables: HALSA_ESPERA_SALUD (segundos, por defecto 120).
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

nuevo="${1:-}"
[[ "$nuevo" =~ ^v[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$ ]] || { echo "uso: desplegar.sh <tag vX.Y.Z>" >&2; exit 2; }

DIR="$(cd "$(dirname "$0")/.." && pwd)"
ENV="$DIR/.env"
ESPERA="${HALSA_ESPERA_SALUD:-120}"
cd "$DIR"

[[ -f "$ENV" ]] || { echo "falta $ENV (cp .env.ejemplo .env, runbook 01)" >&2; exit 1; }

compose() { docker compose -f "$DIR/docker-compose.prod.yml" --env-file "$ENV" "$@"; }
info() { echo "==> $(date -u +%H:%M:%S) $*"; }

fijar_tag() {
    if grep -q '^HALSA_TAG=' "$ENV"; then
        sed -i "s/^HALSA_TAG=.*/HALSA_TAG=$1/" "$ENV"
    else
        echo "HALSA_TAG=$1" >> "$ENV"
    fi
}

# "Healthy" in the JSON of /health (UIResponseWriter). Degraded is not enough to call a deploy good.
sano() {
    compose exec -T "$1" curl -fsS http://localhost:8080/health 2>/dev/null | grep -q '"status":"Healthy"'
}

# Halsa's edge config to Nginx Proxy Manager (and its network, if NPM lost it). Idempotent; with variable
# upstreams NPM needs no reload for the recreated containers, but a new tag may bring new edge files.
sincronizar_npm() {
    bash "$DIR/scripts/sincronizar-npm.sh"
}

esperar_salud() {
    local limite=$((SECONDS + ESPERA))
    while (( SECONDS < limite )); do
        if sano api && sano worker; then
            return 0
        fi
        sleep 5
    done
    return 1
}

anterior="$(grep -E '^HALSA_TAG=' "$ENV" | cut -d= -f2- || true)"
info "desplegando $nuevo (anterior: ${anterior:-ninguno})"

fijar_tag "$nuevo"

info "1/4 pull"
if ! compose --profile migracion pull; then
    fijar_tag "$anterior"
    echo "FALLO: no se pudieron bajar las imágenes de $nuevo; nada cambió." >&2
    exit 1
fi

info "2/4 migraciones"
if ! compose --profile migracion run --rm migrador; then
    fijar_tag "$anterior"
    echo "FALLO: el Migrador terminó con error; la versión anterior sigue corriendo." >&2
    exit 1
fi

info "3/4 up -d api worker bff"
compose up -d api worker bff

info "4/4 esperando /health en Healthy (hasta ${ESPERA} s)"
if esperar_salud; then
    sincronizar_npm || { echo "FALLO: la versión está Healthy pero sincronizar-npm.sh falló (runbook 03)." >&2; exit 1; }
    info "OK: $nuevo desplegado y Healthy"
    exit 0
fi

echo "FALLO: $nuevo no llegó a Healthy en ${ESPERA} s." >&2
compose ps api worker bff >&2 || true

if [[ -z "$anterior" || "$anterior" == "$nuevo" ]]; then
    echo "No hay un tag anterior al que volver: revisa los logs (runbook 03)." >&2
    exit 1
fi

info "rollback a $anterior (las migraciones de $nuevo quedan aplicadas)"
fijar_tag "$anterior"
compose up -d api worker bff
if esperar_salud; then
    info "rollback OK: $anterior Healthy"
else
    echo "FALLO: tampoco $anterior llegó a Healthy. Runbook 03, 'Rollback fallido'." >&2
fi
exit 1

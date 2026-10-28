#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# desplegar.sh <tag> — despliega una versión en el VPS (SPEC 13 y SPEC 14, runbook 03).
#
#   1. verifica que las imágenes del tag estén cargadas (sin registry: las carga recibir-despliegue.sh)
#   2. docker compose run --rm migrador          (Halsa:Rol=Migrador; sale con 0 o 1)
#   3. docker compose up -d api worker bff
#      Con Podman Quadlets rootless (deploy-quadlets/instalar.sh), 2 y 3 son `systemctl --user start
#      halsa-migrador` y `systemctl --user restart halsa-api halsa-worker halsa-bff`: las unidades leen HALSA_TAG
#      del mismo .env. Entonces corre como el usuario dueño de los Quadlets, nunca como root.
#   4. espera hasta 120 s a que /health de api y worker esté en Healthy
#   5. sincroniza la configuración de borde con Nginx Proxy Manager (sincronizar-npm.sh)
#   6. si el paso 4 falla, vuelve al tag anterior. Las migraciones solo avanzan: deben ser compatibles con
#      la versión anterior (expand/contract, revisado en el PR). Si no lo son, el último
#      recurso es el restore PITR del runbook 04.
#
# Lo llama halsa-recibir-despliegue (scripts/recibir-despliegue.sh) en cada despliegue del CI. Una persona lo
# ejecuta a mano (como root con el compose, como el dueño de los Quadlets si no) solo para volver a un tag que siga
# cargado (podman image ls localhost/halsa-backend):
#   ~/halsa/deploy/scripts/desplegar.sh v1.0.41          (con el compose: /opt/halsa/deploy/scripts/...)
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

# The rootless Quadlets are installed (deploy-quadlets/instalar.sh): this user's systemd runs Halsa, not the compose.
usar_quadlets() {
    [[ -f "$HOME/.config/containers/systemd/halsa-api.container" ]]
}
# systemctl --user and rootless podman need it; sudo -u and cron do not set it.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

# Without the Quadlets, a non-root user would bring up a second, rootless compose stack next to nothing.
if ! usar_quadlets && [[ "$(id -u)" -ne 0 ]]; then
    echo "faltan los Quadlets de Halsa en ~/.config/containers/systemd: bash ~/halsa/deploy-quadlets/instalar.sh" \
         "(runbook 01, paso 6). Las imágenes quedan cargadas." >&2
    exit 1
fi

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
    if usar_quadlets; then
        podman exec "halsa-$1" curl -fsS http://localhost:8080/health 2>/dev/null | grep -q '"status":"Healthy"'
    else
        compose exec -T "$1" curl -fsS http://localhost:8080/health 2>/dev/null | grep -q '"status":"Healthy"'
    fi
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

info "1/4 imágenes"
for imagen in "localhost/halsa-backend:$nuevo" "localhost/halsa-bff-paciente:$nuevo"; do
    if ! podman image inspect "$imagen" >/dev/null 2>&1 && ! docker image inspect "$imagen" >/dev/null 2>&1; then
        fijar_tag "$anterior"
        echo "FALLO: falta la imagen $imagen en el VPS; nada cambió." >&2
        exit 1
    fi
done

info "2/4 migraciones"
if usar_quadlets; then
    systemctl --user reset-failed halsa-migrador.service 2>/dev/null || true
    if ! systemctl --user start halsa-migrador.service; then
        fijar_tag "$anterior"
        journalctl --user -u halsa-migrador.service -n 50 --no-pager >&2 || true
        echo "FALLO: el Migrador terminó con error; la versión anterior sigue corriendo." >&2
        exit 1
    fi
else
    if ! compose --profile migracion run --rm migrador; then
        fijar_tag "$anterior"
        echo "FALLO: el Migrador terminó con error; la versión anterior sigue corriendo." >&2
        exit 1
    fi
fi

info "3/4 iniciando / reiniciando api worker bff"
if usar_quadlets; then
    # A unit that fails to start shows up in the health wait below, which rolls back.
    systemctl --user restart halsa-api.service halsa-worker.service halsa-bff.service || true
else
    compose up -d api worker bff
fi

info "4/4 esperando /health en Healthy (hasta ${ESPERA} s)"
if esperar_salud; then
    sincronizar_npm || { echo "FALLO: la versión está Healthy pero sincronizar-npm.sh falló (runbook 03)." >&2; exit 1; }
    info "OK: $nuevo desplegado y Healthy"
    exit 0
fi

echo "FALLO: $nuevo no llegó a Healthy en ${ESPERA} s." >&2
if usar_quadlets; then
    systemctl --user status halsa-api.service halsa-worker.service halsa-bff.service >&2 || true
else
    compose ps api worker bff >&2 || true
fi

if [[ -z "$anterior" || "$anterior" == "$nuevo" ]]; then
    echo "No hay un tag anterior al que volver: revisa los logs (runbook 03)." >&2
    exit 1
fi

info "rollback a $anterior (las migraciones de $nuevo quedan aplicadas)"
fijar_tag "$anterior"
if usar_quadlets; then
    systemctl --user restart halsa-api.service halsa-worker.service halsa-bff.service || true
else
    compose up -d api worker bff
fi
if esperar_salud; then
    info "rollback OK: $anterior Healthy"
else
    echo "FALLO: tampoco $anterior llegó a Healthy. Runbook 03, 'Rollback fallido'." >&2
fi
exit 1

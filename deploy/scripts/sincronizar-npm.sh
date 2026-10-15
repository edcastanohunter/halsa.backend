#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# sincronizar-npm.sh — lleva la configuración de borde de Halsa al Nginx Proxy Manager (SPEC 13).
#
# El compose de NPM NO se modifica. Este script, idempotente:
#   1. une NPM a la red halsa_borde con su IP fija (docker network connect), si no lo está;
#   2. renderiza deploy/npm (solo variables HALSA_*) y lo compara con lo que tiene NPM;
#   3. si cambió: guarda una copia de lo anterior, copia lo nuevo a <NPM_DATA>/nginx/custom/halsa
#      y la PKI pública a <NPM_DATA>/halsa-pki, y agrega a http_top.conf la línea que incluye
#      http.conf de Halsa (sin tocar el resto del archivo);
#   4. valida con `nginx -t` DENTRO de NPM. Si falla, restaura la copia y termina con 1: los otros
#      sitios de NPM nunca reciben una configuración rota;
#   5. recarga NPM.
#
# Lo llaman desplegar.sh y el cron de la CRL (runbook 06); a mano, después de recrear NPM
# (runbook 03). Uso:  /opt/halsa/deploy/scripts/sincronizar-npm.sh
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

DIR="$(cd "$(dirname "$0")/.." && pwd)"
ENV="$DIR/.env"
[[ -f "$ENV" ]] || { echo "falta $ENV (runbook 01)" >&2; exit 1; }
set -a
# shellcheck disable=SC1090
source "$ENV"
set +a

NPM="${HALSA_NPM_CONTENEDOR:?HALSA_NPM_CONTENEDOR}"
DATA="${HALSA_NPM_DATA:?HALSA_NPM_DATA}"
IP="${HALSA_NPM_IP:-172.30.0.10}"
RED="${HALSA_RED_BORDE:-halsa_borde}"
PKI="${HALSA_PKI_PUBLICADA:-/etc/halsa/pki/publicar}"
: "${HALSA_UPSTREAM_KEYCLOAK:?HALSA_UPSTREAM_KEYCLOAK}"
export HALSA_UPSTREAM_API="${HALSA_UPSTREAM_API:-http://api:8080}"
export HALSA_UPSTREAM_BFF="${HALSA_UPSTREAM_BFF:-http://bff:8080}"

DESTINO="$DATA/nginx/custom/halsa"
DESTINO_PKI="$DATA/halsa-pki"
HTTP_TOP="$DATA/nginx/custom/http_top.conf"
LINEA='include /data/nginx/custom/halsa/http.conf;'

info() { echo "==> $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

command -v envsubst >/dev/null || die "falta envsubst (apt install gettext-base)"
docker inspect "$NPM" >/dev/null 2>&1 || die "no existe el contenedor de NPM '$NPM'"
docker network inspect "$RED" >/dev/null 2>&1 || die "no existe la red $RED: levanta primero el compose de Halsa"
[[ -f "$PKI/halsa-integraciones-ca.pem" && -f "$PKI/halsa-integraciones.crl" ]] \
    || die "falta la PKI pública en $PKI (runbook 06)"

# ── 1. Red ────────────────────────────────────────────────────────────────────
ip_actual="$(docker inspect -f "{{with index .NetworkSettings.Networks \"$RED\"}}{{.IPAddress}}{{end}}" "$NPM")"
if [[ -z "$ip_actual" ]]; then
    info "uniendo $NPM a $RED con la IP $IP"
    docker network connect --ip "$IP" "$RED" "$NPM"
elif [[ "$ip_actual" != "$IP" ]]; then
    die "$NPM está en $RED con la IP $ip_actual, no $IP (Red__ProxyConfiable no lo aceptaría)"
fi

# ── 2. Render ─────────────────────────────────────────────────────────────────
trabajo="$(mktemp -d)"
trap 'rm -rf "$trabajo"' EXIT
sh "$DIR/npm/renderizar.sh" "$DIR/npm" "$trabajo/halsa"
mkdir -p "$trabajo/halsa-pki"
cp "$PKI/halsa-integraciones-ca.pem" "$PKI/halsa-integraciones.crl" "$trabajo/halsa-pki/"

linea_presente() { [[ -f "$HTTP_TOP" ]] && grep -qxF "$LINEA" "$HTTP_TOP"; }

if diff -r "$trabajo/halsa" "$DESTINO" >/dev/null 2>&1 \
   && diff -r "$trabajo/halsa-pki" "$DESTINO_PKI" >/dev/null 2>&1 \
   && linea_presente; then
    info "sin cambios en la configuración de Halsa en NPM"
    exit 0
fi

# ── 3. Copia, con respaldo ───────────────────────────────────────────────────
respaldo="$trabajo/respaldo"
mkdir -p "$respaldo"
habia_config=false
if [[ -d "$DESTINO" ]]; then
    habia_config=true
    cp -a "$DESTINO" "$respaldo/halsa"
fi
[[ -d "$DESTINO_PKI" ]] && cp -a "$DESTINO_PKI" "$respaldo/halsa-pki"

restaurar() {
    rm -rf "$DESTINO" "$DESTINO_PKI"
    [[ -d "$respaldo/halsa" ]] && cp -a "$respaldo/halsa" "$DESTINO"
    [[ -d "$respaldo/halsa-pki" ]] && cp -a "$respaldo/halsa-pki" "$DESTINO_PKI"
    # Without a previous http.conf, the include line would break every site: take it out.
    if [[ "$habia_config" == false && -f "$HTTP_TOP" ]]; then
        sed -i "\\#^include /data/nginx/custom/halsa/http.conf;\$#d" "$HTTP_TOP"
    fi
}

rm -rf "$DESTINO" "$DESTINO_PKI"
mkdir -p "$(dirname "$DESTINO")"
cp -a "$trabajo/halsa" "$DESTINO"
cp -a "$trabajo/halsa-pki" "$DESTINO_PKI"
chmod -R a+rX "$DESTINO" "$DESTINO_PKI"

if ! linea_presente; then
    info "agregando el include de Halsa a $HTTP_TOP"
    touch "$HTTP_TOP"
    printf '\n# Halsa (SPEC 13): zonas de rate limit de sus proxy hosts. Lo agrega deploy/scripts/sincronizar-npm.sh.\n%s\n' \
        "$LINEA" >> "$HTTP_TOP"
fi

# ── 4. Validación dentro de NPM ───────────────────────────────────────────────
if ! docker exec "$NPM" nginx -t; then
    echo "ERROR: nginx -t falló en NPM; se restaura la configuración anterior" >&2
    restaurar
    docker exec "$NPM" nginx -t >/dev/null 2>&1 \
        || echo "ATENCIÓN: NPM sigue sin pasar nginx -t con la configuración anterior: revisa a mano" >&2
    exit 1
fi

# ── 5. Recarga ────────────────────────────────────────────────────────────────
docker exec "$NPM" nginx -s reload
info "OK: configuración de Halsa sincronizada y NPM recargado"

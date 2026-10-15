#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# importar-realms.sh — crea los realms de Halsa en el Keycloak COMPARTIDO (SPEC 13).
#
# Halsa no despliega ni administra ese Keycloak (auth.edcastdev.com): solo le agrega sus
# realms, una vez. El script:
#   1. completa deploy/keycloak/*.prod.json con envsubst (solo HALSA_HOST_PACIENTES,
#      HALSA_BACKOFFICE_URL y el secreto del BFF), sin escribir el resultado en disco;
#   2. con kcadm.sh dentro del contenedor (docker exec), crea `halsa` y `halsa-pacientes`
#      SOLO si no existen. Nunca pisa ni modifica un realm existente;
#   3. termina con 0 si al final existen los dos.
#
# Pide en la terminal un administrador del realm master de ese Keycloak. La sesión de
# kcadm vive en un archivo temporal dentro del contenedor y se borra al terminar.
#
# Uso (en el VPS, como root):  /opt/halsa/deploy/keycloak/importar-realms.sh
# Lee de deploy/.env: HALSA_KEYCLOAK_CONTENEDOR, HALSA_HOST_PACIENTES, HALSA_BACKOFFICE_URL,
# HALSA_SECRETOS y, opcional, HALSA_KEYCLOAK_URL_INTERNA (por defecto http://localhost:8080,
# la URL de Keycloak vista desde su propio contenedor).
#
# Después: el SMTP de halsa-pacientes (verifyEmail=true) y los clients int-* (runbook 06).
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

DIR="$(cd "$(dirname "$0")/.." && pwd)"
ENV="$DIR/.env"
[[ -f "$ENV" ]] || { echo "falta $ENV" >&2; exit 1; }
set -a
# shellcheck disable=SC1090
source "$ENV"
set +a

CONTENEDOR="${HALSA_KEYCLOAK_CONTENEDOR:?HALSA_KEYCLOAK_CONTENEDOR}"
: "${HALSA_HOST_PACIENTES:?HALSA_HOST_PACIENTES}" "${HALSA_BACKOFFICE_URL:?HALSA_BACKOFFICE_URL}"
SERVIDOR="${HALSA_KEYCLOAK_URL_INTERNA:-http://localhost:8080}"
SECRETO_BFF="${HALSA_SECRETOS:-/etc/halsa/secretos}/kc_bff_secret"
CONFIG="/tmp/kcadm-halsa-$$.config"

[[ -r "$SECRETO_BFF" ]] || { echo "falta el secreto $SECRETO_BFF (runbook 02)" >&2; exit 1; }
command -v envsubst >/dev/null || { echo "falta envsubst (apt install gettext-base)" >&2; exit 1; }
docker inspect "$CONTENEDOR" >/dev/null 2>&1 || { echo "no existe el contenedor $CONTENEDOR" >&2; exit 1; }

kcadm() { docker exec -i "$CONTENEDOR" /opt/keycloak/bin/kcadm.sh "$@" --config "$CONFIG"; }
limpiar() { docker exec "$CONTENEDOR" rm -f "$CONFIG" >/dev/null 2>&1 || true; }
trap limpiar EXIT

read -rp "Usuario administrador del realm master: " usuario
read -rsp "Contraseña: " clave
echo

# kcadm reads KC_CLI_PASSWORD when it has no console. "-e NAME" without a value copies it from this
# process's environment: the password never appears on a command line (ps) nor in the container's definition.
KC_CLI_PASSWORD="$clave" docker exec -i -e KC_CLI_PASSWORD "$CONTENEDOR" /opt/keycloak/bin/kcadm.sh config credentials \
    --server "$SERVIDOR" --realm master --user "$usuario" --config "$CONFIG" >/dev/null
unset clave

existe() { kcadm get "realms/$1" --fields realm >/dev/null 2>&1; }

HALSA_KC_BFF_SECRET="$(tr -d '\r\n' < "$SECRETO_BFF")"
export HALSA_KC_BFF_SECRET HALSA_HOST_PACIENTES HALSA_BACKOFFICE_URL
# Only these three: ${client_id} in the role mapper is Keycloak's own syntax and must stay literal.
# shellcheck disable=SC2016  # envsubst's list of names: they must stay unexpanded
VARIABLES='${HALSA_HOST_PACIENTES} ${HALSA_BACKOFFICE_URL} ${HALSA_KC_BFF_SECRET}'

for par in "halsa:halsa.prod.json" "halsa-pacientes:halsa-pacientes.prod.json"; do
    realm="${par%%:*}"
    archivo="$DIR/keycloak/${par#*:}"

    if existe "$realm"; then
        echo "==> el realm $realm ya existe: no se toca"
        continue
    fi

    echo "==> creando el realm $realm desde $(basename "$archivo")"
    envsubst "$VARIABLES" < "$archivo" | kcadm create realms -f -
done

unset HALSA_KC_BFF_SECRET

for realm in halsa halsa-pacientes; do
    existe "$realm" || { echo "FALLO: el realm $realm no existe al terminar" >&2; exit 1; }
done

echo "==> OK: halsa y halsa-pacientes existen."
echo "    Pendiente: SMTP de halsa-pacientes (Realm settings → Email) y los clients int-* (runbook 06)."

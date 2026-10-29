#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# crear-admin.sh — da de alta el usuario administrador en el realm `halsa` del Keycloak COMPARTIDO.
#
# Los realms de producción no traen usuarios (SPEC 13, ProdRealmsTests), así que el administrador se crea aparte,
# con kcadm dentro del contenedor, igual que importar-realms.sh. Ninguna contraseña queda en un archivo ni en una
# línea de comandos: se pide en la terminal y viaja por stdin.
#
# El usuario queda con:
#   - actor_tipo=arl y tenant_id=$HALSA_ADMIN_TENANT_ID (la API solo conoce un tenant por token: no existe un actor
#     "todas las ARL"; para ver otra ARL hay que crear otro administrador con su tenant_id);
#   - el rol de cliente halsa-api/administrador (casos, incapacidades, plazos, auditoría, webhooks, códigos del portal
#     y solicitudes de HC; sin HC, PCL, emergencia ni integración);
#   - contraseña temporal y, en el primer ingreso, UPDATE_PASSWORD y CONFIGURE_TOTP.
#
# Uso (en el VPS):  HALSA_ADMIN_EMAIL=nombre@dominio HALSA_ADMIN_TENANT_ID=ARL_A ~/halsa/deploy/keycloak/crear-admin.sh
# Lee además de deploy/.env: HALSA_KEYCLOAK_CONTENEDOR y, opcional, HALSA_KEYCLOAK_URL_INTERNA.
# Si el usuario ya existe no hace nada.
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
EMAIL="${HALSA_ADMIN_EMAIL:?HALSA_ADMIN_EMAIL}"
TENANT="${HALSA_ADMIN_TENANT_ID:?HALSA_ADMIN_TENANT_ID}"
SERVIDOR="${HALSA_KEYCLOAK_URL_INTERNA:-http://localhost:8080}"
REALM="halsa"
CONFIG="/tmp/kcadm-halsa-admin-$$.config"

[[ "$TENANT" =~ ^[A-Z0-9_]{2,20}$ ]] || { echo "HALSA_ADMIN_TENANT_ID debe cumplir ^[A-Z0-9_]{2,20}\$" >&2; exit 1; }
[[ "$EMAIL" =~ ^[^@\ \"\\]+@[^@\ \"\\]+$ ]] || { echo "HALSA_ADMIN_EMAIL no parece un correo" >&2; exit 1; }
docker inspect "$CONTENEDOR" >/dev/null 2>&1 || { echo "no existe el contenedor $CONTENEDOR" >&2; exit 1; }

kcadm() { docker exec -i "$CONTENEDOR" /opt/keycloak/bin/kcadm.sh "$@" --config "$CONFIG"; }
limpiar() { docker exec "$CONTENEDOR" rm -f "$CONFIG" >/dev/null 2>&1 || true; }
trap limpiar EXIT

read -rp "Usuario administrador del realm master: " usuario
read -rsp "Contraseña: " clave
echo
KC_CLI_PASSWORD="$clave" docker exec -i -e KC_CLI_PASSWORD "$CONTENEDOR" /opt/keycloak/bin/kcadm.sh config credentials \
    --server "$SERVIDOR" --realm master --user "$usuario" --config "$CONFIG" >/dev/null
unset clave

kcadm get "realms/$REALM" --fields realm >/dev/null 2>&1 || { echo "el realm $REALM no existe (importar-realms.sh)" >&2; exit 1; }

if [[ -n "$(kcadm get users -r "$REALM" -q "username=$EMAIL" -q exact=true --fields id --format csv --noquotes)" ]]; then
    echo "==> $EMAIL ya existe en $REALM: no se toca"
    exit 0
fi

read -rsp "Contraseña temporal para $EMAIL (mínimo 12 caracteres): " temporal
echo
[[ ${#temporal} -ge 12 ]] || { echo "la contraseña temporal debe tener al menos 12 caracteres" >&2; exit 1; }

echo "==> creando $EMAIL (actor_tipo=arl, tenant_id=$TENANT)"
ID="$(kcadm create users -r "$REALM" -i \
    -s "username=$EMAIL" -s "email=$EMAIL" -s enabled=true -s emailVerified=true \
    -s 'attributes.actor_tipo=["arl"]' -s "attributes.tenant_id=[\"$TENANT\"]" \
    -s 'requiredActions=["UPDATE_PASSWORD","CONFIGURE_TOTP"]')"

printf '{"type":"password","value":"%s","temporary":true}' "$temporal" \
    | kcadm update "users/$ID/reset-password" -r "$REALM" -f - -n
unset temporal

kcadm add-roles -r "$REALM" --uusername "$EMAIL" --cclientid halsa-api --rolename administrador

echo "==> OK: $EMAIL creado con el rol administrador. Debe cambiar la contraseña y registrar el OTP al ingresar."

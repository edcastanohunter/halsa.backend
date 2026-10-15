#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# token.sh — pide un token de integración como lo hará el core de una ARL (SPEC 13):
# client_credentials con private_key_jwt (ES256, firmado con la llave del certificado),
# por https://integraciones.localhost con mTLS. Keycloak liga el token al certificado
# (cnf.x5t#S256). Imprime solo el access_token.
#
# Uso:  ./docker/nginx-dev/token.sh int-arl-ARL_A
#       ./docker/nginx-dev/token.sh int-ips-110011234501
#
# Requiere openssl, curl y perl. La PKI de desarrollo debe existir (generar-pki.sh).
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

AQUI="$(cd "$(dirname "$0")" && pwd)"
PKI="$AQUI/pki"
client_id="${1:?uso: token.sh <client_id>}"
llave="$PKI/clientes/$client_id.key"
certificado="$PKI/clientes/$client_id.crt"
# KC_HOSTNAME of docker-compose.yml: the issuer, and the audience Keycloak accepts in the assertion.
audiencia="${HALSA_EMISOR:-http://localhost:8080/realms/halsa}"
token_endpoint="${HALSA_TOKEN_ENDPOINT:-https://integraciones.localhost/auth/realms/halsa/protocol/openid-connect/token}"

[[ -f "$llave" && -f "$certificado" ]] || { echo "No existe la llave o el certificado de $client_id en $PKI/clientes" >&2; exit 1; }

b64url() { openssl base64 -A | tr '+/' '-_' | tr -d '='; }
hex_a_binario() { perl -pe 's/([0-9a-fA-F]{2})/chr hex $1/ge'; }

ahora=$(date +%s)
encabezado=$(printf '{"alg":"ES256","typ":"JWT"}' | b64url)
cuerpo=$(printf '{"iss":"%s","sub":"%s","aud":"%s","jti":"%s","iat":%d,"exp":%d}' \
    "$client_id" "$client_id" "$audiencia" "$(openssl rand -hex 16)" "$ahora" "$((ahora + 60))" | b64url)
firmado="$encabezado.$cuerpo"

# openssl signs ECDSA as DER (SEQUENCE { r, s }); a JWS ES256 signature is r || s, 32 bytes each.
r_s=$(printf '%s' "$firmado" | openssl dgst -sha256 -sign "$llave" | openssl asn1parse -inform DER \
    | awk -F: '/INTEGER/ { print $NF }')
r=$(echo "$r_s" | sed -n 1p)
s=$(echo "$r_s" | sed -n 2p)
relleno() { local v="${1#00}"; while [[ ${#v} -lt 64 ]]; do v="0$v"; done; echo "${v: -64}"; }
firma=$(printf '%s%s' "$(relleno "$r")" "$(relleno "$s")" | hex_a_binario | b64url)

respuesta=$(curl -sS --fail-with-body \
    --cacert "$PKI/raiz/raiz.crt" --cert "$certificado" --key "$llave" \
    -d grant_type=client_credentials \
    -d client_id="$client_id" \
    -d client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer \
    -d client_assertion="$firmado.$firma" \
    "$token_endpoint")

printf '%s' "$respuesta" | sed -E 's/.*"access_token":"([^"]+)".*/\1/'

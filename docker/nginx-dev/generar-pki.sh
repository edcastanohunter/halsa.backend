#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# generar-pki.sh — la PKI de DESARROLLO (SPEC 13), para probar mTLS de punta a punta
# con docker-compose.yml. Usa los mismos scripts que producción (deploy/pki), con
# HALSA_PKI_DEV=1 (sin passphrase) y todo en docker/nginx-dev/pki (ignorado por git).
#
# Genera:
#   pki/raiz, pki/intermedia            la CA de integraciones de desarrollo
#   pki/publicar/                       la cadena y las CRL que monta Nginx
#   pki/clientes/int-arl-ARL_A.*        certificado + llave del client int-arl-ARL_A
#   pki/clientes/int-ips-110011234501.* certificado + llave del client int-ips-110011234501
#   pki/servidor/                       el certificado TLS de *.localhost (firmado por la raíz de dev)
#   pki/keycloak.env                    los certificados de los clients para el realm de desarrollo
#
# Uso:  ./docker/nginx-dev/generar-pki.sh            (no hace nada si ya existe)
#       ./docker/nginx-dev/generar-pki.sh --forzar   (borra y regenera; hay que reimportar el realm)
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail
export MSYS_NO_PATHCONV=1

AQUI="$(cd "$(dirname "$0")" && pwd)"
# Git Bash: the native openssl wants "C:/..." paths.
command -v cygpath >/dev/null 2>&1 && AQUI="$(cygpath -m "$AQUI")"
PKI_SCRIPTS="$AQUI/../../deploy/pki"
export HALSA_PKI_DIR="$AQUI/pki"
export HALSA_PKI_DEV=1

CLIENTES=(int-arl-ARL_A int-ips-110011234501)
HOSTS=(api.localhost integraciones.localhost auth.localhost pacientes.localhost)

if [[ -f "$HALSA_PKI_DIR/raiz/raiz.crt" && "${1:-}" != "--forzar" ]]; then
    echo "La PKI de desarrollo ya existe en $HALSA_PKI_DIR (usa --forzar para regenerarla)."
    exit 0
fi

rm -rf "$HALSA_PKI_DIR"
mkdir -p "$HALSA_PKI_DIR"

bash "$PKI_SCRIPTS/crear-raiz.sh"
bash "$PKI_SCRIPTS/crear-intermedia.sh" solicitud
bash "$PKI_SCRIPTS/crear-intermedia.sh" firmar "$HALSA_PKI_DIR/intermedia/intermedia.csr"
bash "$PKI_SCRIPTS/crear-intermedia.sh" instalar \
    "$HALSA_PKI_DIR/intermedia/intermedia.crt" "$HALSA_PKI_DIR/raiz/raiz.crt" "$HALSA_PKI_DIR/raiz/raiz.crl"

for cliente in "${CLIENTES[@]}"; do
    bash "$PKI_SCRIPTS/emitir-cliente.sh" "$cliente"
done

# ── TLS server certificate for the four *.localhost hosts ────────────────────
# In production it is Let's Encrypt's; here the dev root signs it, so curl needs
# --cacert docker/nginx-dev/pki/raiz/raiz.crt.
servidor="$HALSA_PKI_DIR/servidor"
mkdir -p "$servidor"
san="$(printf 'DNS:%s,' "${HOSTS[@]}")DNS:localhost"

openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$servidor/servidor.key"
openssl req -new -key "$servidor/servidor.key" -subj "/O=Halsa/CN=halsa.localhost" -out "$servidor/servidor.csr"
printf 'basicConstraints=critical,CA:false\nkeyUsage=critical,digitalSignature\nextendedKeyUsage=serverAuth\nsubjectAltName=%s\n' "$san" \
    > "$servidor/ext.cnf"
openssl x509 -req -in "$servidor/servidor.csr" -CA "$HALSA_PKI_DIR/raiz/raiz.crt" -CAkey "$HALSA_PKI_DIR/raiz/raiz.key" \
    -CAcreateserial -days 825 -sha256 -extfile "$servidor/ext.cnf" -out "$servidor/servidor.crt"
cat "$servidor/servidor.crt" > "$servidor/fullchain.pem"

# ── The clients' certificates for the dev realm (placeholders in halsa-realm.json) ──
source "$PKI_SCRIPTS/_comun.sh"
{
    echo "# Generado por docker/nginx-dev/generar-pki.sh. Lo lee Keycloak al importar el realm halsa."
    echo "HALSA_DEV_CERT_INT_ARL_ARL_A=$(certificado_base64 "$HALSA_PKI_DIR/clientes/int-arl-ARL_A.crt")"
    echo "HALSA_DEV_CERT_INT_IPS_110011234501=$(certificado_base64 "$HALSA_PKI_DIR/clientes/int-ips-110011234501.crt")"
} > "$HALSA_PKI_DIR/keycloak.env"

echo
echo "PKI de desarrollo lista en $HALSA_PKI_DIR."
echo "Si el realm halsa ya estaba importado, recrea el volumen de Keycloak para que tome los certificados:"
echo "  docker compose rm -sf halsa.backend.identity && docker volume rm halsabackend_halsa.backend-identity"

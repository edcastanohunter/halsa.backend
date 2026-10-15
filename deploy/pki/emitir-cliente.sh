#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# emitir-cliente.sh — certificado cliente de una integración (SPEC 13).
#
#   ECDSA P-256, 1 año, CN = client_id de Keycloak (p. ej. int-arl-ARL_A),
#   extendedKeyUsage = clientAuth. Firmado por la intermedia.
#
# Uso:
#   ./emitir-cliente.sh <client_id> [solicitud.csr]
#
# Lo recomendado es que la integración genere su llave y entregue un CSR: así la llave
# nunca sale de su lado. Sin CSR, el script genera la llave aquí (clientes/<client_id>.key)
# y hay que entregarla por un canal seguro (runbook 06).
#
# El CN del certificado es siempre <client_id>, diga lo que diga el CSR.
#
# Al final imprime lo que necesita Keycloak (realm halsa, client <client_id>):
#   - Credentials → "Signed JWT" → certificado (jwt.credential.certificate), base64 DER;
#   - la huella x5t#S256 que llevarán los tokens en cnf.
# ─────────────────────────────────────────────────────────────────────────────
source "$(dirname "$0")/_comun.sh"

client_id="${1:-}"
csr="${2:-}"

[[ "$client_id" =~ ^int-(arl|ips)-[A-Za-z0-9_]+$ ]] || die "uso: emitir-cliente.sh <int-arl-XXX|int-ips-NNN> [solicitud.csr]"
[[ -f "$PKI_DIR/intermedia/intermedia.key" ]] || die "no hay intermedia en $PKI_DIR/intermedia"

preparar_ca intermedia
dir="$PKI_DIR/intermedia"
clientes="$PKI_DIR/clientes"
mkdir -p "$clientes"

if [[ -z "$csr" ]]; then
    info "generando la llave ECDSA P-256 de $client_id (entrégala por un canal seguro)"
    openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out "$clientes/$client_id.key"
    chmod 600 "$clientes/$client_id.key"
    openssl req -new -config "$dir/openssl.cnf" -key "$clientes/$client_id.key" \
        -subj "/O=$ORGANIZACION/CN=$client_id" -out "$clientes/$client_id.csr"
    csr="$clientes/$client_id.csr"
else
    [[ -f "$csr" ]] || die "no existe $csr"
    openssl req -in "$csr" -noout -verify >/dev/null
    curva="$(openssl req -in "$csr" -noout -text | grep -E 'ASN1 OID|NIST CURVE' | head -1 || true)"
    [[ "$curva" == *prime256v1* || "$curva" == *P-256* ]] \
        || die "el CSR debe ser ECDSA P-256 (prime256v1); trae: ${curva:-otro algoritmo}"
fi

info "firmando $client_id con la intermedia ($CLIENTE_DIAS días)"
openssl ca -batch -config "$dir/openssl.cnf" -extensions ext_cliente \
    -subj "/O=$ORGANIZACION/CN=$client_id" -days "$CLIENTE_DIAS" -notext \
    -in "$csr" -out "$clientes/$client_id.crt"

cat "$PKI_DIR/intermedia/intermedia.crt" "$PKI_DIR/raiz/raiz.crt" > "$clientes/$client_id.cadena.pem"
openssl verify -CAfile "$clientes/$client_id.cadena.pem" "$clientes/$client_id.crt"

echo
openssl x509 -in "$clientes/$client_id.crt" -noout -subject -serial -enddate
echo "x5t#S256:                   $(huella_x5t_s256 "$clientes/$client_id.crt")"
echo "jwt.credential.certificate: $(certificado_base64 "$clientes/$client_id.crt")"
echo
info "registra el certificado en Keycloak (client $client_id → Credentials → Signed JWT) y entrega"
info "$client_id.crt y $client_id.cadena.pem a la integración."

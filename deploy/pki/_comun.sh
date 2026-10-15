#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# _comun.sh — lo que comparten los scripts de la CA de integraciones (SPEC 13).
# No se ejecuta solo: lo cargan crear-raiz.sh, crear-intermedia.sh, emitir-cliente.sh,
# revocar.sh y generar-crl.sh.
#
# Variables:
#   HALSA_PKI_DIR   raíz de la PKI (por defecto ./halsa-pki). En el equipo sin red vive la
#                   raíz; en el VPS, /etc/halsa/pki (solo la intermedia y lo público).
#   HALSA_PKI_DEV   1 = la llave de la raíz sin passphrase. Solo para docker/nginx-dev.
#
# Estructura:
#   raiz/         raiz.key (cifrada), raiz.crt, raiz.crl, index.txt, serial, crlnumber
#   intermedia/   intermedia.key, intermedia.csr, intermedia.crt, intermedia.crl, index.txt, ...
#   clientes/     <client_id>.key (si se generó aquí), .csr, .crt
#   publicar/     halsa-integraciones-ca.pem  (intermedia + raíz)        → ssl_client_certificate
#                 halsa-integraciones.crl     (CRL intermedia + CRL raíz) → ssl_crl
#
# Nginx activa X509_V_FLAG_CRL_CHECK_ALL con ssl_crl: necesita una CRL vigente de CADA CA de
# la cadena. Por eso halsa-integraciones.crl lleva las dos, y una CRL vencida cierra todo.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# Git Bash on Windows rewrites "/CN=..." into a Windows path without this.
export MSYS_NO_PATHCONV=1

PKI_DIR="${HALSA_PKI_DIR:-./halsa-pki}"
# ...and its openssl (native) cannot open "/c/..." paths: hand it "C:/..." instead.
if command -v cygpath >/dev/null 2>&1; then
    PKI_DIR="$(cygpath -m "$PKI_DIR")"
fi
PKI_DEV="${HALSA_PKI_DEV:-0}"

# shellcheck disable=SC2034  # sourced: the CA scripts use these values
RAIZ_DIAS=3650          # 10 años
RAIZ_CRL_DIAS=365       # se reemite en la ceremonia anual (runbook 06)
INTERMEDIA_DIAS=1095    # 3 años
INTERMEDIA_CRL_DIAS=10  # se regenera cada 7 días (cron) y con cada revocación: 3 días de margen
CLIENTE_DIAS=365        # 1 año

# shellcheck disable=SC2034  # sourced: the CA scripts use these values
{
RAIZ_CN="Halsa Raiz Integraciones"
INTERMEDIA_CN="Halsa Integraciones"
ORGANIZACION="Halsa"
}

die() { echo "ERROR: $*" >&2; exit 1; }
info() { echo "==> $*"; }

command -v openssl >/dev/null 2>&1 || die "falta openssl en el PATH"

# Cipher for the root key: none in dev, AES-256 with a prompted passphrase otherwise.
cifrado_raiz() {
    if [[ "$PKI_DEV" == "1" ]]; then echo ""; else echo "-aes256"; fi
}

# Creates the openssl ca database and config of a CA ($1 = raiz | intermedia) if missing.
preparar_ca() {
    local ca="$1"
    local dir="$PKI_DIR/$1"
    local dias crl_dias

    mkdir -p "$dir/emitidos"
    chmod 700 "$dir"
    [[ -f "$dir/index.txt" ]] || : > "$dir/index.txt"
    [[ -f "$dir/index.txt.attr" ]] || echo "unique_subject = no" > "$dir/index.txt.attr"
    [[ -f "$dir/serial" ]] || openssl rand -hex 16 > "$dir/serial"
    [[ -f "$dir/crlnumber" ]] || echo 1000 > "$dir/crlnumber"

    if [[ "$ca" == "raiz" ]]; then
        dias=$INTERMEDIA_DIAS
        crl_dias=$RAIZ_CRL_DIAS
    else
        dias=$CLIENTE_DIAS
        crl_dias=$INTERMEDIA_CRL_DIAS
    fi

    {
        echo "[ ca ]"
        echo "default_ca = ca_halsa"
        echo
        echo "[ ca_halsa ]"
        echo "dir               = $dir"
        echo "database          = \$dir/index.txt"
        echo "serial            = \$dir/serial"
        echo "crlnumber         = \$dir/crlnumber"
        echo "new_certs_dir     = \$dir/emitidos"
        echo "certificate       = \$dir/$ca.crt"
        echo "private_key       = \$dir/$ca.key"
        echo "default_md        = sha384"
        echo "default_days      = $dias"
        echo "default_crl_days  = $crl_dias"
        echo "preserve          = no"
        echo "unique_subject    = no"
        echo "copy_extensions   = none"
        echo "policy            = politica_cn"
        echo "crl_extensions    = ext_crl"
        echo
        echo "[ politica_cn ]"
        echo "organizationName  = optional"
        echo "commonName        = supplied"
        echo
        echo "[ ext_crl ]"
        echo "authorityKeyIdentifier = keyid:always"
        echo
        echo "[ req ]"
        echo "distinguished_name = dn"
        echo "prompt             = no"
        echo
        echo "[ dn ]"
        echo "CN = halsa"
        echo
        echo "[ ext_raiz ]"
        echo "basicConstraints       = critical, CA:true"
        echo "keyUsage               = critical, keyCertSign, cRLSign"
        echo "subjectKeyIdentifier   = hash"
        echo
        echo "[ ext_intermedia ]"
        echo "basicConstraints       = critical, CA:true, pathlen:0"
        echo "keyUsage               = critical, keyCertSign, cRLSign"
        echo "subjectKeyIdentifier   = hash"
        echo "authorityKeyIdentifier = keyid:always"
        echo
        echo "[ ext_cliente ]"
        echo "basicConstraints       = critical, CA:false"
        echo "keyUsage               = critical, digitalSignature"
        echo "extendedKeyUsage       = clientAuth"
        echo "subjectKeyIdentifier   = hash"
        echo "authorityKeyIdentifier = keyid:always"
    } > "$dir/openssl.cnf"
}

# x5t#S256 of a certificate: base64url(SHA-256(DER)), what Keycloak puts in cnf (RFC 8705).
huella_x5t_s256() {
    openssl x509 -in "$1" -outform DER | openssl dgst -sha256 -binary | openssl base64 -A | tr '+/' '-_' | tr -d '='
}

# The certificate as single-line base64 DER: Keycloak's "jwt.credential.certificate".
certificado_base64() {
    openssl x509 -in "$1" -outform DER | openssl base64 -A
}

# Concatenates what Nginx mounts: the chain and both CRLs.
publicar() {
    [[ -f "$PKI_DIR/intermedia/intermedia.crt" ]] || die "no hay intermedia instalada en $PKI_DIR/intermedia"
    [[ -f "$PKI_DIR/raiz/raiz.crt" ]] || die "falta raiz/raiz.crt (el certificado público de la raíz)"
    [[ -f "$PKI_DIR/raiz/raiz.crl" ]] || die "falta raiz/raiz.crl (la CRL de la raíz, emitida en el equipo sin red)"
    [[ -f "$PKI_DIR/intermedia/intermedia.crl" ]] || die "falta intermedia/intermedia.crl: ejecuta generar-crl.sh"

    mkdir -p "$PKI_DIR/publicar"
    cat "$PKI_DIR/intermedia/intermedia.crt" "$PKI_DIR/raiz/raiz.crt" > "$PKI_DIR/publicar/halsa-integraciones-ca.pem"
    cat "$PKI_DIR/intermedia/intermedia.crl" "$PKI_DIR/raiz/raiz.crl" > "$PKI_DIR/publicar/halsa-integraciones.crl"
    chmod 644 "$PKI_DIR/publicar/"*
    info "publicado en $PKI_DIR/publicar (cadena y CRL para Nginx)"
}

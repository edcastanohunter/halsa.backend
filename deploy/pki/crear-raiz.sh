#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# crear-raiz.sh — la raíz de la CA privada de integraciones (SPEC 13).
#
# SE EJECUTA EN UN EQUIPO SIN RED. La llave de la raíz nunca toca el VPS: se custodia
# fuera de línea en dos ubicaciones independientes (runbook 02). Al VPS solo viajan
# raiz.crt y raiz.crl.
#
#   RSA 4096, 10 años, llave cifrada con AES-256 (passphrase interactiva).
#   Emite también la primera CRL de la raíz (365 días; se reemite en la ceremonia anual).
#
# Uso:  HALSA_PKI_DIR=/media/boveda/halsa-pki ./crear-raiz.sh
# ─────────────────────────────────────────────────────────────────────────────
source "$(dirname "$0")/_comun.sh"

[[ -f "$PKI_DIR/raiz/raiz.key" ]] && die "ya existe una raíz en $PKI_DIR/raiz: no se sobrescribe"

preparar_ca raiz
dir="$PKI_DIR/raiz"

info "generando la llave RSA 4096 de la raíz"
# shellcheck disable=SC2046
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 $(cifrado_raiz) -out "$dir/raiz.key"
chmod 400 "$dir/raiz.key"

info "autofirmando el certificado de la raíz ($RAIZ_DIAS días)"
openssl req -new -x509 -config "$dir/openssl.cnf" -key "$dir/raiz.key" \
    -subj "/O=$ORGANIZACION/CN=$RAIZ_CN" -days "$RAIZ_DIAS" -sha384 \
    -extensions ext_raiz -out "$dir/raiz.crt"

info "emitiendo la CRL de la raíz ($RAIZ_CRL_DIAS días)"
openssl ca -batch -config "$dir/openssl.cnf" -gencrl -out "$dir/raiz.crl"

openssl x509 -in "$dir/raiz.crt" -noout -subject -enddate -fingerprint -sha256
info "listo. Copia al VPS SOLO raiz/raiz.crt y raiz/raiz.crl."

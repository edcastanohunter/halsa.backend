#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# crear-intermedia.sh — la intermedia "Halsa Integraciones" (SPEC 13): ECDSA P-384, 3 años.
#
# Tres pasos en dos equipos, para que la llave de la raíz no salga del equipo sin red
# y la de la intermedia no salga del VPS:
#
#   1. En el VPS:            ./crear-intermedia.sh solicitud
#                            → intermedia/intermedia.key (0600) e intermedia/intermedia.csr
#   2. En el equipo sin red: ./crear-intermedia.sh firmar intermedia.csr
#                            → intermedia.crt firmada por la raíz
#   3. En el VPS:            ./crear-intermedia.sh instalar intermedia.crt raiz.crt raiz.crl
#                            → instala, genera la CRL de la intermedia y publica para Nginx
#
# La llave de la intermedia queda sin passphrase: el cron semanal de la CRL la usa sin
# intervención. Vive en /etc/halsa/pki con root:root 0600 y fuera de todo backup.
# ─────────────────────────────────────────────────────────────────────────────
# shellcheck source=_comun.sh
source "$(dirname "$0")/_comun.sh"

paso="${1:-}"

case "$paso" in
  solicitud)
    [[ -f "$PKI_DIR/intermedia/intermedia.key" ]] && die "ya existe intermedia/intermedia.key: no se sobrescribe"
    preparar_ca intermedia
    dir="$PKI_DIR/intermedia"

    info "generando la llave ECDSA P-384 de la intermedia"
    openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-384 -out "$dir/intermedia.key"
    chmod 600 "$dir/intermedia.key"
    openssl req -new -config "$dir/openssl.cnf" -key "$dir/intermedia.key" \
        -subj "/O=$ORGANIZACION/CN=$INTERMEDIA_CN" -out "$dir/intermedia.csr"

    info "lleva $dir/intermedia.csr al equipo sin red y ejecuta: crear-intermedia.sh firmar intermedia.csr"
    ;;

  firmar)
    csr="${2:-}"
    [[ -f "$csr" ]] || die "uso: crear-intermedia.sh firmar <intermedia.csr>"
    [[ -f "$PKI_DIR/raiz/raiz.key" ]] || die "no hay raíz en $PKI_DIR/raiz: esto se ejecuta en el equipo sin red"
    preparar_ca raiz
    dir="$PKI_DIR/raiz"
    salida="$(dirname "$csr")/intermedia.crt"

    info "firmando la intermedia con la raíz ($INTERMEDIA_DIAS días)"
    openssl ca -batch -config "$dir/openssl.cnf" -extensions ext_intermedia \
        -subj "/O=$ORGANIZACION/CN=$INTERMEDIA_CN" -days "$INTERMEDIA_DIAS" -notext \
        -in "$csr" -out "$salida"
    openssl verify -CAfile "$dir/raiz.crt" "$salida"

    info "lleva $salida, raiz/raiz.crt y raiz/raiz.crl al VPS y ejecuta: crear-intermedia.sh instalar ..."
    ;;

  instalar)
    crt="${2:-}"
    raiz_crt="${3:-}"
    raiz_crl="${4:-}"
    [[ -f "$crt" && -f "$raiz_crt" && -f "$raiz_crl" ]] \
        || die "uso: crear-intermedia.sh instalar <intermedia.crt> <raiz.crt> <raiz.crl>"
    preparar_ca intermedia
    dir="$PKI_DIR/intermedia"

    openssl verify -CAfile "$raiz_crt" "$crt"

    # The certificate must belong to the key generated in step 1.
    [[ "$(openssl x509 -in "$crt" -noout -pubkey)" == "$(openssl pkey -in "$dir/intermedia.key" -pubout)" ]] \
        || die "intermedia.crt no corresponde a intermedia/intermedia.key"

    mkdir -p "$PKI_DIR/raiz"
    copiar() { [[ "$(realpath "$1")" == "$(realpath -m "$2")" ]] || cp "$1" "$2"; }
    copiar "$crt" "$dir/intermedia.crt"
    copiar "$raiz_crt" "$PKI_DIR/raiz/raiz.crt"
    copiar "$raiz_crl" "$PKI_DIR/raiz/raiz.crl"

    bash "$(dirname "$0")/generar-crl.sh"
    openssl x509 -in "$dir/intermedia.crt" -noout -subject -issuer -enddate
    ;;

  *)
    die "uso: crear-intermedia.sh solicitud | firmar <csr> | instalar <crt> <raiz.crt> <raiz.crl>"
    ;;
esac

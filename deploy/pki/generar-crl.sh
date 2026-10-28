#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# generar-crl.sh — regenera la CRL de la intermedia y publica cadena + CRLs para Nginx (SPEC 13).
#
# En el VPS lo ejecuta un cron cada 7 días (la CRL vale 10) y revocar.sh tras cada revocación.
# Después hay que llevarla a Nginx Proxy Manager con sincronizar-npm.sh (crontab del dueño de los Quadlets,
# runbook 06):
#   0 4 * * 1  HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/generar-crl.sh && bash ~/halsa/deploy/scripts/sincronizar-npm.sh
#
# Si la CRL vence, NPM rechaza TODOS los certificados (falla cerrado): Seq alerta 48 h antes
# (runbook 07).
#
#   --raiz   en el equipo sin red: reemite la CRL de la raíz (ceremonia anual o tras revocar
#            la intermedia). Su resultado, raiz/raiz.crl, se lleva al VPS.
# ─────────────────────────────────────────────────────────────────────────────
# shellcheck source=_comun.sh
source "$(dirname "$0")/_comun.sh"

if [[ "${1:-}" == "--raiz" ]]; then
    [[ -f "$PKI_DIR/raiz/raiz.key" ]] || die "no hay raíz en $PKI_DIR/raiz: esto se ejecuta en el equipo sin red"
    preparar_ca raiz
    openssl ca -batch -config "$PKI_DIR/raiz/openssl.cnf" -gencrl -out "$PKI_DIR/raiz/raiz.crl"
    openssl crl -in "$PKI_DIR/raiz/raiz.crl" -noout -nextupdate
    info "lleva raiz/raiz.crl al VPS y ejecuta generar-crl.sh allí"
    exit 0
fi

[[ -f "$PKI_DIR/intermedia/intermedia.key" ]] || die "no hay intermedia en $PKI_DIR/intermedia"
preparar_ca intermedia

openssl ca -batch -config "$PKI_DIR/intermedia/openssl.cnf" -gencrl -out "$PKI_DIR/intermedia/intermedia.crl"
echo "CRL intermedia: $(openssl crl -in "$PKI_DIR/intermedia/intermedia.crl" -noout -nextupdate)"
echo "CRL raíz:       $(openssl crl -in "$PKI_DIR/raiz/raiz.crl" -noout -nextupdate 2>/dev/null || echo 'FALTA')"

publicar

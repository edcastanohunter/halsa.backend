#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# revocar.sh — revoca un certificado cliente y regenera la CRL (SPEC 13).
#
# Uso:  ./revocar.sh <client_id | ruta.crt> [motivo]
#   motivo: unspecified | keyCompromise | affiliationChanged | superseded | cessationOfOperation
#           (por defecto, keyCompromise)
#
# Después hay que llevar la CRL nueva a Nginx Proxy Manager (runbook 06):
#   bash ~/halsa/deploy/scripts/sincronizar-npm.sh
#
# Revocar la intermedia NO se hace aquí: se revoca desde la raíz, en el equipo sin red
# (openssl ca -revoke con raiz/openssl.cnf y generar-crl.sh --raiz), runbook 06.
# ─────────────────────────────────────────────────────────────────────────────
# shellcheck source=_comun.sh
source "$(dirname "$0")/_comun.sh"

objetivo="${1:-}"
motivo="${2:-keyCompromise}"

[[ -n "$objetivo" ]] || die "uso: revocar.sh <client_id | ruta.crt> [motivo]"

if [[ -f "$objetivo" ]]; then
    crt="$objetivo"
else
    crt="$PKI_DIR/clientes/$objetivo.crt"
fi
[[ -f "$crt" ]] || die "no existe $crt"

preparar_ca intermedia
info "revocando $(openssl x509 -in "$crt" -noout -subject -serial | tr '\n' ' ') ($motivo)"
openssl ca -batch -config "$PKI_DIR/intermedia/openssl.cnf" -revoke "$crt" -crl_reason "$motivo"

bash "$(dirname "$0")/generar-crl.sh"
info "ejecuta deploy/scripts/sincronizar-npm.sh para llevar la CRL a NPM (en desarrollo: reinicia halsa.backend.nginx)"

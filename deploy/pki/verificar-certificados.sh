#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# verificar-certificados.sh — valida la PKI de integraciones de HALSA_PKI_DIR (SPEC 13, runbook 06).
#
# Solo lee: no firma, no regenera y no toca NPM. Sirve en el equipo sin red (la raíz), en el VPS
# (~/.config/halsa/pki) y en desarrollo. Revisa lo que exista de:
#   raiz/        el certificado (CA autofirmada, vigencia), su CRL (firma, nextUpdate) y que la llave esté cifrada
#   intermedia/  la cadena hasta la raíz, que no esté revocada, llave ↔ certificado, pathlen:0, vigencia y su CRL
#   publicar/    que la cadena y la CRL publicadas sean exactamente intermedia + raíz (lo que monta NPM)
#   clientes/    cada certificado contra la cadena y las dos CRL (-crl_check_all), clientAuth, CN y vigencia
#
# Uso:
#   HALSA_PKI_DIR=~/.config/halsa/pki ./verificar-certificados.sh [certificado.crt ...]
# Los certificados sueltos (p. ej. la intermedia recién firmada, en la memoria USB) se verifican contra la
# raíz si son una CA, o contra la cadena completa si son de un cliente.
#
# Avisos, que no fallan: la raíz vence en menos de 365 días o la intermedia en menos de 90 (runbook 05), un
# cliente en menos de 30, la CRL de la intermedia en menos de 48 h (la alerta CrlPorVencer) y la de la raíz en
# menos de 30 días (ceremonia anual).
#
# Exit: 0 sin fallos (puede haber avisos); 1 con al menos un FALLO.
# ─────────────────────────────────────────────────────────────────────────────
# shellcheck source=_comun.sh
source "$(dirname "$0")/_comun.sh"

RAIZ="$PKI_DIR/raiz"
INTER="$PKI_DIR/intermedia"
PUBLICAR="$PKI_DIR/publicar"

fallos=0
avisos=0
ok() { echo "  OK     $*"; }
aviso() { echo "  AVISO  $*"; avisos=$((avisos + 1)); }
fallo() { echo "  FALLO  $*"; fallos=$((fallos + 1)); }

ahora="$(date -u +%s)"

# Seconds from now until an OpenSSL date ("Oct 27 20:03:35 2027 GMT").
segundos_hasta() { echo $(( $(date -u -d "$1" +%s) - ahora )); }

en_linux() { [[ "$(uname -s)" == Linux ]]; }

# An editor saved the file: OpenSSL 3 still reads it, but it is no longer the byte-exact copy from custody.
sin_bom() {
    if [[ "$(head -c 3 "$1" | od -An -tx1 | tr -d ' \n')" == "efbbbf" ]]; then
        aviso "$(basename "$1") empieza con un BOM UTF-8: lo guardó un editor; vuelve a copiarlo de la custodia"
    fi
}

# $1 file, $2 the octal modes accepted (e.g. "400 600").
permisos() {
    en_linux || return 0
    local modo
    modo="$(stat -c '%a' "$1")"
    [[ " $2 " == *" $modo "* ]] || fallo "$(basename "$1") tiene permisos $modo (se esperan: $2)"
}

# $1 certificate, $2 label, $3 warning threshold in days.
vigencia() {
    local fin restante
    fin="$(openssl x509 -in "$1" -noout -enddate | cut -d= -f2)"
    restante="$(segundos_hasta "$fin")"
    if (( restante <= 0 )); then
        fallo "$2: vencido desde $fin"
    elif (( restante < $3 * 86400 )); then
        aviso "$2: vence en $(( restante / 86400 )) días ($fin)"
    else
        ok "$2: vigente $(( restante / 86400 )) días más ($fin)"
    fi
}

# $1 CRL, $2 issuer certificate, $3 label, $4 warning threshold in hours.
crl() {
    local siguiente restante revocados
    if [[ ! -f "$1" ]]; then
        fallo "$3: no existe ($1)"
        return
    fi
    sin_bom "$1"
    if ! openssl crl -in "$1" -noout -CAfile "$2" >/dev/null 2>&1; then
        fallo "$3: no la firmó $(openssl x509 -in "$2" -noout -subject)"
        return
    fi
    siguiente="$(openssl crl -in "$1" -noout -nextupdate | cut -d= -f2)"
    restante="$(segundos_hasta "$siguiente")"
    revocados="$(openssl crl -in "$1" -noout -text | grep -c 'Serial Number:' || true)"
    if (( restante <= 0 )); then
        fallo "$3: vencida desde $siguiente: NPM rechaza TODOS los certificados (runbook 06)"
    elif (( restante < $4 * 3600 )); then
        aviso "$3: nextUpdate en $(( restante / 3600 )) h ($siguiente); $revocados revocado(s)"
    else
        ok "$3: firma válida, nextUpdate en $(( restante / 3600 )) h ($siguiente); $revocados revocado(s)"
    fi
}

es_ca() { openssl x509 -in "$1" -noout -ext basicConstraints 2>/dev/null | grep -q 'CA:TRUE'; }

# The cause in `openssl verify`'s output ("error 8 at 0 depth lookup: CRL signature failure"), not its last line.
motivo() { grep -m1 'error [0-9]' <<< "$1" || tail -1 <<< "$1"; }

# An intermediate CA ($1): signed by the root, not revoked by it, pathlen:0, still valid.
verificar_intermedia() {
    local salida
    sin_bom "$1"
    [[ -f "$RAIZ/raiz.crt" ]] || { fallo "$(basename "$1"): sin raiz/raiz.crt no se puede verificar"; return; }
    if [[ -f "$RAIZ/raiz.crl" ]]; then
        salida="$(openssl verify -crl_check -CAfile "$RAIZ/raiz.crt" -CRLfile "$RAIZ/raiz.crl" "$1" 2>&1)" || true
    else
        salida="$(openssl verify -CAfile "$RAIZ/raiz.crt" "$1" 2>&1)" || true
    fi
    case "$salida" in
        *": OK") ok "$(basename "$1"): firmada por la raíz y no revocada" ;;
        *"certificate revoked"*) fallo "$(basename "$1"): REVOCADA por la raíz (runbook 06, 'Compromiso o pérdida de la intermedia')" ;;
        *) fallo "$(basename "$1"): no verifica contra la raíz: $(motivo "$salida")" ;;
    esac
    if openssl x509 -in "$1" -noout -ext basicConstraints | grep -q 'pathlen:0'; then
        ok "$(basename "$1"): CA:TRUE, pathlen:0"
    else
        fallo "$(basename "$1"): sin pathlen:0 (podría firmar otras CA)"
    fi
    vigencia "$1" "$(basename "$1")" 90
}

# A client certificate ($1): chain + both CRLs, clientAuth, CN = file name (under clientes/), still valid.
verificar_cliente() {
    local nombre cn salida
    nombre="$(basename "$1" .crt)"
    sin_bom "$1"
    if [[ ! -f "$INTER/intermedia.crt" || ! -f "$RAIZ/raiz.crt" ]]; then
        fallo "$nombre: sin intermedia/intermedia.crt y raiz/raiz.crt no se puede verificar"
        return
    fi
    salida="$(openssl verify -crl_check_all -CAfile "$RAIZ/raiz.crt" -untrusted "$INTER/intermedia.crt" \
        -CRLfile "$INTER/intermedia.crl" -CRLfile "$RAIZ/raiz.crl" "$1" 2>&1)" || true
    case "$salida" in
        *": OK") ok "$nombre: cadena y CRL válidas" ;;
        *"certificate revoked"*) ok "$nombre: revocado (está en la CRL): NPM lo rechaza con 400"; return ;;
        *) fallo "$nombre: no verifica: $(motivo "$salida")"; return ;;
    esac
    openssl x509 -in "$1" -noout -ext extendedKeyUsage | grep -q 'TLS Web Client Authentication' \
        || fallo "$nombre: sin extendedKeyUsage clientAuth"
    cn="$(openssl x509 -in "$1" -noout -subject -nameopt RFC2253 | sed -n 's/.*CN=\([^,]*\).*/\1/p')"
    if [[ "$(dirname "$1")" == "$PKI_DIR/clientes" && "$cn" != "$nombre" ]]; then
        fallo "$nombre: el CN es '$cn' (debe ser el client_id de Keycloak)"
    fi
    vigencia "$1" "$nombre" 30
}

echo "PKI: $PKI_DIR"

# ── Raíz ─────────────────────────────────────────────────────────────────────
echo "== raíz"
if [[ -f "$RAIZ/raiz.crt" ]]; then
    sin_bom "$RAIZ/raiz.crt"
    if es_ca "$RAIZ/raiz.crt" && openssl verify -CAfile "$RAIZ/raiz.crt" "$RAIZ/raiz.crt" >/dev/null 2>&1; then
        ok "raiz.crt: CA autofirmada, $(openssl x509 -in "$RAIZ/raiz.crt" -noout -fingerprint -sha256 | cut -d= -f2)"
    else
        fallo "raiz.crt no es una CA autofirmada"
    fi
    vigencia "$RAIZ/raiz.crt" "raiz.crt" 365
    crl "$RAIZ/raiz.crl" "$RAIZ/raiz.crt" "raiz.crl" $(( 30 * 24 ))
else
    fallo "falta raiz/raiz.crt"
fi
if [[ -f "$RAIZ/raiz.key" ]]; then
    sin_bom "$RAIZ/raiz.key"
    permisos "$RAIZ/raiz.key" "400 600"
    if [[ "$PKI_DEV" == "1" ]]; then
        ok "raiz.key presente (HALSA_PKI_DEV=1: sin passphrase, solo desarrollo)"
    elif grep -q 'BEGIN ENCRYPTED PRIVATE KEY' "$RAIZ/raiz.key"; then
        ok "raiz.key cifrada (esto es el equipo sin red)"
    else
        fallo "raiz.key NO está cifrada: fuera de desarrollo debe llevar passphrase (AES-256)"
    fi
    if [[ -f "$INTER/intermedia.key" && "$PKI_DEV" != "1" ]]; then
        fallo "raiz.key e intermedia.key en el mismo HALSA_PKI_DIR: la llave de la raíz no va al VPS (runbook 06)"
    fi
fi

# ── Intermedia ───────────────────────────────────────────────────────────────
if [[ -f "$INTER/intermedia.crt" ]]; then
    echo "== intermedia"
    verificar_intermedia "$INTER/intermedia.crt"
    if [[ -f "$INTER/intermedia.key" ]]; then
        permisos "$INTER/intermedia.key" "600"
        if [[ "$(openssl x509 -in "$INTER/intermedia.crt" -noout -pubkey)" == "$(openssl pkey -in "$INTER/intermedia.key" -pubout)" ]]; then
            ok "intermedia.key corresponde a intermedia.crt"
        else
            fallo "intermedia.crt no corresponde a intermedia.key"
        fi
    fi
    crl "$INTER/intermedia.crl" "$INTER/intermedia.crt" "intermedia.crl" 48
elif [[ -f "$INTER/intermedia.csr" ]]; then
    echo "== intermedia: solo intermedia.csr, pendiente de firma en el equipo sin red"
fi

# ── Publicado (lo que copian sincronizar-npm.sh a NPM y el worker monta) ─────
if [[ -d "$PUBLICAR" ]]; then
    echo "== publicar"
    if [[ -f "$INTER/intermedia.crt" && -f "$RAIZ/raiz.crt" ]]; then
        if cmp -s "$PUBLICAR/halsa-integraciones-ca.pem" <(cat "$INTER/intermedia.crt" "$RAIZ/raiz.crt"); then
            ok "halsa-integraciones-ca.pem = intermedia.crt + raiz.crt"
        else
            fallo "halsa-integraciones-ca.pem no es intermedia.crt + raiz.crt: ejecuta generar-crl.sh"
        fi
    fi
    if [[ -f "$INTER/intermedia.crl" && -f "$RAIZ/raiz.crl" ]]; then
        if cmp -s "$PUBLICAR/halsa-integraciones.crl" <(cat "$INTER/intermedia.crl" "$RAIZ/raiz.crl"); then
            ok "halsa-integraciones.crl = intermedia.crl + raiz.crl (las dos que exige ssl_crl)"
        else
            fallo "halsa-integraciones.crl no es intermedia.crl + raiz.crl: ejecuta generar-crl.sh"
        fi
    fi
fi

# ── Clientes ─────────────────────────────────────────────────────────────────
if compgen -G "$PKI_DIR/clientes/*.crt" >/dev/null; then
    echo "== clientes"
    for c in "$PKI_DIR"/clientes/*.crt; do
        verificar_cliente "$c"
        [[ ! -f "${c%.crt}.key" ]] \
            || aviso "$(basename "${c%.crt}.key"): la llave del cliente sigue aquí; entrégala y bórrala (runbook 06)"
    done
fi

# ── Sueltos ──────────────────────────────────────────────────────────────────
for suelto in "$@"; do
    echo "== $suelto"
    if [[ ! -f "$suelto" ]]; then
        fallo "no existe $suelto"
    elif es_ca "$suelto"; then
        verificar_intermedia "$suelto"
    else
        verificar_cliente "$suelto"
    fi
done

echo
echo "Resultado: $fallos fallo(s), $avisos aviso(s)"
(( fallos == 0 ))

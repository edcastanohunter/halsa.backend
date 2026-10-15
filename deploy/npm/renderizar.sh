#!/bin/sh
# ─────────────────────────────────────────────────────────────────────────────
# renderizar.sh <origen> <destino> — renderiza las plantillas *.conf de Halsa (SPEC 13).
#
# Sustituye SOLO las variables HALSA_* del entorno: las de Nginx ($host, $remote_addr...)
# quedan intactas. Conserva nombres y subcarpetas, así que NPM (sincronizar-npm.sh), el
# Nginx de desarrollo y el CI ven exactamente las mismas rutas:
#   deploy/npm/http.conf            → <destino>/http.conf
#   deploy/npm/snippets/proxy.conf  → <destino>/snippets/proxy.conf
#   deploy/npm/sitios/api.conf      → <destino>/sitios/api.conf
#
# POSIX sh y envsubst (gettext): corre en el VPS y dentro de nginx:alpine.
# Falla si queda un ${HALSA_...} sin valor: es una variable faltante, no un literal.
# ─────────────────────────────────────────────────────────────────────────────
set -eu

origen="${1:?uso: renderizar.sh <origen> <destino>}"
destino="${2:?uso: renderizar.sh <origen> <destino>}"

variables=$(env | sed -n 's/^\(HALSA_[A-Z0-9_]*\)=.*/${\1}/p' | tr '\n' ' ')

mkdir -p "$destino"
cd "$origen"
find . -type f -name '*.conf' | while read -r archivo; do
    mkdir -p "$destino/$(dirname "$archivo")"
    envsubst "$variables" < "$archivo" > "$destino/$archivo"
done

if grep -rn '\${HALSA_' "$destino" >&2; then
    echo "renderizar.sh: hay variables HALSA_* sin valor (arriba)" >&2
    exit 1
fi

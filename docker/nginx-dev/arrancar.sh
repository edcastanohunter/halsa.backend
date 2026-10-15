#!/bin/sh
# ─────────────────────────────────────────────────────────────────────────────
# arrancar.sh — arranque del Nginx que imita a NPM (desarrollo y CI, SPEC 13).
#
#   1. renderiza deploy/npm (montado en /halsa/npm) en /data/nginx/custom/halsa, como hace
#      sincronizar-npm.sh en NPM, y servidores.conf en /etc/nginx/simulado;
#   2. escribe el resolver a partir de /etc/resolv.conf, como hace NPM: Halsa resuelve sus
#      upstreams en cada request (proxy_pass con variables);
#   3. ejecuta el comando ("nginx -g 'daemon off;'" en desarrollo, "nginx -t" en el CI).
# ─────────────────────────────────────────────────────────────────────────────
set -eu

sh /halsa/npm/renderizar.sh /halsa/npm /data/nginx/custom/halsa
sh /halsa/npm/renderizar.sh /halsa/simulado /etc/nginx/simulado

servidor_dns=$(awk '/^nameserver/ { print $2; exit }' /etc/resolv.conf)
echo "resolver ${servidor_dns:-127.0.0.11} valid=10s ipv6=off;" > /etc/nginx/simulado/resolver.conf

exec "$@"

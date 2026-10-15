#!/bin/sh
# ─────────────────────────────────────────────────────────────────────────────
# arrancar-dotnet.sh — entrypoint de api, worker, migrador y bff en producción (SPEC 13).
#
# Los secretos de Docker son archivos root:root 0600 (/etc/halsa/secretos) montados en
# /run/secrets, y las imágenes .NET corren como el usuario sin privilegios "app" ($APP_UID).
# Este script arranca como root con capacidades mínimas, y:
#   1. copia SOLO los secretos que necesita este rol a /run/halsa (tmpfs: en RAM, nunca en
#      disco ni en un backup), legibles solo por "app";
#   2. arma las cadenas de conexión y demás variables a partir de ellos;
#   3. baja a "app" (setpriv) y ejecuta el comando ("dotnet Halsa.Backend.Api.dll", ...).
#
# Las cadenas usan SSL Mode=Require: pg_hba.conf solo admite hostssl desde la red "datos".
# ─────────────────────────────────────────────────────────────────────────────
set -eu

UID_APP="${APP_UID:-1654}"
DESTINO=/run/halsa
DB_HOST="${HALSA_DB_HOST:-postgres}"
DB_NOMBRE="${HALSA_DB_NOMBRE:-halsa}"

mkdir -p "$DESTINO"
chmod 700 "$DESTINO"

secreto() {
    [ -r "/run/secrets/$1" ] || { echo "arrancar-dotnet.sh: falta el secreto $1" >&2; exit 1; }
    tr -d '\r\n' < "/run/secrets/$1"
}

copiar() {
    [ -r "/run/secrets/$1" ] || { echo "arrancar-dotnet.sh: falta el secreto $1" >&2; exit 1; }
    cp "/run/secrets/$1" "$DESTINO/$1"
    chmod 400 "$DESTINO/$1"
}

cadena() {
    _clave="$(secreto "$2")" || exit 1
    echo "Host=$DB_HOST;Port=5432;Database=$DB_NOMBRE;Username=$1;Password=$_clave;SSL Mode=Require"
}

redis() {
    _clave="$(secreto redis_password)" || exit 1
    echo "redis:6379,password=$_clave,abortConnect=false"
}

# Assign first, export after: "export X=$(...)" would hide a missing secret (the exit 1 of secreto() only ends the
# subshell) and start the service with an empty password.
case "${HALSA_SERVICIO:?HALSA_SERVICIO: api, worker, migrador o bff}" in
    api|worker)
        ConnectionStrings__Database="$(cadena halsa_app pg_app_password)"; export ConnectionStrings__Database
        ConnectionStrings__Clinical="$(cadena halsa_clinico pg_clinico_password)"; export ConnectionStrings__Clinical
        ConnectionStrings__Ingesta="$(cadena halsa_ingesta pg_ingesta_password)"; export ConnectionStrings__Ingesta
        ConnectionStrings__Cache="$(redis)"; export ConnectionStrings__Cache
        copiar halsa_kek
        export Clinico__KekPath="$DESTINO/halsa_kek"
        ;;
esac

case "$HALSA_SERVICIO" in
    worker)
        ConnectionStrings__Worker="$(cadena halsa_worker pg_worker_password)"; export ConnectionStrings__Worker
        # TODO(SPEC 11): the seal key and the WORM bucket credentials. The secrets are declared and mounted, but no
        # code reads them yet (SPEC 11 is still a draft); copy them here when it lands:
        #   copiar halsa_sello; export Sellado__LlavePath="$DESTINO/halsa_sello"
        #   export Sellado__Worm__AccessKey="$(secreto worm_s3_key)" ...
        ;;
    migrador)
        ConnectionStrings__Migrations="$(cadena halsa_owner pg_owner_password)"; export ConnectionStrings__Migrations
        ;;
    bff)
        ConnectionStrings__Redis="$(redis)"; export ConnectionStrings__Redis
        Bff__ClientSecret="$(secreto kc_bff_secret)"; export Bff__ClientSecret
        ;;
esac

chown -R "$UID_APP:$UID_APP" "$DESTINO"

# The encrypted supports (SPEC 07) live in a volume Docker creates as root.
if [ -n "${Archivos__Raiz:-}" ] && [ -d "$Archivos__Raiz" ]; then
    chown "$UID_APP:$UID_APP" "$Archivos__Raiz"
fi

exec setpriv --reuid="$UID_APP" --regid="$UID_APP" --clear-groups --inh-caps=-all -- "$@"

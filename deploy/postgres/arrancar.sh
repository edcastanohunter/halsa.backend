#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# arrancar.sh — entrypoint del PostgreSQL de producción (SPEC 13).
#
# 1. Escribe los secretos de pgBackRest (credenciales del bucket y passphrase del cifrado)
#    en /etc/pgbackrest/conf.d/secretos.conf (tmpfs, 0600, del usuario postgres). Así los
#    leen tanto el archive_command del servidor como los backups lanzados con
#    `docker compose exec` (runbook 04), sin pasar por variables del contenedor.
# 2. Genera el certificado TLS del servidor si no existe (hostssl en pg_hba.conf). Es
#    autofirmado: los clientes están en la red interna "datos" y usan SSL Mode=Require.
# 3. Copia las contraseñas de los roles a un tmpfs que solo lee "postgres" (initdb/01-roles.sh).
# 4. Sigue con el entrypoint oficial (initdb + scripts de initdb/ la primera vez).
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

secreto() {
    local archivo="/run/secrets/$1"
    [[ -r "$archivo" ]] || { echo "arrancar.sh: falta el secreto $1" >&2; exit 1; }
    tr -d '\r\n' < "$archivo"
}

# ── 1. pgBackRest ─────────────────────────────────────────────────────────────
umask 077
{
    echo "[global]"
    echo "repo1-s3-key=$(secreto backups_s3_key)"
    echo "repo1-s3-key-secret=$(secreto backups_s3_secret)"
    echo "repo1-cipher-pass=$(secreto backups_cipher_pass)"
} > /etc/pgbackrest/conf.d/secretos.conf
chown postgres:postgres /etc/pgbackrest/conf.d/secretos.conf
umask 022

# The bucket, its region and endpoint are not secret: they come from .env (HALSA_BACKUPS_*).
{
    echo "[global]"
    echo "repo1-s3-bucket=${HALSA_BACKUPS_BUCKET:?HALSA_BACKUPS_BUCKET}"
    echo "repo1-s3-region=${HALSA_BACKUPS_REGION:?HALSA_BACKUPS_REGION}"
    echo "repo1-s3-endpoint=${HALSA_BACKUPS_ENDPOINT:?HALSA_BACKUPS_ENDPOINT}"
} > /etc/pgbackrest/conf.d/bucket.conf
chown postgres:postgres /etc/pgbackrest/conf.d/bucket.conf

# ── 2. TLS del servidor ───────────────────────────────────────────────────────
tls=/var/lib/postgresql/tls
if [[ ! -f "$tls/servidor.crt" ]]; then
    mkdir -p "$tls"
    openssl req -x509 -newkey ec -pkeyopt ec_paramgen_curve:P-256 -nodes -days 3650 \
        -subj "/CN=postgres" -keyout "$tls/servidor.key" -out "$tls/servidor.crt" 2>/dev/null
    chown -R postgres:postgres "$tls"
    chmod 600 "$tls/servidor.key"
fi

# ── 3. The role passwords, for initdb/01-roles.sh ─────────────────────────────
# The host files are root:root 0600 and the official entrypoint runs initdb as "postgres": it reads its copies from a
# tmpfs (/run/halsa-secretos, in RAM) only it can read.
privados=/run/halsa-secretos
mkdir -p "$privados"
for s in pg_postgres_password pg_owner_password pg_app_password pg_clinico_password pg_ingesta_password \
         pg_worker_password; do
    secreto "$s" > "$privados/$s"
done
chown -R postgres:postgres "$privados"
chmod 700 "$privados"
chmod 400 "$privados"/*
export POSTGRES_PASSWORD_FILE="$privados/pg_postgres_password"

# ── 4. The official entrypoint (it drops to the postgres user) ────────────────
exec docker-entrypoint.sh "$@"

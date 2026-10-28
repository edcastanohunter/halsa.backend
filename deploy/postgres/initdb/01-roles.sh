#!/bin/bash
# ─────────────────────────────────────────────────────────────────────────────
# 01-roles.sh — roles de producción (SPEC 13). Lo ejecuta el entrypoint oficial UNA VEZ,
# sobre un volumen vacío, como superusuario. Es el equivalente de docker/postgres/init/
# 01-roles.sql (desarrollo), con las contraseñas de los secretos de Docker en vez de
# las de desarrollo. Los permisos sobre esquemas y tablas los dan las migraciones
# (Halsa:Rol=Migrador, como halsa_owner).
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

# Copies made by arrancar.sh: this script runs as "postgres", which cannot read /run/secrets (root:root 0600 inside the
# container; on the host, the Quadlets' owner).
secreto() {
    local archivo="/run/halsa-secretos/$1"
    [[ -r "$archivo" ]] || { echo "01-roles.sh: falta el secreto $1" >&2; exit 1; }
    tr -d '\r\n' < "$archivo"
}

# psql variables (:'x') quote the passwords: no secret is interpolated into the SQL text.
psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" \
    -v owner="$(secreto pg_owner_password)" \
    -v app="$(secreto pg_app_password)" \
    -v clinico="$(secreto pg_clinico_password)" \
    -v ingesta="$(secreto pg_ingesta_password)" \
    -v worker="$(secreto pg_worker_password)" <<'SQL'
-- Owner of every schema; the migrations run as it. No BYPASSRLS: FORCE RLS covers it too.
CREATE ROLE halsa_owner LOGIN PASSWORD :'owner';
-- The API at runtime (SPEC 01–02).
CREATE ROLE halsa_app LOGIN PASSWORD :'app';
-- The clinical context (SPEC 03); never USAGE on atel.
CREATE ROLE halsa_clinico LOGIN PASSWORD :'clinico';
-- The FURAT/FUREP ingestion (SPEC 05).
CREATE ROLE halsa_ingesta LOGIN PASSWORD :'ingesta';
-- The Worker's cross-tenant sweeps (SPEC 09).
CREATE ROLE halsa_worker LOGIN PASSWORD :'worker';

GRANT CREATE ON DATABASE halsa TO halsa_owner;
REVOKE ALL ON DATABASE halsa FROM PUBLIC;
GRANT CONNECT ON DATABASE halsa TO halsa_owner, halsa_app, halsa_clinico, halsa_ingesta, halsa_worker;

-- Keycloak is the shared one at auth.edcastdev.com (SPEC 13, amendment): it has no database here.
SQL

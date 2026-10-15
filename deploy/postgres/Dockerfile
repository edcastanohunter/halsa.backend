# syntax=docker/dockerfile:1
# ─────────────────────────────────────────────────────────────────────────────
# PostgreSQL de producción (SPEC 13): postgres:17 + pgBackRest (PITR a un bucket S3
# externo) + pgaudit (DDL y cambios de roles al log).
#
# La configuración va dentro de la imagen; los secretos, no: arrancar.sh los lee de
# /run/secrets al arrancar.
#
#   docker build -t halsa-postgres:17 deploy/postgres
# ─────────────────────────────────────────────────────────────────────────────
FROM postgres:17-bookworm

# The official image already carries the PGDG apt repository, so pgaudit matches the server's major version.
RUN apt-get update \
 && apt-get install -y --no-install-recommends pgbackrest postgresql-17-pgaudit ca-certificates \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /etc/pgbackrest/conf.d /var/log/pgbackrest /var/lib/pgbackrest /var/spool/pgbackrest \
 && chown -R postgres:postgres /etc/pgbackrest /var/log/pgbackrest /var/lib/pgbackrest /var/spool/pgbackrest

COPY --chown=postgres:postgres postgresql.conf pg_hba.conf /etc/postgresql/
COPY --chown=postgres:postgres pgbackrest.conf /etc/pgbackrest/pgbackrest.conf
COPY --chmod=755 initdb/ /docker-entrypoint-initdb.d/
COPY --chmod=755 arrancar.sh /usr/local/bin/halsa-arrancar.sh

ENV TZ=UTC PGTZ=UTC

ENTRYPOINT ["/usr/local/bin/halsa-arrancar.sh"]
CMD ["postgres", "-c", "config_file=/etc/postgresql/postgresql.conf"]

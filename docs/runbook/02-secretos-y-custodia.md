# Runbook 02 — Secretos y custodia

> SPEC 13. El VPS es un único servidor, así que la defensa en profundidad depende de **qué sale de él**. Los
> backups y los sellos van a buckets externos. Las llaves que hacen legible la historia clínica (HC), o que vuelven
> confiable una integración, **no van en ningún backup**: tienen custodia propia, fuera de línea.

## Qué es cada secreto y dónde vive

| Secreto | En el VPS | Custodia fuera de línea | Si se pierde |
| --- | --- | --- | --- |
| `halsa_kek` (la KEK, SPEC 03) | `/etc/halsa/secretos/halsa_kek` | **Sí**, 2 ubicaciones | Las DEK guardadas en la base no se pueden desenvolver: **la HC queda ilegible** |
| `halsa_sello` (SPEC 11, pendiente) | `/etc/halsa/secretos/halsa_sello` | **Sí**, 2 ubicaciones | No se pueden emitir sellos con la misma llave (los viejos se verifican con la pública) |
| Raíz de la CA de integraciones | **Nunca** | **Sí**, equipo sin red + 2 copias cifradas | No se pueden emitir intermedias nuevas: hay que crear otra CA y reemitir todo |
| Intermedia de la CA | `/etc/halsa/pki/intermedia/intermedia.key` | No (se reemite desde la raíz) | Runbook 06, "Compromiso o pérdida de la intermedia" |
| `pg_*_password` | `/etc/halsa/secretos/` | En el gestor de contraseñas del equipo | Se cambian con `ALTER ROLE` (runbook 05) |
| `backups_s3_*`, `backups_cipher_pass` | `/etc/halsa/secretos/` | **`backups_cipher_pass` sí**: sin ella, los backups no sirven | Sin la passphrase, el repositorio de pgBackRest es ilegible |
| `worm_s3_*` (SPEC 11, pendiente) | `/etc/halsa/secretos/` | En el gestor de contraseñas | Se rotan en el proveedor |
| `kc_bff_secret`, `redis_password` | `/etc/halsa/secretos/` | En el gestor de contraseñas | Se regeneran (runbook 05; el del BFF también en el Keycloak compartido) |

**Custodia fuera de línea** = dos ubicaciones independientes (p. ej. una caja fuerte en la oficina y una bóveda de
un custodio externo). En cada una, una memoria USB cifrada (LUKS o VeraCrypt) con la KEK, la llave de sello y la
copia de la raíz. La passphrase la tienen al menos dos personas, y ninguna de ellas puede abrirla sola
(*split knowledge*: cada una conoce la mitad).

## Crear el directorio de secretos (VPS nuevo)

```bash
install -d -o root -g root -m 0700 /etc/halsa/secretos
cd /etc/halsa/secretos
umask 077

# Restaurar desde la custodia (NO generar una KEK nueva si ya hay datos cifrados con la anterior):
cp /media/custodia/halsa_kek     halsa_kek
cp /media/custodia/halsa_sello   halsa_sello       # SPEC 11: mientras no exista, un valor cualquiera
# (solo para un despliegue sin datos previos) KEK nueva: 32 bytes en base64
#   openssl rand -base64 32 | tr -d '\n' > halsa_kek

for s in pg_postgres_password pg_owner_password pg_app_password pg_clinico_password pg_ingesta_password \
         pg_worker_password kc_bff_secret redis_password; do
  [ -f "$s" ] || openssl rand -base64 32 | tr -d '\n/+=' > "$s"
done
printf '%s' '<access key>'  > backups_s3_key
printf '%s' '<secret key>'  > backups_s3_secret
printf '%s' '<passphrase>'  > backups_cipher_pass     # la de la custodia, si se reconstruye
printf '%s' '<access key>'  > worm_s3_key             # SPEC 11
printf '%s' '<secret key>'  > worm_s3_secret          # SPEC 11

chown root:root ./* && chmod 0600 ./*
ls -l   # todos -rw------- root root
```

Halsa no guarda secretos de Keycloak salvo `kc_bff_secret`: la base y el admin de Keycloak son del dueño de esa
instancia compartida. Los realms de Halsa viven en ella; lo que Halsa puede reconstruir está en el repo (runbook 06).

Cada contenedor recibe solo los suyos (`secrets:` en `docker-compose.prod.yml`). Los servicios que no corren como
root los copian al arrancar a un **tmpfs** propio (`deploy/scripts/arrancar-dotnet.sh` y `deploy/postgres/arrancar.sh`).
Así nunca tocan el disco del contenedor ni un volumen.

## Excluir de backups y snapshots

1. **pgBackRest** respalda solo `PGDATA` (`/var/lib/postgresql/data` dentro del volumen `postgres-datos`). Los secretos
   no están ahí. Verificación (criterio *staging*):
   ```bash
   docker compose -f /opt/halsa/deploy/docker-compose.prod.yml exec -u postgres postgres \
     pgbackrest --stanza=halsa repo-ls --recurse | grep -Ei 'secret|halsa_kek|sello|password' \
     && echo "FALLO" || echo "OK: ningún secreto en el repositorio"
   ```
2. **Snapshots del proveedor del VPS:** si se usan, el disco completo entra en el snapshot. Por eso:
   - `/etc/halsa/secretos` y `/etc/halsa/pki/intermedia` van en un **volumen aparte** (un disco adicional pequeño o un
     `tmpfs` que se repuebla desde la custodia al arrancar), **excluido** de la política de snapshots;
   - o los snapshots del proveedor se desactivan y la recuperación se hace solo con el runbook 01 + 04.
   Anota aquí qué opción se usa y la revisión: `____________ (fecha, responsable)`.
3. **Cualquier otra copia** (rsync, restic) excluye `/etc/halsa/` y `/opt/halsa/deploy/.env`.

## Ensayo semestral de recuperación

Cada 6 meses, con dos custodios presentes:

1. Abrir una de las dos ubicaciones y montar la memoria cifrada en un equipo sin red.
2. Verificar que la KEK de la custodia es la del VPS: `sha256sum halsa_kek` en los dos lados.
3. Restaurar un backup en un contenedor temporal con esa KEK (runbook 04, `restaurar-prueba.sh`) y leer un registro
   clínico con la API apuntando a esa base. Debe descifrarse.
4. Registrar el resultado en `docs/runbook/registro-ensayos.md` (fecha, custodios, resultado).
5. Repetir el paso 2 con la **segunda** ubicación.

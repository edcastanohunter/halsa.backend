#!/usr/bin/env bash
# version: 1
# ─────────────────────────────────────────────────────────────────────────────
# recibir-despliegue.sh — the only command the CI's SSH key can run on the VPS (SPEC 14, runbooks 01 y 03).
#
# Installed as /usr/local/sbin/halsa-recibir-despliegue (root 0755), OUTSIDE /opt/halsa/deploy: the deploy
# itself rewrites that folder, and code that runs as root only changes when a person reinstalls it.
#
#   ssh halsa-deploy@vps "desplegar v1.0.<n>" < paquete.tar
#
#   paquete.tar   manifiesto.sha256, imagenes.tar.gz, deploy.tar.gz y, opcional, postgres.tar.gz
#
#   1. valida SSH_ORIGINAL_COMMAND y el tag; toma el lock (un despliegue a la vez)
#   2. copia la entrada estándar a /var/lib/halsa/entregas/<tag>/ (hasta HALSA_PAQUETE_MAX_MB)
#   3. extrae el paquete y verifica sha256sum -c manifiesto.sha256
#   4. docker load de las imágenes; verifica que estén las del tag
#   5. sincroniza deploy/ en /opt/halsa/deploy con rsync --delete, sin tocar .env
#   6. ejecuta desplegar.sh <tag> y devuelve su código de salida
#   7. si terminó en 0: retención de imágenes y borrado de la zona de recepción
#
# Exit: 2 comando inválido; 1 cualquier fallo (en 1-5, antes de tocar .env o los contenedores).
# Variables: HALSA_PAQUETE_MAX_MB (3072), HALSA_VERSIONES_RETENIDAS (5). sudo las borra salvo que estén en el
# env_keep del sudoers (runbook 01).
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

DESTINO=/opt/halsa/deploy
ENTREGAS=/var/lib/halsa/entregas
LOCK=/var/lock/halsa-despliegue.lock
MAX_MB="${HALSA_PAQUETE_MAX_MB:-3072}"
RETENIDAS="${HALSA_VERSIONES_RETENIDAS:-5}"
REGEX_TAG='^v[0-9]+\.[0-9]+\.[0-9]+([-.][0-9A-Za-z.-]+)?$'

info() { echo "==> $(date -u +%H:%M:%S) $*" >&2; }
fallo() { echo "FALLO: $*" >&2; exit 1; }

# ── 1. Comando, tag y lock ───────────────────────────────────────────────────
comando="${SSH_ORIGINAL_COMMAND:-}"
read -r accion tag resto <<< "$comando" || true
if [[ "${accion:-}" != "desplegar" || -n "${resto:-}" || ! "${tag:-}" =~ $REGEX_TAG ]]; then
    echo "comando no permitido; el único válido es: desplegar <tag vX.Y.Z>" >&2
    exit 2
fi
[[ "$MAX_MB" =~ ^[0-9]+$ && "$RETENIDAS" =~ ^[0-9]+$ ]] || fallo "HALSA_PAQUETE_MAX_MB y HALSA_VERSIONES_RETENIDAS deben ser enteros"
[[ -f "$DESTINO/.env" ]] || fallo "falta $DESTINO/.env (runbook 01)"

exec 9>"$LOCK"
info "esperando el lock de despliegue"
flock -w 1800 9 || fallo "otro despliegue tiene el lock desde hace 30 min"

entrega="$ENTREGAS/$tag"
temporal=""
limpiar() { [[ -n "$temporal" ]] && rm -rf "$temporal"; return 0; }
trap limpiar EXIT

rm -rf "$entrega"
mkdir -p "$ENTREGAS"
chmod 0700 "$ENTREGAS"
mkdir -m 0700 "$entrega"

# ── 2. Recepción ─────────────────────────────────────────────────────────────
info "1/6 recibiendo el paquete de $tag (máximo ${MAX_MB} MB)"
head -c "$((MAX_MB * 1024 * 1024))" > "$entrega/paquete.tar"
if [[ "$(head -c 1 | wc -c)" -gt 0 ]]; then
    fallo "el paquete supera ${MAX_MB} MB; nada cambió"
fi

# ── 3. Contenido y sumas ─────────────────────────────────────────────────────
info "2/6 verificando el paquete"
miembros="$(tar -tf "$entrega/paquete.tar")" || fallo "el paquete no es un tar válido; nada cambió"
while IFS= read -r m; do
    case "$m" in
        manifiesto.sha256|imagenes.tar.gz|deploy.tar.gz|postgres.tar.gz) ;;
        *) fallo "miembro inesperado en el paquete: '$m'; nada cambió" ;;
    esac
done <<< "$miembros"
tar -xf "$entrega/paquete.tar" -C "$entrega" --no-same-owner
rm -f "$entrega/paquete.tar"
for requerido in manifiesto.sha256 imagenes.tar.gz deploy.tar.gz; do
    [[ -f "$entrega/$requerido" ]] || fallo "falta $requerido en el paquete; nada cambió"
done
for archivo in imagenes.tar.gz deploy.tar.gz postgres.tar.gz; do
    if [[ -f "$entrega/$archivo" ]] && ! grep -Eq "^[0-9a-f]{64}  $archivo\$" "$entrega/manifiesto.sha256"; then
        fallo "$archivo no está en el manifiesto; nada cambió"
    fi
done
(cd "$entrega" && sha256sum --quiet -c manifiesto.sha256) || fallo "las sumas sha256 no coinciden; nada cambió"

# ── 4. Imágenes ──────────────────────────────────────────────────────────────
info "3/6 docker load"
gunzip -c "$entrega/imagenes.tar.gz" | docker load >&2 || fallo "docker load de imagenes.tar.gz; nada cambió"
if [[ -f "$entrega/postgres.tar.gz" ]]; then
    gunzip -c "$entrega/postgres.tar.gz" | docker load >&2 || fallo "docker load de postgres.tar.gz; nada cambió"
    info "halsa-postgres cargada; recrear postgres es manual (runbook 03)"
fi
for imagen in "localhost/halsa-backend:$tag" "localhost/halsa-bff-paciente:$tag"; do
    docker image inspect "$imagen" >/dev/null 2>&1 || fallo "el paquete no trae $imagen; nada cambió"
done

# ── 5. deploy/ ───────────────────────────────────────────────────────────────
info "4/6 sincronizando $DESTINO (sin tocar .env)"
temporal="$(mktemp -d)"
tar -xzf "$entrega/deploy.tar.gz" -C "$temporal" --no-same-owner || fallo "deploy.tar.gz ilegible; nada cambió"
[[ -f "$temporal/deploy/docker-compose.prod.yml" && -f "$temporal/deploy/scripts/desplegar.sh" ]] \
    || fallo "deploy.tar.gz no trae deploy/docker-compose.prod.yml ni deploy/scripts/desplegar.sh; nada cambió"
rsync -a --delete --exclude /.env "$temporal/deploy/" "$DESTINO/" || fallo "rsync a $DESTINO"

# ── 6. Despliegue ────────────────────────────────────────────────────────────
anterior="$(grep -E '^HALSA_TAG=' "$DESTINO/.env" | cut -d= -f2- || true)"
info "5/6 desplegar.sh $tag"
set +e
bash "$DESTINO/scripts/desplegar.sh" "$tag"
codigo=$?
set -e
if (( codigo != 0 )); then
    echo "FALLO: desplegar.sh terminó con $codigo; la entrega queda en $entrega para diagnóstico." >&2
    exit "$codigo"
fi

# ── 7. Retención y limpieza ──────────────────────────────────────────────────
info "6/6 retención: ${RETENIDAS} versiones, más $tag y ${anterior:-ninguna}"
# The current and the previous tag always stay; the newest others fill up to RETENIDAS.
declare -A conservar=(["$tag"]=1)
[[ -n "$anterior" ]] && conservar["$anterior"]=1
mapfile -t tags < <(docker image ls localhost/halsa-backend --format '{{.CreatedAt}}|{{.Tag}}' | sort -r | cut -d'|' -f2)
for t in "${tags[@]}"; do
    [[ "$t" == "<none>" || -n "${conservar[$t]:-}" ]] && continue
    if (( ${#conservar[@]} < RETENIDAS )); then
        conservar["$t"]=1
        continue
    fi
    info "borrando las imágenes de $t"
    docker image rm "localhost/halsa-backend:$t" "localhost/halsa-bff-paciente:$t" >/dev/null 2>&1 || true
done
rm -rf "$entrega"
info "OK: $tag desplegado"

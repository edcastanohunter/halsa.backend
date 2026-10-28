#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# instalar.sh — instala los Quadlets de Halsa (rootless) en ~/.config/containers/systemd y levanta la
# infraestructura. Se ejecuta como el usuario dueño de NPM (sin sudo): las redes de Podman son por usuario, y NPM
# solo puede unirse a halsa_borde si es del mismo.
#
#   1. requisitos: no root, linger activo, Podman ≥ 5 (Entrypoint=, Notify=healthy), `docker` = podman (los
#      scripts de deploy/ lo usan), ~/halsa/deploy con su .env completo, los secretos que montan las unidades, la
#      PKI pública, que el compose de Halsa NO esté corriendo (ni el del usuario ni el de root) y que ningún
#      contenedor ajeno use los nombres halsa-*
#   2. valida las unidades con el generador de Quadlet (--user --dryrun) antes de copiar nada
#   3. las copia, recarga systemd --user y descarga las imágenes públicas (la primera vez no cabe en el timeout)
#   4. arranca postgres (espera a healthy), redis, antivirus y seq; las redes y volúmenes vienen solos
#
# Las unidades generadas NO se habilitan con `systemctl --user enable` (systemd lo rechaza): el [Install]
# (default.target) lo aplica el generador en cada daemon-reload. api, worker y bff los arranca desplegar.sh.
#
# Idempotente: se vuelve a ejecutar cada vez que cambie un archivo de deploy-quadlets/. No reinicia lo que
# ya corre: si cambió la unidad de un servicio activo, `systemctl --user restart <servicio>`.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

DIR="$(cd "$(dirname "$0")" && pwd)"
# The same paths the units name with %h.
DESTINO="$HOME/.config/containers/systemd"
DEPLOY="$HOME/halsa/deploy"
ENV="$DEPLOY/.env"
SECRETOS="$HOME/.config/halsa/secretos"
PKI="$HOME/.config/halsa/pki/publicar"
GENERADOR=/usr/lib/systemd/system-generators/podman-system-generator
# systemctl --user and rootless podman need it; sudo -u and cron do not set it.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

info() { echo "==> $(date -u +%H:%M:%S) $*"; }
die() { echo "ERROR: $*" >&2; exit 1; }

[[ "$(id -u)" -ne 0 ]] || die "se ejecuta como el usuario dueño de NPM, no como root (README, Requisitos)"

# ── 1. Requisitos ────────────────────────────────────────────────────────────
info "1/4 verificando requisitos"
[[ "$(loginctl show-user "$(id -un)" -p Linger --value 2>/dev/null)" == yes ]] \
    || die "sin linger los servicios no arrancan con el VPS: sudo loginctl enable-linger $(id -un)"
command -v podman >/dev/null || die "falta podman"
version="$(podman version --format '{{.Client.Version}}')"
[[ "${version%%.*}" -ge 5 ]] || die "Podman $version: las unidades usan Entrypoint= y Notify=healthy (Podman ≥ 5.0)"
[[ -x "$GENERADOR" ]] || die "falta el generador de Quadlet en $GENERADOR"
# recibir-despliegue.sh, sincronizar-npm.sh and restaurar-prueba.sh call "docker": it must be Podman (podman-docker).
docker --version 2>/dev/null | grep -qi podman || die "'docker' no es Podman: sudo apt install podman-docker"

[[ -f "$ENV" ]] || die "falta $ENV (README, Instalación)"
[[ -f "$DEPLOY/scripts/arrancar-dotnet.sh" ]] || die "falta $DEPLOY/scripts/arrancar-dotnet.sh (README, Instalación)"
set -a
# shellcheck disable=SC1090  # the VPS's .env: not in the repo
source "$ENV"
set +a

# What the compose demands with ${VAR:?} or fills with a default: systemd leaves an unset one empty, with no default.
faltan=()
for v in HALSA_TAG HALSA_POSTGRES_TAG HALSA_SEQ_TAG HALSA_KEYCLOAK_URL HALSA_KEYCLOAK_HOST HALSA_NPM_IP HALSA_KEK_ID \
         HALSA_HOST_API HALSA_HOST_INTEGRACIONES HALSA_HOST_PACIENTES \
         HALSA_BACKUPS_BUCKET HALSA_BACKUPS_REGION HALSA_BACKUPS_ENDPOINT HALSA_SEQ_ADMIN_HASH; do
    [[ -n "${!v:-}" ]] || faltan+=("$v")
done
(( ${#faltan[@]} == 0 )) || die "faltan en $ENV: ${faltan[*]}"
[[ "$HALSA_KEYCLOAK_URL" == https://* ]] || die "HALSA_KEYCLOAK_URL debe ser https (ProductionGuard)"
# The units mount these paths literally; the scripts of deploy/ read them from .env.
[[ "${HALSA_SECRETOS:-}" == "$SECRETOS" ]] || die "en $ENV: HALSA_SECRETOS=$SECRETOS (las unidades montan esa ruta)"
[[ "${HALSA_PKI_PUBLICADA:-}" == "$PKI" ]] || die "en $ENV: HALSA_PKI_PUBLICADA=$PKI (el worker monta esa ruta)"

[[ "$(stat -c '%a' "$SECRETOS" 2>/dev/null)" == 700 ]] || die "$SECRETOS debe existir con permisos 0700 (runbook 02)"
# The secrets are exactly the files the units bind-mount: a missing one only shows up as a failed start.
faltan=()
while IFS= read -r s; do
    s="${s/\%h/$HOME}"
    if [[ ! -s "$s" ]]; then
        faltan+=("$(basename "$s")")
    elif [[ "$(stat -c '%U %a' "$s")" != "$(id -un) 600" ]]; then
        die "$s debe ser de $(id -un) con 0600: en rootless, el root del contenedor es este usuario"
    fi
done < <(grep -ho '^Volume=%h/.config/halsa/secretos/[^:]*' "$DIR"/*.container | cut -d= -f2 | sort -u)
(( ${#faltan[@]} == 0 )) || die "faltan secretos en $SECRETOS: ${faltan[*]} (runbook 02)"

[[ -f "$PKI/halsa-integraciones-ca.pem" && -f "$PKI/halsa-integraciones.crl" ]] \
    || die "falta la PKI pública en $PKI (runbook 06, 'Instalar la intermedia en el VPS')"

for etiqueta in com.docker.compose.project=halsa io.podman.compose.project=halsa; do
    corriendo="$(podman ps --filter "label=$etiqueta" --format '{{.Names}}' | tr '\n' ' ')"
    [[ -z "$corriendo" ]] || die "el compose de Halsa sigue corriendo ($corriendo): deténlo antes"
done
# The compose as root is invisible to this user's podman, but its postgres would archive WAL to the same pgBackRest
# stanza as halsa-postgres. conmon names each container with -n.
if pgrep -u root -f -- 'conmon .*-n halsa[-_]' >/dev/null; then
    die "hay contenedores de Halsa corriendo como root (el compose anterior): runbook 03, 'Transición a rootless'"
fi
# Quadlet starts with --replace: a container of someone else with one of these names would be removed.
while IFS= read -r nombre; do
    unidad="$(podman inspect "$nombre" --format '{{index .Config.Labels "PODMAN_SYSTEMD_UNIT"}}' 2>/dev/null || true)"
    if podman container exists "$nombre" && [[ "$unidad" != "$nombre.service" ]]; then
        die "ya existe un contenedor '$nombre' que no es de los Quadlets de Halsa: Quadlet lo reemplazaría"
    fi
done < <(grep -ho '^ContainerName=.*' "$DIR"/*.container | cut -d= -f2)

# ── 2. Validación ────────────────────────────────────────────────────────────
info "2/4 validando las unidades (generador de Quadlet, --user --dryrun)"
if ! salida="$(QUADLET_UNIT_DIRS="$DIR" "$GENERADOR" --user --dryrun 2>&1 >/dev/null)"; then
    grep -v 'Loading source unit file' <<< "$salida" >&2 || true
    die "el generador rechazó las unidades; nada se copió"
fi

# ── 3. Copia e imágenes ──────────────────────────────────────────────────────
info "3/4 copiando a $DESTINO, recargando systemd --user y descargando las imágenes públicas"
install -d -m 0700 "$DESTINO"
install -m 0644 "$DIR"/*.network "$DIR"/*.volume "$DIR"/*.container "$DESTINO/"
systemctl --user daemon-reload
podman pull -q docker.io/library/redis:7-alpine docker.io/clamav/clamav:1.4 "docker.io/datalust/seq:$HALSA_SEQ_TAG" >/dev/null
podman image exists "localhost/halsa-postgres:$HALSA_POSTGRES_TAG" \
    || die "falta localhost/halsa-postgres:$HALSA_POSTGRES_TAG: la carga el CI (workflow_dispatch con postgres)"

# ── 4. Infraestructura ───────────────────────────────────────────────────────
info "4/4 arrancando postgres (hasta healthy), redis, antivirus y seq"
systemctl --user start halsa-postgres.service halsa-redis.service halsa-antivirus.service halsa-seq.service

info "OK. Estado: systemctl --user status 'halsa-*'"
info "Primera vez: podman exec -u postgres halsa-postgres pgbackrest --stanza=halsa stanza-create (runbook 04)"
info "Luego: $DEPLOY/scripts/desplegar.sh $HALSA_TAG (si la imagen está cargada) o el job desplegar del CI"

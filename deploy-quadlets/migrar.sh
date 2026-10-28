#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# migrar.sh — ejecuta las migraciones (halsa-migrador.service) sin desplegar una versión nueva.
#
# Corre como el usuario dueño de los Quadlets rootless (deploy-quadlets/README), nunca como root.
# Usa la imagen de HALSA_TAG en ~/halsa/deploy/.env. Uso:  bash ~/halsa/deploy-quadlets/migrar.sh
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

[[ "$(id -u)" -ne 0 ]] || { echo "ERROR: ejecútalo como el usuario dueño de los Quadlets, no como root" >&2; exit 1; }
# systemctl --user needs it; sudo -u and cron do not set it.
export XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-/run/user/$(id -u)}"

info() { echo "==> $(date -u +%H:%M:%S) $*"; }

info "Iniciando servicio de migraciones (halsa-migrador)"
systemctl --user reset-failed halsa-migrador.service 2>/dev/null || true
if systemctl --user start halsa-migrador.service; then
    info "Migraciones completadas exitosamente"
else
    echo "ERROR: Falló la ejecución de las migraciones" >&2
    journalctl --user -u halsa-migrador.service -n 50 --no-pager >&2 || true
    exit 1
fi

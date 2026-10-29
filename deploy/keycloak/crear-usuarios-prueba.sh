#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# crear-usuarios-prueba.sh — da de alta (o borra) los usuarios de la fase de pruebas en el Keycloak COMPARTIDO.
#
# Complementa a deploy/scripts/pruebas.sh: sembrar.sql crea los casos de PRUEBA_A/PRUEBA_B y estos usuarios son quienes los
# operan. Los `sub` son FIJOS (01999f00-0000-7000-8000-…) porque sembrar.sql ya los usa en asignaciones, solicitudes y vínculos;
# si Keycloak no respeta el id pedido, el script se detiene y deshace el usuario.
#
#   crear-usuarios-prueba.sh crear      crea los 15 usuarios que falten (no toca los que ya existen)
#   crear-usuarios-prueba.sh listar     muestra cuáles existen
#   crear-usuarios-prueba.sh eliminar   los borra (pide confirmación; solo los de esta lista y con su id exacto)
#
# Como crear-admin.sh: ninguna contraseña queda en un archivo ni en una línea de comandos (se piden en la terminal y viajan por
# stdin); cada usuario nace con la contraseña temporal que elijas, UPDATE_PASSWORD y, el personal, CONFIGURE_TOTP.
# Los correos son <usuario>@${HALSA_PRUEBAS_DOMINIO:-pruebas.halsa.invalid}: ninguno recibe correo (emailVerified=true). Si los
# testers necesitan recuperar la contraseña por correo, define HALSA_PRUEBAS_DOMINIO con un dominio cuyo buzón controles.
#
# Lee de deploy/.env: HALSA_KEYCLOAK_CONTENEDOR y, opcional, HALSA_KEYCLOAK_URL_INTERNA.
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

DIR="$(cd "$(dirname "$0")/.." && pwd)"
ENV="$DIR/.env"
[[ -f "$ENV" ]] || { echo "falta $ENV" >&2; exit 1; }
set -a
# shellcheck disable=SC1090
source "$ENV"
set +a

CONTENEDOR="${HALSA_KEYCLOAK_CONTENEDOR:?HALSA_KEYCLOAK_CONTENEDOR}"
SERVIDOR="${HALSA_KEYCLOAK_URL_INTERNA:-http://localhost:8080}"
DOMINIO="${HALSA_PRUEBAS_DOMINIO:-pruebas.halsa.invalid}"
REALM_PERSONAL="halsa"
REALM_PACIENTES="halsa-pacientes"
CONFIG="/tmp/kcadm-halsa-pruebas-$$.config"

[[ "$DOMINIO" =~ ^[a-z0-9.-]+$ ]] || { echo "HALSA_PRUEBAS_DOMINIO no parece un dominio" >&2; exit 1; }
docker inspect "$CONTENEDOR" >/dev/null 2>&1 || { echo "no existe el contenedor $CONTENEDOR" >&2; exit 1; }

# usuario | sub | realm | roles de halsa-api (coma) | atributos (k=v;k=v)
# Los atributos son los que lee TenantContextResolver: actor_tipo + tenant_id (ARL) o ips_id (IPS); los médicos, además, su
# documento y su RETHUS. Los que aparecen en sembrar.sql (c002 y a003) coinciden con la fila de incapacidades y dictámenes.
USUARIOS=(
    "analista.a|01999f00-0000-7000-8000-00000000a001|$REALM_PERSONAL|analista-arl|actor_tipo=arl;tenant_id=PRUEBA_A"
    "laboral.a|01999f00-0000-7000-8000-00000000a002|$REALM_PERSONAL|medico-laboral|actor_tipo=arl;tenant_id=PRUEBA_A;tipo_documento=CC;numero_documento=99000001;rethus=RM-PRUEBA-0003"
    "calificador.a|01999f00-0000-7000-8000-00000000a003|$REALM_PERSONAL|medico-calificador,clinico:emergencia|actor_tipo=arl;tenant_id=PRUEBA_A;tipo_documento=CC;numero_documento=99000002;rethus=RM-PRUEBA-0001"
    "privacidad.a|01999f00-0000-7000-8000-00000000a004|$REALM_PERSONAL|oficial-privacidad|actor_tipo=arl;tenant_id=PRUEBA_A"
    "admin.a|01999f00-0000-7000-8000-00000000a005|$REALM_PERSONAL|administrador|actor_tipo=arl;tenant_id=PRUEBA_A"
    "analista.b|01999f00-0000-7000-8000-00000000b001|$REALM_PERSONAL|analista-arl|actor_tipo=arl;tenant_id=PRUEBA_B"
    "laboral.b|01999f00-0000-7000-8000-00000000b002|$REALM_PERSONAL|medico-laboral|actor_tipo=arl;tenant_id=PRUEBA_B;tipo_documento=CC;numero_documento=99000003;rethus=RM-PRUEBA-0004"
    "privacidad.b|01999f00-0000-7000-8000-00000000b003|$REALM_PERSONAL|oficial-privacidad|actor_tipo=arl;tenant_id=PRUEBA_B"
    "coordinador.ips1|01999f00-0000-7000-8000-00000000c001|$REALM_PERSONAL|coordinador-ips|actor_tipo=ips;ips_id=990000000001"
    "medico.ips1|01999f00-0000-7000-8000-00000000c002|$REALM_PERSONAL|medico-ips|actor_tipo=ips;ips_id=990000000001;tipo_documento=CC;numero_documento=80000222;rethus=RM-PRUEBA-0002"
    "coordinador.ips2|01999f00-0000-7000-8000-00000000c003|$REALM_PERSONAL|coordinador-ips|actor_tipo=ips;ips_id=990000000002"
    "medico.ips2|01999f00-0000-7000-8000-00000000c004|$REALM_PERSONAL|medico-ips|actor_tipo=ips;ips_id=990000000002;tipo_documento=CC;numero_documento=80000333;rethus=RM-PRUEBA-0005"
    "paciente.1|01999f00-0000-7000-8000-00000000d001|$REALM_PACIENTES||"
    "paciente.2|01999f00-0000-7000-8000-00000000d002|$REALM_PACIENTES||"
    "paciente.3|01999f00-0000-7000-8000-00000000d003|$REALM_PACIENTES||"
)

kcadm() { docker exec -i "$CONTENEDOR" /opt/keycloak/bin/kcadm.sh "$@" --config "$CONFIG"; }
limpiar() { docker exec "$CONTENEDOR" rm -f "$CONFIG" >/dev/null 2>&1 || true; }
trap limpiar EXIT

conectar() {
    local usuario clave
    read -rp "Usuario administrador del realm master: " usuario
    read -rsp "Contraseña: " clave
    echo
    KC_CLI_PASSWORD="$clave" docker exec -i -e KC_CLI_PASSWORD "$CONTENEDOR" /opt/keycloak/bin/kcadm.sh config credentials \
        --server "$SERVIDOR" --realm master --user "$usuario" --config "$CONFIG" >/dev/null
    for realm in "$REALM_PERSONAL" "$REALM_PACIENTES"; do
        kcadm get "realms/$realm" --fields realm >/dev/null 2>&1 || { echo "el realm $realm no existe (importar-realms.sh)" >&2; exit 1; }
    done
}

# Devuelve el id del usuario con ese nombre exacto, o nada.
id_de() { kcadm get users -r "$2" -q "username=$1" -q exact=true --fields id --format csv --noquotes; }

listar() {
    local fila usuario sub realm id correo
    printf '%-18s %-38s %-16s %s\n' USUARIO SUB REALM ESTADO
    for fila in "${USUARIOS[@]}"; do
        IFS='|' read -r usuario sub realm _ _ <<< "$fila"
        correo="$usuario@$DOMINIO"
        id="$(id_de "$correo" "$realm")"
        if [[ -z "$id" ]]; then
            printf '%-18s %-38s %-16s %s\n' "$usuario" "$sub" "$realm" "no existe"
        elif [[ "$id" == "$sub" ]]; then
            printf '%-18s %-38s %-16s %s\n' "$usuario" "$sub" "$realm" "OK"
        else
            printf '%-18s %-38s %-16s %s\n' "$usuario" "$sub" "$realm" "EXISTE CON OTRO ID ($id)"
        fi
    done
}

# JSON de texto: escapa \ y ", lo único que puede traer la contraseña elegida por quien opera.
json_texto() { local t="${1//\\/\\\\}"; printf '%s' "${t//\"/\\\"}"; }

crear() {
    local temporal fila usuario sub realm roles atributos correo id rol par acciones cuerpo attrs roles_json
    local -a roles_lista pares

    read -rsp "Contraseña temporal para todos los usuarios de prueba (mínimo 12 caracteres): " temporal
    echo
    [[ ${#temporal} -ge 12 ]] || { echo "la contraseña temporal debe tener al menos 12 caracteres" >&2; exit 1; }

    for fila in "${USUARIOS[@]}"; do
        IFS='|' read -r usuario sub realm roles atributos <<< "$fila"
        correo="$usuario@$DOMINIO"

        if [[ -n "$(id_de "$correo" "$realm")" ]]; then
            echo "==> $correo ya existe en $realm: no se toca"
            continue
        fi

        attrs=""
        if [[ -n "$atributos" ]]; then
            IFS=';' read -r -a pares <<< "$atributos"
            for par in "${pares[@]}"; do
                attrs+="${attrs:+,}\"${par%%=*}\":[\"${par#*=}\"]"
            done
        fi
        roles_json=""
        if [[ -n "$roles" ]]; then
            IFS=',' read -r -a roles_lista <<< "$roles"
            for rol in "${roles_lista[@]}"; do
                roles_json+="${roles_json:+,}\"$rol\""
            done
        fi
        if [[ "$realm" == "$REALM_PERSONAL" ]]; then
            acciones='["UPDATE_PASSWORD","CONFIGURE_TOTP"]'
        else
            acciones='["UPDATE_PASSWORD"]'
        fi

        # El REST normal (create users) ignora el id que se le pida; partialImport sí lo respeta. Es lo único que garantiza
        # que el sub coincida con el de sembrar.sql. Un usuario por llamada, para saber cuál falló.
        cuerpo="$(printf '{"ifResourceExists":"FAIL","users":[{"id":"%s","username":"%s","email":"%s","enabled":true,"emailVerified":true,"attributes":{%s},"requiredActions":%s,"credentials":[{"type":"password","value":"%s","temporary":true}],"clientRoles":{"halsa-api":[%s]}}]}'             "$sub" "$correo" "$correo" "$attrs" "$acciones" "$(json_texto "$temporal")" "$roles_json")"

        echo "==> creando $correo en $realm"
        printf '%s' "$cuerpo" | kcadm create partialImport -r "$realm" -f - >/dev/null

        id="$(id_de "$correo" "$realm")"
        if [[ "$id" != "$sub" ]]; then
            echo "ERROR: $correo quedó con el id '$id' en vez de $sub; sembrar.sql usa el sub fijo. Se deshace y se detiene." >&2
            [[ -z "$id" ]] || kcadm delete "users/$id" -r "$realm" >/dev/null 2>&1 || true
            exit 1
        fi
    done
    unset temporal
    echo "==> OK. Cada usuario cambia la contraseña al ingresar; el personal registra además su OTP."
}

eliminar() {
    local respuesta fila usuario sub realm correo id
    echo "Se borrarán de Keycloak los usuarios de prueba (solo los de la lista, con su sub exacto)."
    read -rp "Escribe ELIMINAR para continuar: " respuesta
    [[ "$respuesta" == "ELIMINAR" ]] || { echo "cancelado" >&2; exit 1; }

    for fila in "${USUARIOS[@]}"; do
        IFS='|' read -r usuario sub realm _ _ <<< "$fila"
        correo="$usuario@$DOMINIO"
        id="$(id_de "$correo" "$realm")"
        if [[ -z "$id" ]]; then
            echo "==> $correo no existe en $realm"
        elif [[ "$id" != "$sub" ]]; then
            echo "==> $correo existe con otro id ($id): NO es de la siembra, no se toca" >&2
        else
            kcadm delete "users/$id" -r "$realm"
            echo "==> $correo borrado de $realm"
        fi
    done
    echo "==> Los pacientes que se registraron por su cuenta (no son de esta lista) se borran a mano en la consola de $REALM_PACIENTES."
}

case "${1:-}" in
    crear) conectar; crear ;;
    listar) conectar; listar ;;
    eliminar) conectar; eliminar ;;
    *) echo "uso: $0 crear|listar|eliminar" >&2; exit 2 ;;
esac

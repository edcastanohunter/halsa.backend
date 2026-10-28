# Runbook 06 — Certificados de las integraciones

> SPEC 13. Halsa tiene una CA privada para las integraciones (los cores de las ARL y los sistemas de las IPS):
>
> - **raíz:** RSA 4096, 10 años. Fuera de línea: nunca toca el VPS.
> - **intermedia "Halsa Integraciones":** ECDSA P-384, 3 años. Vive en el VPS y firma los certificados cliente.
> - **certificados cliente:** ECDSA P-256, 1 año, `CN = client_id` y `extendedKeyUsage = clientAuth`.
>
> Nginx Proxy Manager exige el certificado en `halsa-integraciones.edcastdev.com` (`ssl_verify_client on`) contra la
> cadena y la CRL de las dos CA, que `sincronizar-npm.sh` le copia. El Keycloak compartido (`auth.edcastdev.com`) liga
> el token al certificado (`cnf.x5t#S256`, RFC 8705) y la API lo compara en cada request
> (`IntegrationCertificateBindingMiddleware`).

Scripts: `deploy/pki/` (`crear-raiz.sh`, `crear-intermedia.sh`, `emitir-cliente.sh`, `revocar.sh` y `generar-crl.sh`).
Todos usan `HALSA_PKI_DIR`.

## Crear la CA (una sola vez)

### En el equipo sin red (ceremonia, dos personas)

`HALSA_PKI_DIR` es donde se genera la PKI, en el medio cifrado de la bóveda (aquí montado en `/media/boveda/`). El
script lo crea si no existe:

```bash
export HALSA_PKI_DIR=/media/boveda/halsa-pki
bash deploy/pki/crear-raiz.sh                # pide la passphrase de la raíz (AES-256)
```

La passphrase no se escribe en ningún archivo, y menos en el repo: va a las custodias (runbook 02).

El directorio `raiz/` va a las dos custodias (runbook 02). Al VPS solo viajan `raiz/raiz.crt` y `raiz/raiz.crl`.

### Instalar la intermedia en el VPS

En el VPS, como `edhunter` y sin sudo: la PKI vive en `~/.config/halsa/pki` (0700, llaves 0600, de `edhunter`), que
el worker monta (`publicar/`) y `sincronizar-npm.sh` copia a NPM.

```bash
# 1. VPS: la llave y el CSR de la intermedia. La llave no sale del VPS.
export HALSA_PKI_DIR=~/.config/halsa/pki
install -d -m 0700 ~/.config/halsa "$HALSA_PKI_DIR"
bash ~/halsa/deploy/pki/crear-intermedia.sh solicitud

# 2. Equipo sin red: firmar el CSR, que llega en una memoria USB.
HALSA_PKI_DIR=/media/boveda/halsa-pki bash deploy/pki/crear-intermedia.sh firmar /media/usb/intermedia.csr

# 3. VPS: instalar la intermedia firmada junto con la raíz pública y su CRL. Genera la CRL y publica en
#    ~/.config/halsa/pki/publicar. Luego, validar todo.
bash ~/halsa/deploy/pki/crear-intermedia.sh instalar /media/usb/intermedia.crt /media/usb/raiz.crt /media/usb/raiz.crl
bash ~/halsa/deploy/pki/verificar-certificados.sh
```

La validación completa de la PKI, lo publicado en NPM y el mTLS de punta a punta está en deploy-quadlets/README,
"Validación de los certificados".

### Cron de la CRL (VPS)

En el **crontab de `edhunter`** (`crontab -e`, junto a los backups del runbook 04 y con su `XDG_RUNTIME_DIR`). La CRL
de la intermedia vale 10 días y se regenera cada 7; después se valida:

```cron
0 4 * * 1    { HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/generar-crl.sh && bash ~/halsa/deploy/scripts/sincronizar-npm.sh && HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/verificar-certificados.sh; } >> ~/halsa/crl.log 2>&1
```

1. Abrir el crontab de edhunter
```bash
ssh edhunter@<vps>
crontab -e
# La primera vez te pide escoger un editor. Elige nano, que es el más sencillo.
```

2. Pegar esto en el archivo

```cron
XDG_RUNTIME_DIR=/run/user/1001
CRON_TZ=America/Bogota
```
# CRL de la intermedia: lunes 04:00 (vale 10 días, se regenera cada 7)
```cron
0 4 * * 1    { HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/generar-crl.sh && bash ~/halsa/deploy/scripts/sincronizar-npm.sh && HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/verificar-certificados.sh; } >> ~/halsa/crl.log 2>&1
```

- XDG_RUNTIME_DIR es obligatorio. sincronizar-npm.sh usa podman para entrar a NPM, y sin esa variable podman no encuentra tus contenedores rootless desde cron. El 1001 tiene que coincidir con lo que te devuelva id -u edhunter.
- Si después agregas los backups del runbook 04, van en este mismo archivo. Esas dos primeras líneas se escriben una sola vez, arriba de todo.
- En nano se guarda con Ctrl+O y Enter, y se sale con Ctrl+X. Al salir, crontab muestra installing new crontab.

3. Comprobar
```bash
crontab -l          # muestra lo que quedó programado
```
Antes de esperar al lunes, ejecútalo una vez a mano para ver que funciona:

{ HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/generar-crl.sh && bash ~/halsa/deploy/scripts/sincronizar-npm.sh && HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/verificar-certificados.sh; } 2>&1 | tee -a ~/halsa/crl.log

Desde ahí, cada ejecución del cron deja su resultado en ~/halsa/crl.log (tail ~/halsa/crl.log).

Cómo leer la línea

┌─────────────────────────┬───────────────────────────────────────────────────────────────────┐
│          Parte          │                             Significa                             │
├─────────────────────────┼───────────────────────────────────────────────────────────────────┤
│ 0 4 * * 1               │ minuto 0, hora 4, cualquier día del mes, cualquier mes, lunes (1) │
├─────────────────────────┼───────────────────────────────────────────────────────────────────┤
│ { a && b && c; }        │ ejecuta los tres en orden y se detiene en el primero que falle    │
├─────────────────────────┼───────────────────────────────────────────────────────────────────┤
│ >> ~/halsa/crl.log 2>&1 │ agrega la salida y los errores al log                             │
└─────────────────────────┴───────────────────────────────────────────────────────────────────┘

Dos cosas que revisé:
- Pasos previos: la intermedia ya tiene que estar instalada (runbook 06) y NPM unido a halsa_borde. Si falta algo, el script lo dice en el log.
- Passphrase: cron no puede pedir contraseñas, pero aquí no hace falta. La llave de la intermedia se crea sin passphrase y solo la raíz tiene; por eso puede correr desatendido.


> **Una CRL vencida cierra todas las integraciones** (NPM falla cerrado). El Worker alerta 48 h antes
> (`CrlPorVencer`, runbook 07). La CRL de la **raíz** vale 365 días y se reemite en la ceremonia anual:
> `bash deploy/pki/generar-crl.sh --raiz` en el equipo sin red, y luego se copia `raiz.crl` al VPS y se ejecuta
> `generar-crl.sh`.

## Alta de una integración

### 1. Certificado

Lo recomendado es que la integración genere su llave y entregue un CSR ECDSA P-256:

```bash
# Del lado de la integración:
openssl genpkey -algorithm EC -pkeyopt ec_paramgen_curve:P-256 -out int-arl-ARL_X.key
openssl req -new -key int-arl-ARL_X.key -subj "/CN=int-arl-ARL_X" -out int-arl-ARL_X.csr
```

```bash
# En el VPS:
HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/emitir-cliente.sh int-arl-ARL_X /tmp/int-arl-ARL_X.csr
```

El script fuerza `CN=int-arl-ARL_X` e imprime la huella `x5t#S256` y el certificado en base64 DER para Keycloak. Se
entregan `clientes/int-arl-ARL_X.crt` y `clientes/int-arl-ARL_X.cadena.pem`. Si no hay CSR, el script genera la llave
(`clientes/<id>.key`). En ese caso se entrega por un canal seguro y luego se **borra** del VPS.

### 2. Client en el Keycloak compartido (realm `halsa`)

Por la consola de `auth.edcastdev.com` o con `kcadm.sh` dentro de su contenedor. Primero, una sesión de kcadm. La
contraseña va por `KC_CLI_PASSWORD`, nunca en la línea de comandos:

```bash
K=keykloack-keycloak-1; CFG=/tmp/kcadm-halsa.config
read -rp "Admin de master: " U; read -rsp "Contraseña: " P; echo
KC_CLI_PASSWORD="$P" podman exec -e KC_CLI_PASSWORD $K /opt/keycloak/bin/kcadm.sh config credentials --server http://localhost:8080 --realm master --user "$U" --config $CFG; unset P
kc() { podman exec -i $K /opt/keycloak/bin/kcadm.sh "$@" --config $CFG; }
```

```bash
kc create clients -r halsa -f - <<EOF
{
  "clientId": "int-arl-ARL_X",
  "name": "Integración ARL_X",
  "enabled": true,
  "clientAuthenticatorType": "client-jwt",
  "publicClient": false,
  "standardFlowEnabled": false,
  "directAccessGrantsEnabled": false,
  "serviceAccountsEnabled": true,
  "attributes": {
    "use.jwks.url": "false",
    "jwt.credential.certificate": "<base64 DER de emitir-cliente.sh>",
    "token.endpoint.auth.signing.alg": "ES256",
    "tls.client.certificate.bound.access.tokens": "true"
  },
  "protocolMappers": [
    { "name": "halsa-api audience", "protocol": "openid-connect", "protocolMapper": "oidc-audience-mapper",
      "config": { "included.client.audience": "halsa-api", "access.token.claim": "true", "id.token.claim": "false" } },
    { "name": "actor_tipo hardcoded", "protocol": "openid-connect", "protocolMapper": "oidc-hardcoded-claim-mapper",
      "config": { "claim.name": "actor_tipo", "claim.value": "integracion", "jsonType.label": "String", "access.token.claim": "true" } },
    { "name": "tenant_id hardcoded", "protocol": "openid-connect", "protocolMapper": "oidc-hardcoded-claim-mapper",
      "config": { "claim.name": "tenant_id", "claim.value": "ARL_X", "jsonType.label": "String", "access.token.claim": "true" } }
  ]
}
EOF
# Rol del service account y scope mapping de halsa-api:
kc add-roles -r halsa --uusername service-account-int-arl-arl_x --cclientid halsa-api --rolename integracion-arl
podman exec $K rm -f $CFG          # al terminar
```

Para una IPS: `int-ips-<REPS>`, con el mapper `ips_id` en lugar de `tenant_id` y el rol `integracion-ips`.

### 3. Lo que se le entrega a la integración

- su certificado y la cadena (`.crt` y `.cadena.pem`);
- el token endpoint: `POST https://halsa-integraciones.edcastdev.com/auth/realms/halsa/protocol/openid-connect/token`, con mTLS y
  `client_credentials`, autenticado con `client_assertion_type=urn:ietf:params:oauth:client-assertion-type:jwt-bearer`;
  el `client_assertion` es un JWT ES256 firmado con su llave, con `iss = sub = client_id`, `aud =
  https://auth.edcastdev.com/realms/halsa`, `jti` único y `exp` ≤ 60 s;
- que **todas** las llamadas a la API van por `https://halsa-integraciones.edcastdev.com/v1/...` con el **mismo**
  certificado. El token no sirve con otro certificado (`401 Auth.CertificadoNoVinculado`) ni por `halsa-api`;
- TLS 1.2 (solo ECDHE-AESGCM) o 1.3.

## Revocar un certificado cliente

Por compromiso, fin del contrato o reemplazo:

```bash
HALSA_PKI_DIR=~/.config/halsa/pki bash ~/halsa/deploy/pki/revocar.sh int-arl-ARL_X keyCompromise
bash ~/halsa/deploy/scripts/sincronizar-npm.sh       # copia la CRL nueva a NPM, valida y recarga
```

Desde la recarga, NPM rechaza ese certificado con `400` y nada llega a la API. Además,
deshabilitar el client en Keycloak si la integración termina.

## Renovar un certificado cliente (anual)

Emitir uno nuevo con `emitir-cliente.sh` (mismo `client_id`) y cambiar `jwt.credential.certificate` en Keycloak por el
nuevo. Hay que coordinar el corte: los tokens vigentes quedan ligados al certificado anterior, pero duran 5 minutos.
Después, revocar el anterior con motivo `superseded`.

## Compromiso o pérdida de la intermedia

1. **Equipo sin red:** revocar la intermedia con la raíz y reemitir la CRL de la raíz:
   ```bash
   export HALSA_PKI_DIR=/media/boveda/halsa-pki
   openssl ca -config "$HALSA_PKI_DIR/raiz/openssl.cnf" -revoke intermedia-comprometida.crt -crl_reason keyCompromise
   bash deploy/pki/generar-crl.sh --raiz
   ```
2. **VPS:** mover `~/.config/halsa/pki/intermedia` aparte y crear una intermedia nueva (los tres pasos de "Instalar la
   intermedia en el VPS"), con la `raiz.crl` nueva.
3. Reemitir **todos** los certificados cliente con la intermedia nueva y actualizar cada client en Keycloak.
4. Ejecutar `sincronizar-npm.sh`. Desde ese momento, todo certificado de la intermedia anterior se rechaza.

## Desarrollo

`docker/nginx-dev/generar-pki.sh` usa estos mismos scripts con `HALSA_PKI_DEV=1` (la raíz sin passphrase) y emite los
certificados de `int-arl-ARL_A` e `int-ips-110011234501`. Ver `docker/nginx-dev/README.md`.

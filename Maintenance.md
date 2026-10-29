## Desde tu máquina, en la raíz del repo:
scp deploy-quadlets/halsa-datos.network deploy-quadlets/halsa-postgres.container \
    deploy-quadlets/halsa-api.container deploy-quadlets/halsa-worker.container \
    deploy-quadlets/halsa-migrador.container alcast1:halsa/deploy-quadlets/

## En el VPS, como edhunter:
systemctl --user stop halsa-bff halsa-api halsa-worker halsa-postgres
podman network rm halsa_datos
bash ~/halsa/deploy-quadlets/instalar.sh                 # copia las unidades, daemon-reload, arranca postgres/redis/antivirus/seq
systemctl --user restart halsa-datos-network.service     # por si quedó "active": vuelve a crear la red con el IPRange
podman network inspect halsa_datos --format '{{range .Subnets}}{{.Subnet}} {{.LeaseRange}}{{end}}'
podman inspect halsa-postgres --format '{{(index .NetworkSettings.Networks "halsa_datos").IPAddress}}'   # 172.30.2.10
~/halsa/deploy/scripts/desplegar.sh v1.0.7

Si podman inspect no muestra 172.30.2.10, es porque postgres arrancó antes de que se recreara la red. Ejecuta systemctl --user restart halsa-postgres y repite la comprobación.

Cuando termine, desplegar.sh debería decir OK: v1.0.7 desplegado y Healthy.

Este es el segundo cambio en deploy-quadlets/ que hay que llevar a mano. Sigue en pie mi oferta de que el CI envíe esa carpeta y ejecute instalar.sh cuando cambie.



-----

## Check Halsa Endpoint:

https://halsa-api.alcast.dev/api siempre devuelve 404. Eso es a propósito: la configuración de NPM de Halsa (deploy/npm/sitios/api.conf) devuelve 404 para cualquier ruta que no esté en su lista, y /api no está. Estas son las comprobaciones que sí sirven para los tres sitios. Usé tu dominio alcast.dev.

1. halsa-api

# Health: la API está viva detrás de NPM → 200
curl -sS -w '\n%{http_code}\n' https://halsa-api.alcast.dev/health/vivo

# Una ruta /v1 sin token → 401: NPM llega a la API y la autenticación funciona
curl -sS -o /dev/null -w '%{http_code}\n' https://halsa-api.alcast.dev/v1/casos

# Rutas bloqueadas en este host → 404 las dos
curl -sS -o /dev/null -w '%{http_code}\n' https://halsa-api.alcast.dev/v1/integraciones/x
curl -sS -o /dev/null -w '%{http_code}\n' https://halsa-api.alcast.dev/v1/paciente/x

2. halsa-integraciones (mTLS)

# Sin certificado de cliente → 400 de NPM; nada llega a la API
curl -sS -o /dev/null -w '%{http_code}\n' https://halsa-integraciones.alcast.dev/v1/integraciones/x

# Con un certificado emitido por la CA de Halsa (runbook 06) → ya no da 400 (401/404 según la ruta)
curl -sS -o /dev/null -w '%{http_code}\n' \
--cert cliente.crt --key cliente.key \
https://halsa-integraciones.alcast.dev/v1/integraciones/x

3. halsa-pacientes (BFF)

# Health del BFF → 200
curl -sS -w '\n%{http_code}\n' https://halsa-pacientes.alcast.dev/health/vivo

# Login → 302 hacia Keycloak (revisa que Location apunte a tu realm halsa-pacientes)
curl -sS -o /dev/null -w '%{http_code} %{redirect_url}\n' https://halsa-pacientes.alcast.dev/paciente/bff/login

# Usuario sin sesión → 401
curl -sS -o /dev/null -w '%{http_code}\n' https://halsa-pacientes.alcast.dev/paciente/bff/usuario

TLS y cabeceras

# Comprueba que aparezcan una sola vez strict-transport-security y las demás cabeceras de seguridad
curl -sSI https://halsa-api.alcast.dev/health/vivo

Qué significa cada fallo:
- 502: NPM no llega al contenedor. Revisa que NPM siga en halsa_borde.
- 404 en /health/vivo: el Advanced de ese host no carga el archivo de Halsa.
- Error de certificado: Let's Encrypt todavía no emitió el certificado.

Sobre el dominio: la documentación usa edcastdev.com. Si en producción usas alcast.dev, el .env debe tenerlo en HALSA_HOST_API, HALSA_HOST_INTEGRACIONES, HALSA_HOST_PACIENTES y HALSA_KEYCLOAK_URL. Si no coincide, el login del BFF redirige a una dirección equivocada.




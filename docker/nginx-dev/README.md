# Nginx and the CA in development (SPEC 13)

In production the edge is the VPS's Nginx Proxy Manager (NPM), which includes Halsa's files from `deploy/npm`
(`deploy/scripts/sincronizar-npm.sh`). In development, `docker compose up` starts a plain Nginx that plays NPM's part
and includes **the same files, at the same paths**. It also brings a development CA. This lets you test mTLS,
certificate-bound tokens, route filtering and the `X-Forwarded-For` rewrite end to end.

| File | Plays the part of |
| --- | --- |
| `nginx.conf` | NPM's main config: the TLS policy, the resolver, `http_top.conf` including `deploy/npm/http.conf` |
| `servidores.conf` | NPM's three proxy hosts: `listen`, `server_name`, certificate, and the `include` of the "Advanced" field |
| `arrancar.sh` | `sincronizar-npm.sh`: renders `deploy/npm` (only `HALSA_*`) to `/data/nginx/custom/halsa` |

The API and the BFF still run on the host (`dotnet run`), and Nginx reaches them through `host.docker.internal`.
Keycloak stays on `http://localhost:8080`; there is no `auth.localhost`, because in production Keycloak is a
shared instance with its own proxy host.

| Host | Goes to |
| --- | --- |
| `https://api.localhost` | the API (`localhost:5600`) |
| `https://integraciones.localhost` | the API and the Keycloak token endpoint, **mTLS required** |
| `https://pacientes.localhost` | the BFF (`localhost:5610`) |

`*.localhost` resolves to `127.0.0.1` in curl and browsers without touching `hosts`.

## First run

```bash
bash docker/nginx-dev/generar-pki.sh          # dev CA, int-* certificates, *.localhost certificate, keycloak.env
docker compose up -d
dotnet run --project src/Halsa.Backend.Api --launch-profile mtls     # Integraciones:ExigirCertificado=true
```

- **The PKI.** `generar-pki.sh` uses the same scripts as production (`deploy/pki`) and writes everything to
  `docker/nginx-dev/pki/`, which git ignores. The development server certificate is signed by the dev root, so curl
  needs `--cacert docker/nginx-dev/pki/raiz/raiz.crt` (or a browser that trusts it).
- **Re-importing the realm.** The `halsa` realm reads the client certificates (`keycloak.env`) **only when it is
  imported**. If Keycloak already had the realm, or you regenerated the PKI, recreate its volume:
  `docker compose rm -sf halsa.backend.identity && docker volume rm halsabackend_halsa.backend-identity`.
- **Debugging without certificates.** `int-arl-ARL_A-secreto` (`client_secret` = `int-arl-ARL_A-secreto-secret`)
  exists only in the development realm. Its tokens carry no `cnf`. Use it against `http://localhost:5600`, with the
  `http` or `api` profile (`ExigirCertificado=false`).
- **Podman.** `host.docker.internal` resolves to the host's LAN IP, not its loopback. An API listening only on
  `localhost:5600` is unreachable from Nginx (502). Run it with `ASPNETCORE_URLS=http://0.0.0.0:5600`, or run the built
  API in a container on the compose network and set `HALSA_DEV_UPSTREAM_API` (`.env.example`). The compose project
  name `halsa.backend` becomes `halsabackend` in volume and network names.
- **Upstreams are resolved per request** (variables in `proxy_pass`, as in NPM). If the API isn't running, its
  routes answer 502 but Nginx starts anyway.
- **Trusted proxy.** `appsettings.Development.json` trusts loopback and the Docker networks
  (`Red:ProxyConfiable`). Only from there are `X-Forwarded-*` and `X-Client-Cert` accepted.

## Acceptance criteria *(dev)*

```bash
CA="--cacert docker/nginx-dev/pki/raiz/raiz.crt"
C=docker/nginx-dev/pki/clientes
# furat.json: a valid FURAT body, e.g. components.examples.FuratCaidaAltura from docs/api/atel-api.openapi.yaml

# 1. Without a client certificate: Nginx rejects it (400 "No required SSL certificate"), nothing reaches the API.
curl -i $CA https://integraciones.localhost/v1/integraciones/furat

# 2. private_key_jwt token through integraciones, with cnf.x5t#S256, and the FURAT → 202.
TOKEN=$(bash docker/nginx-dev/token.sh int-arl-ARL_A)
echo "$TOKEN" | cut -d. -f2 | base64 -d 2>/dev/null | grep -o '"cnf":{[^}]*}'
curl -i $CA --cert $C/int-arl-ARL_A.crt --key $C/int-arl-ARL_A.key \
     -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" \
     -H "Idempotency-Key: $(uuidgen)" --data @furat.json \
     https://integraciones.localhost/v1/integraciones/furat

# 3. The same token with another certificate → 401 Auth.CertificadoNoVinculado.
curl -i $CA --cert $C/int-ips-110011234501.crt --key $C/int-ips-110011234501.key \
     -H "Authorization: Bearer $TOKEN" https://integraciones.localhost/v1/integraciones/suscripciones-webhook

# 4. The same token on api.localhost (no mTLS) → 401 (and the route is 404 anyway; try an allowed one).
curl -i $CA -H "Authorization: Bearer $TOKEN" https://api.localhost/v1/casos

# 5. Revoke ARL_A, regenerate the CRL and reload Nginx → handshake rejected (400 "certificate revoked").
HALSA_PKI_DEV=1 HALSA_PKI_DIR=docker/nginx-dev/pki bash deploy/pki/revocar.sh int-arl-ARL_A
docker compose exec halsa.backend.nginx nginx -s reload
# (bash docker/nginx-dev/generar-pki.sh --forzar restores everything; re-import the realm)

# 6. A forged X-Client-Cert sent to api.localhost never reaches the API (Nginx clears it).
curl -i $CA -H "X-Client-Cert: falso" -H "Authorization: Bearer $TOKEN" https://api.localhost/v1/casos

# 7. Routes filtered by Nginx → 404.
curl -i $CA https://api.localhost/v1/integraciones/furat
curl -i $CA https://api.localhost/v1/paciente/mis-casos

# 8. The audit row records the client's IP (X-Forwarded-For from Nginx), not the Nginx container's.
# 9. A client-sent "X-Forwarded-For: 1.2.3.4" does not reach the audit row either: Nginx rewrites it.
curl -i $CA --cert $C/int-arl-ARL_A.crt --key $C/int-arl-ARL_A.key -H "X-Forwarded-For: 1.2.3.4"      -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json"      -H "Idempotency-Key: $(uuidgen)" --data @furat.json https://integraciones.localhost/v1/integraciones/furat
```

> **Note.** With `ssl_verify_client on`, Nginx completes the TLS handshake and rejects with **HTTP 400** (internal
> codes 495/496) when the certificate is missing, invalid or revoked. Nothing reaches the API or Keycloak. This
> is how Nginx works: it has no option to abort the handshake itself.

# SPEC 14 — Despliegue continuo desde el CI por SSH, sin registry

> **Estado:** Implementado
> **Depende de:** SPEC 13
> **Fecha:** 2026-10-15
> **Objetivo:** Que cada push a `main` con los tests, la cobertura y las validaciones en verde, tras una aprobación en GitHub, construya las imágenes en el runner y las envíe al VPS por SSH junto con `deploy/`, y despliegue con `desplegar.sh`, sin GHCR y sin que una persona entre al VPS.

---

## Por qué existe esta spec

SPEC 13 dejó un flujo de dos manos: un tag `v*` publica las imágenes en GHCR (`release.yml`), y después una persona entra al VPS, hace *pull* y ejecuta `desplegar.sh`. Esta spec adopta el modelo de otro proyecto del mismo dueño (TiendaMa API): el runner construye, empaqueta las imágenes como tarball y las envía al VPS por SSH, donde se cargan y se despliegan. No hay registry.

Esto **revierte tres decisiones de SPEC 13**: "imágenes en GHCR", "No: SSH desde el CI" y "el VPS hace *pull*". Se conserva "No: construir en el VPS". Todo lo demás de SPEC 13 sigue igual: el Migrador, el rollback de `desplegar.sh`, `sincronizar-npm.sh`, los secretos en archivos y el borde con NPM.

El modelo de TiendaMa se adapta en cuatro puntos, porque Halsa guarda datos clínicos:

- El CI entra con un usuario sin shell.
- El host del VPS está fijado en `known_hosts`.
- El VPS no guarda ningún token de GitHub.
- Hay una aprobación humana antes de tocar producción.

---

## Alcance

**Entra:**

- `ci.yml` gana:
  - la exigencia de cobertura sobre `Halsa.Backend.Domain` y `Halsa.Backend.Application`;
  - un job `validar-despliegue` (shellcheck, `docker compose config`, actionlint);
  - un job `desplegar` que solo corre en `main`, con el environment `produccion`.
- Se elimina `.github/workflows/release.yml`, y con él la publicación en GHCR.
- Las imágenes se llaman `localhost/halsa-backend`, `localhost/halsa-bff-paciente` y `localhost/halsa-postgres`, con `pull_policy: never` en el compose de producción.
- La versión de cada despliegue es `v1.0.<run_number>`.
- `halsa-postgres` se construye y se envía solo si el push toca `deploy/postgres/**`, o con el input `incluir_postgres` de `workflow_dispatch`.
- Un script nuevo, `deploy/scripts/recibir-despliegue.sh`, se instala en el VPS como `/usr/local/sbin/halsa-recibir-despliegue`. Es el único comando que la llave del CI puede ejecutar. Hace cinco cosas:
  - recibe el paquete;
  - verifica las sumas;
  - carga las imágenes;
  - sincroniza `deploy/` conservando `.env`;
  - llama a `desplegar.sh`.
- `desplegar.sh` deja de hacer *pull*. En su lugar verifica que las imágenes del tag existan en el VPS. Sigue sirviendo a mano para un rollback a un tag que todavía esté cargado.
- El usuario `halsa-deploy` en el VPS, con `command=` forzado en `authorized_keys` y `sudo` limitado a ese único script.
- Se actualizan los runbooks 01 (aprovisionamiento) y 03 (despliegue y transición desde GHCR).
- El README se actualiza en dos puntos:
  - la sección de despliegue;
  - una sección nueva con **todas** las variables de entorno de producción (`deploy/.env.ejemplo`), las de los scripts y los secrets de GitHub.
- SPEC 13 recibe una línea en Decisiones que apunta a esta spec.

**Fuera de alcance (para specs futuras):**

- Borrar los paquetes ya publicados en GHCR (es una tarea manual, se menciona en el runbook).
- Un entorno de *staging* separado y despliegues por entorno.
- Recrear `postgres` automáticamente cuando cambia su imagen. Sigue siendo manual (runbook 03).
- Crear tags git o *releases* de GitHub por cada despliegue.
- Firmar las imágenes, SBOM y escaneo de vulnerabilidades (ya estaban fuera en SPEC 13).
- Subir la cobertura a más del 80% o medir otras capas.
- Despliegues sin corte (*blue/green*) o en varios VPS.

---

## Modelo de datos

Esta spec no introduce tablas ni estructuras de dominio. Introduce tres contratos de despliegue.

**1. El paquete que el runner envía por la entrada estándar de SSH.** Es un tar sin comprimir:

```
paquete.tar
  manifiesto.sha256     sha256sum de los otros archivos
  imagenes.tar.gz       docker save de localhost/halsa-backend:<tag> y localhost/halsa-bff-paciente:<tag>, gzip
  postgres.tar.gz       (opcional) docker save de localhost/halsa-postgres:17 y :17-<tag>, gzip
  deploy.tar.gz         git archive <sha> deploy/ (solo archivos versionados: nunca .env)
```

**2. La invocación.** La única forma válida es:

```bash
ssh -i llave halsa-deploy@$VPS_HOST "desplegar v1.0.<run_number>" < paquete.tar
```

`recibir-despliegue.sh` lee `SSH_ORIGINAL_COMMAND`. Rechaza con exit `2` cualquier cosa que no sea `desplegar <tag>`, donde el tag cumple la misma regex de `desplegar.sh`.

**3. El estado en el VPS:**

| Ruta | Dueño | Qué es |
| --- | --- | --- |
| `/usr/local/sbin/halsa-recibir-despliegue` | root 0755 | Copia instalada de `deploy/scripts/recibir-despliegue.sh` (runbook 01). |
| `/etc/sudoers.d/halsa-deploy` | root 0440 | `halsa-deploy ALL=(root) NOPASSWD: /usr/local/sbin/halsa-recibir-despliegue` y `Defaults!/usr/local/sbin/halsa-recibir-despliegue env_keep += "SSH_ORIGINAL_COMMAND"`. |
| `~halsa-deploy/.ssh/authorized_keys` | halsa-deploy 0600 | `command="sudo /usr/local/sbin/halsa-recibir-despliegue",no-pty,no-port-forwarding,no-agent-forwarding,no-X11-forwarding ssh-ed25519 … ci-halsa`. |
| `/var/lib/halsa/entregas/<tag>/` | root 0700 | Zona de recepción. Se borra cuando el despliegue termina bien. |
| `/var/lock/halsa-despliegue.lock` | root | `flock`: dos despliegues nunca corren a la vez. |
| `/opt/halsa/deploy/` | root | Igual que hoy, pero sincronizado por `rsync -a --delete --exclude /.env` en vez de un sparse checkout. |

**Retención:** después de un despliegue exitoso se conservan las **5** versiones más recientes de `localhost/halsa-backend` y `localhost/halsa-bff-paciente` (por fecha de creación), y siempre la que está en `HALSA_TAG` y la anterior. Las demás se borran.

**Variables nuevas o que cambian:**

| Dónde | Variable | Cambio |
| --- | --- | --- |
| `deploy/.env.ejemplo` | `HALSA_GHCR_ORG` | Se elimina. |
| `deploy/.env.ejemplo` | `HALSA_TAG` | Sin cambio de forma (`v1.0.<n>`). La sigue fijando `desplegar.sh`. |
| `recibir-despliegue.sh` | `HALSA_PAQUETE_MAX_MB` | Nueva. Por defecto `3072`. El paquete se corta con error si lo supera. |
| `recibir-despliegue.sh` | `HALSA_VERSIONES_RETENIDAS` | Nueva. Por defecto `5`. |
| `ci.yml` | `COBERTURA_MINIMA` | Nueva (`env` del workflow). Arranca en el valor que fije el paso 1 del plan. |
| GitHub, environment `produccion` | `VPS_HOST`, `VPS_USER`, `VPS_SSH_KEY`, `VPS_SSH_KNOWN_HOSTS` | Secrets nuevos. `VPS_USER` es `halsa-deploy`. `VPS_SSH_KNOWN_HOSTS` es la salida de `ssh-keyscan -t ed25519 <host>`, verificada a mano contra la huella del VPS. |

---

## Plan de implementación

1. **Medir la cobertura y crear `coverage.runsettings`** en la raíz. Coverlet incluye solo `[Halsa.Backend.Domain]*` y `[Halsa.Backend.Application]*`, en formato `cobertura`, y excluye el código generado y `[ExcludeFromCodeCoverage]`.
   - Ejecutar `dotnet test Halsa.Backend.slnx -c Release --settings coverage.runsettings --collect "XPlat Code Coverage"` y anotar el *line-rate* de cada paquete en la descripción del PR.
   - Si los dos están en 80% o más, `COBERTURA_MINIMA=0.80`.
   - Si no, `COBERTURA_MINIMA` es el menor de los dos, redondeado hacia abajo al punto porcentual, y queda un `# TODO(cobertura)` en `ci.yml` con la meta de 0.80.

2. **Agregar la cobertura a `ci.yml`.**
   - El paso `Test` agrega `--settings coverage.runsettings --collect "XPlat Code Coverage"`, sin cambiar qué tests corren.
   - Un paso nuevo, `Exigir cobertura`, ejecuta el script de Python del modelo TiendaMa, con dos diferencias:
     - solo mira los paquetes `Halsa.Backend.Domain` y `Halsa.Backend.Application`;
     - falla si falta cualquiera de los dos.
   - El artefacto `resultados-tests` incluye los XML de cobertura.

3. **Cambiar la concurrencia de `ci.yml`** a `cancel-in-progress: ${{ github.event_name == 'pull_request' }}`. Hoy un push nuevo a `main` cancelaría un despliegue en curso.
   - Agregar el disparador `workflow_dispatch` con el input booleano `incluir_postgres` (por defecto `false`).

4. **Agregar el job `validar-despliegue`** a `ci.yml`. Corre en PRs y en `main`:
   - `shellcheck` sobre `deploy/scripts/*.sh`, `deploy/pki/*.sh` y `deploy/keycloak/*.sh`. Los avisos que existan hoy se corrigen o se anotan con `# shellcheck disable=...` justificado.
   - `bash -n` sobre los mismos archivos.
   - `docker compose -f deploy/docker-compose.prod.yml --env-file deploy/.env.ejemplo config -q`.
   - `actionlint` sobre `.github/workflows/` (imagen `rhysd/actionlint` con versión fija).

5. **Renombrar las imágenes en `deploy/docker-compose.prod.yml`:**
   - `localhost/halsa-backend:${HALSA_TAG:?HALSA_TAG}`
   - `localhost/halsa-bff-paciente:${HALSA_TAG:?HALSA_TAG}`
   - `localhost/halsa-postgres:${HALSA_POSTGRES_TAG:-17}`

   Las tres llevan `pull_policy: never`. Quitar `HALSA_GHCR_ORG` de `.env.ejemplo` y actualizar su cabecera.

6. **Cambiar `desplegar.sh`.**
   - El paso `1/4 pull` pasa a ser `1/4 imágenes`: `docker image inspect` de `localhost/halsa-backend:<tag>` y `localhost/halsa-bff-paciente:<tag>`. Si falta alguna, restaura `HALSA_TAG` y sale con `1` ("nada cambió").
   - Actualizar la cabecera: lo llama `halsa-recibir-despliegue` desde el CI, o una persona para un rollback a un tag cargado.
   - El resto del script no cambia (Migrador, `up -d`, salud, rollback, NPM).

7. **Crear `deploy/scripts/recibir-despliegue.sh`.** Usa `set -euo pipefail`, se ejecuta como root y loguea a stderr con `==>` igual que `desplegar.sh`. En orden:
   1. Valida `SSH_ORIGINAL_COMMAND` y el tag, y toma el `flock`.
   2. Copia la entrada estándar a `/var/lib/halsa/entregas/<tag>/paquete.tar` con `head -c`, hasta `HALSA_PAQUETE_MAX_MB`.
   3. Extrae el paquete y verifica `sha256sum -c manifiesto.sha256`.
   4. Hace `docker load` de `imagenes.tar.gz` y, si viene, de `postgres.tar.gz`. Verifica que existan las dos imágenes del tag.
   5. Extrae `deploy.tar.gz` a un directorio temporal y hace `rsync -a --delete --exclude /.env` sobre `/opt/halsa/deploy/`.
   6. Ejecuta `/opt/halsa/deploy/scripts/desplegar.sh <tag>` y propaga su código de salida.
   7. Solo si terminó en `0`: aplica la retención de imágenes y borra la zona de recepción.

   Cualquier fallo en los pasos 1 a 5 sale con `1` antes de tocar `.env` o los contenedores.

8. **Agregar el job `desplegar`** a `ci.yml`:
   - `needs: [build-y-tests, nginx, validar-despliegue]`
   - `if: github.event_name != 'pull_request' && github.ref == 'refs/heads/main'`
   - `environment: produccion`
   - `concurrency: { group: despliegue-produccion, cancel-in-progress: false }`
   - `permissions: contents: read`
   - `timeout-minutes: 30`

   Pasos:
   1. Checkout con `fetch-depth: 0`. `VERSION=v1.0.${{ github.run_number }}`.
   2. Construye con `docker/build-push-action` (`load: true`, `push: false`), con la caché `gha` de cada imagen (los `scope` de `release.yml`) y las etiquetas OCI `source`, `revision` y `version`.
   3. Decide si incluye `halsa-postgres`:
      - `workflow_dispatch`: el input `incluir_postgres`;
      - `push`: `git diff --quiet ${{ github.event.before }} ${{ github.sha }} -- deploy/postgres/`;
      - si `before` es todo ceros: se incluye.
   4. Arma el paquete del modelo de datos y escribe su tamaño en `GITHUB_STEP_SUMMARY`.
   5. Escribe `VPS_SSH_KEY` en un archivo `0600` y `VPS_SSH_KNOWN_HOSTS` en un `known_hosts` propio.
   6. Ejecuta `ssh -o StrictHostKeyChecking=yes -o UserKnownHostsFile=known_hosts -i llave "$VPS_USER@$VPS_HOST" "desplegar $VERSION" < paquete.tar`.
   7. Un paso `if: always()` borra la llave.

   El resumen del job lista la versión, el commit y si incluyó PostgreSQL.

9. **Eliminar `.github/workflows/release.yml`.** Verificar que `grep -rni "ghcr\|HALSA_GHCR_ORG\|release.yml"` fuera de `specs/` no devuelva nada.

10. **Actualizar el runbook 01** (aprovisionamiento):
    - Quitar `docker login ghcr.io` y el sparse checkout. El primer contenido de `/opt/halsa/deploy` llega con el primer despliegue del CI. `.env` y los secretos se crean antes, igual que hoy.
    - Agregar `rsync` a los prerequisitos.
    - Crear `halsa-deploy` (sin contraseña, shell `/bin/sh`: `sshd` ejecuta el `command=` forzado con el shell del usuario, y con `/usr/sbin/nologin` toda conexión falla; la restricción la ponen `command=` y `sudo`), instalar el script y el sudoers (`visudo -cf`), y escribir `authorized_keys` con la línea del modelo de datos.
    - Generar la llave del CI (`ssh-keygen -t ed25519 -C ci-halsa`) y obtener `VPS_SSH_KNOWN_HOSTS`.
    - Configurar el environment `produccion` en GitHub con sus cuatro secrets, un revisor obligatorio y la rama `main` como única permitida.
    - Explicar cómo reinstalar el script en `/usr/local/sbin` cuando cambie en el repo.

11. **Actualizar el runbook 03** (despliegue):
    - El flujo normal pasa a ser: merge a `main`, aprobar el job `desplegar` en GitHub y revisar Seq.
    - El rollback manual pasa a ser `desplegar.sh <tag cargado>`. `docker image ls localhost/halsa-backend` muestra los tags disponibles.
    - Para recrear `postgres` tras un cambio de imagen, ejecutar `up -d postgres` a mano, en una ventana de mantenimiento.
    - Agregar la sección **"Transición desde GHCR"**, una sola vez:
      - `docker tag` de las imágenes `ghcr.io/<org>/halsa-*:<tag actual>` a `localhost/halsa-*:<tag actual>` (y `halsa-postgres:17`), para que el primer rollback tenga a dónde volver;
      - `docker logout ghcr.io`;
      - borrar `/opt/halsa/.git`;
      - eliminar `HALSA_GHCR_ORG` del `.env`.

12. **Actualizar el README.**
    - Reescribir *Deploying to production* con el flujo nuevo. *One-time setup* apunta al runbook 01. *Every release* pasa a ser "merge a `main` y aprobar".
    - Corregir las menciones a GHCR y a `release.yml` en *The edge and production*.
    - Agregar la sección **"Environment variables"**, en inglés como el resto del README, con cuatro tablas:
      1. cada variable de `deploy/.env.ejemplo`: nombre, para qué sirve, ejemplo, y quién la lee (compose, `sincronizar-npm.sh` o `importar-realms.sh`);
      2. las variables de los scripts (`HALSA_ESPERA_SALUD`, `HALSA_PAQUETE_MAX_MB`, `HALSA_VERSIONES_RETENIDAS`);
      3. los secrets del environment `produccion`;
      4. los nombres de archivo de `/etc/halsa/secretos/`, que **no** son variables, con un enlace al runbook 02.

13. **Enmendar SPEC 13.** En Decisiones, la línea de GHCR y SSH queda tachada (`~~…~~`), seguida de "Reemplazada por SPEC 14 (2026-10-15)".

---

## Criterios de aceptación

**Que no se rompa nada (verificables en el PR):**

- [ ] En un PR, `ci.yml` ejecuta `build-y-tests`, `nginx` y `validar-despliegue`, y `desplegar` aparece como *skipped*.
- [ ] `build-y-tests` ejecuta los mismos proyectos de test que antes. El número de tests del `.trx` no baja.
- [ ] El paso `Exigir cobertura` imprime el *line-rate* de `Halsa.Backend.Domain` y `Halsa.Backend.Application`, y pasa con `COBERTURA_MINIMA`.
- [ ] Bajar temporalmente `COBERTURA_MINIMA` a `0.99` en una rama hace fallar el paso con "Cobertura insuficiente".
- [ ] `validar-despliegue` pasa: shellcheck sin avisos, `bash -n`, `docker compose … config -q` con `.env.ejemplo` y actionlint.
- [ ] El job `nginx` sigue pasando sin cambios.
- [ ] `grep -rni "ghcr\|HALSA_GHCR_ORG\|release.yml" --exclude-dir=specs .` no devuelve resultados.
- [ ] `.github/workflows/release.yml` no existe.
- [ ] El README tiene la sección *Environment variables*, y cada variable de `deploy/.env.ejemplo` aparece en ella (verificado con un `grep` por nombre).

**El despliegue (verificables en el VPS, la primera vez durante la transición):**

- [ ] Un push a `main` deja el job `desplegar` esperando aprobación en el environment `produccion`. Sin aprobación no se abre ninguna conexión SSH.
- [ ] Aprobado, el job termina en verde. `HALSA_TAG` en `/opt/halsa/deploy/.env` es `v1.0.<run_number>`. `api` y `worker` responden `Healthy`.
- [ ] Un segundo push a `main` mientras el primero se despliega no lo cancela: queda en cola.
- [ ] `/opt/halsa/deploy/.env` conserva su contenido y sus permisos (`0600`) tras el despliegue.
- [ ] Un archivo borrado de `deploy/` en el repo desaparece de `/opt/halsa/deploy/` tras el siguiente despliegue.
- [ ] Un push que no toca `deploy/postgres/` no incluye `postgres.tar.gz`, y el contenedor `postgres` mantiene su `StartedAt`.
- [ ] `workflow_dispatch` con `incluir_postgres=true` carga `localhost/halsa-postgres:17-v1.0.<n>` en el VPS y no recrea `postgres`.
- [ ] Una versión cuya API no llega a `Healthy` deja el job en rojo, y el VPS vuelve al tag anterior (`HALSA_TAG` restaurado y `Healthy`).
- [ ] Un paquete con un byte alterado falla en `sha256sum -c` con exit `1`. No cambian `.env`, las imágenes en ejecución ni `/opt/halsa/deploy`.
- [ ] `ssh halsa-deploy@vps` sin comando, y `ssh halsa-deploy@vps "bash"`, terminan con exit `2` sin abrir un shell.
- [ ] `sudo -l -U halsa-deploy` lista solo `/usr/local/sbin/halsa-recibir-despliegue`.
- [ ] El job falla en el SSH si la huella del host no coincide con `VPS_SSH_KNOWN_HOSTS`.
- [ ] Tras 7 despliegues seguidos, `docker image ls localhost/halsa-backend` muestra como máximo 5 tags, y entre ellos están el actual y el anterior.
- [ ] `/opt/halsa/deploy/scripts/desplegar.sh <tag retenido anterior>`, ejecutado a mano como root, vuelve a esa versión y queda `Healthy`.
- [ ] `desplegar.sh v9.9.9` (un tag no cargado) sale con `1` sin cambiar `HALSA_TAG`.
- [ ] `/var/lib/halsa/entregas/` queda vacío después de un despliegue exitoso.

---

## Decisiones

- **Sí:** se reemplaza GHCR por tarballs enviados por SSH, siguiendo el modelo TiendaMa. **No:** que convivan los dos caminos, porque serían dos fuentes de imágenes con nombres distintos y un rollback ambiguo. Revierte una decisión de SPEC 13.
- **Sí:** se despliega en cada push a `main` y con `workflow_dispatch`, detrás del environment `produccion` con un revisor obligatorio. **No:** desplegar sin aprobación (como TiendaMa), porque Halsa guarda datos clínicos. **No:** desplegar por tags.
- **Sí:** la versión es `v1.0.<run_number>`. Es inmutable, automática y cumple la regex de `desplegar.sh`, así el rollback al tag anterior sigue funcionando. **No:** `sha-<commit>`, porque obligaría a cambiar la regex y no es ordenable. **No:** crear tags git (fuera de alcance). La trazabilidad está en la etiqueta OCI `revision`.
- **Sí:** el usuario dedicado `halsa-deploy`, con `command=` forzado y `sudo` limitado a un solo script. Hace falta root porque `.env`, `/etc/halsa/secretos` y `/opt/npm/data` son de root. **No:** root directo. **No:** el grupo `docker` (equivale a root y aun así no lee `.env`).
- **Sí:** un solo comando SSH que recibe el paquete por la entrada estándar. **No:** dos llamadas (`podman load` y un script remoto, como TiendaMa), porque la llave forzada solo admite un comando y así todo el despliegue queda bajo un mismo `flock`.
- **Sí:** el script forzado se instala **fuera** de `/opt/halsa/deploy` (en `/usr/local/sbin`) y se actualiza a mano. **No:** ejecutarlo desde `deploy/`, porque el propio despliegue lo sobrescribiría mientras corre, y porque el código que corre como root no debe cambiar sin una persona.
- **Sí:** `deploy/` viaja desde el runner como `git archive` del commit exacto. **No:** `git fetch` con un token en el VPS (TiendaMa), porque deja un PAT guardado en el remote del VPS y obliga a gestionarlo.
- **Sí:** `rsync --delete` excluyendo `.env`, para que un archivo eliminado del repo también desaparezca del VPS. **No:** extraer encima, porque dejaría archivos viejos activos (por ejemplo, un `sitios/*.conf` retirado).
- **Sí:** se verifica el `sha256` del paquete en el VPS. Detecta un envío truncado antes de tocar nada.
- **Sí:** `StrictHostKeyChecking=yes` con `VPS_SSH_KNOWN_HOSTS`. **No:** `accept-new` (TiendaMa), porque en el primer uso, o tras perder el `known_hosts` efímero del runner, acepta cualquier host.
- **Sí:** Docker con buildx en el runner, conservando la caché `gha` de `release.yml`. **No:** Podman. El VPS corre Docker, y Podman pierde la caché.
- **Sí:** `halsa-postgres` se envía solo si cambia `deploy/postgres/**` o se pide en `workflow_dispatch`. Su recreación sigue siendo manual. **No:** enviarla siempre, porque son cientos de MB sin cambios. **No:** recrear `postgres` en cada despliegue, porque corta las conexiones y el archivado.
- **Sí:** `pull_policy: never` y nombres `localhost/…`. Que una imagen falte es un error explícito, no una descarga silenciosa desde otro lado.
- **Sí:** se exige cobertura en Domain y Application. El umbral arranca en la cobertura medida, si es menor al 80%, para no dejar el CI en rojo, y la meta 0.80 queda anotada. **No:** un 80% fijo desde el primer día.
- **Sí:** los tests corren en un solo `dotnet test` de toda la solución, con cobertura. **No:** separar unitarios e integración como TiendaMa, porque hoy no hay un filtro por nombre en Halsa y no aporta nada al objetivo.
- **Sí:** la concurrencia del workflow cancela solo en PRs. El despliegue tiene su propio grupo, sin cancelación.
- **Sí:** se retienen 5 versiones en el VPS, configurable. Alcanza para rollbacks manuales sin llenar el disco.
- **Sí:** la sección de variables del README va en inglés, como el resto del README.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| La llave del CI se filtra. | Solo ejecuta `halsa-recibir-despliegue`, que acepta únicamente un paquete con sumas válidas y despliega lo que trae. Aun así, alguien con la llave podría desplegar una imagen propia: la llave vive solo en el environment `produccion` (rama `main`, revisor obligatorio), y el runbook 05 agrega su rotación. |
| `sudo` borra `SSH_ORIGINAL_COMMAND` y el script siempre rechaza el comando. | La línea `env_keep` del sudoers. El criterio "aprobado, el job termina en verde" lo detecta en el primer despliegue. |
| El rollback vuelve a la imagen anterior, pero con el `deploy/` nuevo (compose y scripts). | Los cambios en `deploy/` deben ser compatibles con la versión anterior, igual que las migraciones (*expand/contract*, runbook 03). El PR lo declara. |
| El primer despliegue tras la transición no tiene a dónde volver, porque las imágenes viejas se llaman `ghcr.io/…`. | La sección "Transición desde GHCR" del runbook 03 las re-etiqueta como `localhost/…` antes del primer despliegue. |
| El paquete llena el disco del VPS. | El tope `HALSA_PAQUETE_MAX_MB`, la retención de 5 versiones y el borrado de la zona de recepción. |
| `halsa-recibir-despliegue` queda desactualizado respecto al repo. | Cambiarlo exige reinstalarlo a mano (runbook 01). El script imprime su propia versión (un comentario `# version:`), y `validar-despliegue` lo revisa con shellcheck. |
| La cobertura medida es baja y el umbral inicial no protege mucho. | Queda un `TODO(cobertura)` con la meta 0.80 en `ci.yml`. Subirlo es trabajo de tests, no de esta spec. |
| Una aprobación olvidada deja despliegues en cola. | Cada push nuevo a `main` encola su propio despliegue. Aprobar el más reciente es suficiente, y los anteriores se pueden rechazar. |

---

## Qué **no** está en esta spec

- Borrar los paquetes existentes en GHCR.
- Un entorno de *staging* o despliegues por entorno.
- La recreación automática de `postgres`.
- Tags git o *releases* de GitHub por despliegue.
- La firma de imágenes, el SBOM y el escaneo.
- Subir la cobertura al 80% o medir otras capas.
- *Blue/green* o varios VPS.

Cada uno, si se aborda, va en su propia spec.

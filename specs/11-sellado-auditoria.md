# SPEC 11 — Sellado Merkle de la auditoría, WORM externo y exportación verificable

> **Estado:** Borrador
> **Depende de:** SPEC 03, SPEC 04, SPEC 09
> **Fecha:** 2026-10-09
> **Objetivo:** Sellar cada 5 minutos los registros nuevos de `auditoria.accesos` de cada tenant en una raíz Merkle encadenada, firmarla con ECDSA y publicarla en un bucket externo inmutable, y permitir que un ente de control verifique una exportación sin confiar en Halsa.

---

## Por qué existe esta spec

El trigger de la SPEC 04 impide modificar la auditoría desde la aplicación.
No impide que alguien con root en el VPS o con el rol dueño altere filas, borre una partición o restaure un backup viejo.

El sellado convierte esa alteración en algo **detectable por terceros**.
Cada lote de registros produce una raíz Merkle firmada y encadenada con la anterior.
Esa raíz se publica fuera del VPS, en un almacenamiento que ni Halsa puede borrar.

Como el despliegue es en un VPS, el WORM es un **bucket S3 externo con Object Lock** (ARQ §3.3 asumía nube), y solo guarda hashes y exportaciones cifradas.

---

## Alcance

**Incluido:**

- `SelladoAuditoriaJob` (Quartz, cada 5 min): árbol Merkle (RFC 6962) por tenant y ventana, cadena de sellos y firma ECDSA P-256.
- La tabla `auditoria.sellos` (inmutable).
- `IWormStore` y `S3WormStore` (AWSSDK.S3, Object Lock en modo compliance, retención de 15 años). En desarrollo, MinIO con Object Lock en el compose.
- `ISelloTemporal`, con una implementación nula. Es la costura para un estampado RFC 3161.
- `transaction_timeout` en los roles de la aplicación, para garantizar que ninguna fila llega tarde a una ventana ya sellada.
- Las exportaciones asíncronas: `POST /v1/auditoria/exportaciones`, la consulta de estado y la descarga. El `.jsonl` incluye las pruebas de inclusión, va cifrado en el WORM y se audita.
- `GET /v1/auditoria/llaves-publicas`.
- La CLI `tools/Halsa.VerificadorAuditoria`.
- El health check `sellado` (`Degraded` si hay sellos sin publicar desde hace más de 1 h).

**Fuera de alcance (specs futuras):**

- El estampado cronológico certificado (RFC 3161 con una entidad acreditada por ONAC) → spec propia, sobre `ISelloTemporal`.
- El archivado de particiones antiguas en almacenamiento frío → spec propia.
- El sellado de otras tablas inmutables (la HC y los mensajes entrantes) → spec propia, si se requiere.
- La custodia de la llave de firma en un HSM → spec propia.
- La exportación automática y periódica a los entes de control → spec propia.

---

## Modelo de datos

### Ventanas y completitud

- **Clave de tenant:** el `tenant_id` del registro, o `__plataforma__` si es `null` (por ejemplo, un denegado antes del handler).
- **Ventana:** `(hasta_anterior, now() − 2 min]`, sobre `ocurrido_en`. Si un tenant no tiene sello previo, empieza en el registro más antiguo de ese tenant.
- **Garantía de completitud:** `ALTER ROLE halsa_app|halsa_clinico|halsa_ingesta SET transaction_timeout = '60s'` e `idle_in_transaction_session_timeout = '30s'` (PostgreSQL 17). Así ninguna transacción confirma un registro de auditoría más de 60 s después de su `ocurrido_en`, y el margen de 2 min cubre esa demora.
- Se sella aunque la ventana esté vacía (`cantidad = 0`): un sello vacío prueba que no hubo accesos.

### Árbol y sello

- Las hojas son los `hash_registro`, ordenados por `(ocurrido_en, id)`.
- `hoja = SHA-256(0x00 ‖ hash_registro)` y `nodo = SHA-256(0x01 ‖ izq ‖ der)` (RFC 6962). Un árbol vacío tiene raíz `SHA-256("")`.
- El sello tiene esta forma:

```json
{
  "version": 1, "selloId": "…", "tenant": "ARL_A", "desde": "…", "hasta": "…",
  "cantidad": 1234, "raiz": "hex", "selloAnterior": "hex | null",
  "llaveId": "hex16", "creadoEn": "…"
}
```

- `sello_hash = SHA-256(JCS(sello))` y `firma = ECDSA-P256-SHA256(sello_hash)`, en formato IEEE P1363 y codificada en base64.

Tabla `auditoria.sellos` (inmutable por trigger; solo `halsa_worker` inserta):

| Columna | Notas |
| --- | --- |
| `id`, `tenant_clave`, `desde`, `hasta`, `cantidad`, `raiz bytea`, `sello_anterior_hash bytea`, `sello_hash bytea`, `firma bytea`, `llave_id`, `estampa_tsa bytea` (null), `creado_en` | Única `(tenant_clave, hasta)` |
| `worm_objeto`, `worm_version_id`, `publicado_en` | Null hasta que se publica. Es la **única** columna actualizable: el trigger permite `UPDATE` solo de null a valor. |

### Llave de firma

- Es PKCS#8 PEM EC P-256, leída de `Auditoria:LlaveSelloPath` (`/run/secrets/halsa_sello` en el VPS). En desarrollo se genera con `docker/secrets/generar-dev.sh` (en `.gitignore`), y los tests la generan en memoria.
- `llaveId` son los primeros 16 caracteres hexadecimales del SHA-256 del SPKI público.
- `Auditoria:LlavesPublicasAnteriores` acepta las públicas retiradas, para verificar sellos viejos después de una rotación.
- `GET /v1/auditoria/llaves-publicas` es anónimo y devuelve `[{ llaveId, spkiPem, vigenteDesde, vigenteHasta }]`.

### WORM

```csharp
public interface IWormStore
{
    Task<WormObjeto> PublicarAsync(string clave, ReadOnlyMemory<byte> contenido, DateTimeOffset retenerHasta, CancellationToken ct);
    Task<Stream> LeerAsync(string clave, CancellationToken ct);
}
```

- La configuración es `Worm:Endpoint`, `Worm:Bucket`, `Worm:Region`, `Worm:AccessKey` y `Worm:SecretKey` (estos dos como secretos). El bucket se crea **con Object Lock** y un usuario cuya política no incluye `s3:BypassGovernanceRetention` ni `s3:DeleteObject`.
- Claves:
  - `sellos/{tenant}/{yyyy}/{MM}/{dd}/{selloId}.json`: el sello más su firma, en claro (solo hashes).
  - `exportaciones/{tenant}/{exportacionId}.jsonl.enc`: cifrado (ver abajo).
- `retenerHasta = now + 15 años`, en modo `COMPLIANCE`.
- En desarrollo, el servicio `halsa.backend.worm` (MinIO) y un contenedor de inicio `mc` crean el bucket `halsa-worm` con Object Lock.
- Si la publicación falla, el sello queda en la base con `worm_objeto` nulo, y el job reintenta la publicación en cada ejecución.

### Exportaciones

Tabla `auditoria.exportaciones`:

| Columna | Notas |
| --- | --- |
| `id`, `tenant_id`, `solicitada_por`, `desde`, `hasta`, `caso_medico_id` (null), `estado` (`PENDIENTE`/`EN_PROCESO`/`LISTA`/`FALLIDA`), `error` | RLS por tenant |
| `worm_objeto`, `sha256_claro`, `tamano`, `dek_envuelta`, `key_id`, `creada_en`, `lista_en` | |

- `POST /v1/auditoria/exportaciones` `{ desde, hasta, casoId? }` exige `auditoria:export` y un actor `Arl`. El rango es de 12 meses como máximo. Responde `202 { exportacionId }`, y audita `EXPORTAR / AUDITORIA`.
- `ExportacionAuditoriaJob` (Worker, cada minuto) toma una `PENDIENTE` y genera un JSON Lines con estas líneas:
  - `{"tipo":"registro", …todas las columnas…, "hashRegistro":"hex"}` por cada registro que coincide con los filtros;
  - `{"tipo":"prueba", "registroId", "selloId", "indice", "ruta":["hex",…]}`, la prueba de inclusión de cada registro;
  - `{"tipo":"sello", …sello…, "firma":"b64", "wormObjeto":"…"}` por cada sello involucrado;
  - `{"tipo":"manifiesto", "lineas":N, "sha256":"hex de las líneas anteriores", "firma":"b64", "llaveId":"…"}`.
- El archivo se cifra con una DEK aleatoria (AES-GCM), envuelta con la KEK, y se publica en el WORM.
- Solo se exportan registros ya sellados. Si `hasta` no está sellado, se ajusta y se informa en el manifiesto.
- `GET /v1/auditoria/exportaciones/{id}` devuelve el estado.
- `GET /v1/auditoria/exportaciones/{id}/archivo` (`auditoria:export`) lee del WORM, descifra y entrega en streaming (`application/x-ndjson`, `Cache-Control: no-store`). Se audita `LEER / AUDITORIA`.

### Verificador (`tools/Halsa.VerificadorAuditoria`)

`dotnet run --project tools/Halsa.VerificadorAuditoria -- exportacion.jsonl --llaves llaves.json [--sellos-worm <dir>]`

1. Recalcula `hashRegistro` con la misma canonicalización JCS de la SPEC 04 (el código compartido se copia, no se referencia, para que la herramienta sea autocontenida).
2. Verifica cada prueba de inclusión contra la `raiz` de su sello.
3. Verifica la firma de cada sello y la cadena `selloAnterior` entre los sellos consecutivos presentes.
4. Verifica el manifiesto (número de líneas, hash y firma).
5. Con `--sellos-worm`, compara cada sello con el objeto descargado del bucket.
6. Termina con `0` y "VERIFICADO", o con `1` y la primera discrepancia, que indica la línea.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Crear `MerkleTree` (RFC 6962, raíz y pruebas de inclusión) en `Infrastructure/Auditoria/Sellado/`. Tests unitarios con los vectores de RFC 6962 y una prueba de inclusión sobre 1, 2, 3 y 1000 hojas.
2. Crear `FirmadorSellos` (ECDSA P-256 desde PEM) y `SelloCanonico`. Tests: firmar y verificar, y una alteración de un byte invalida la firma.
3. Crear la migración `SellosAuditoria` (`sellos`, `exportaciones`, triggers, `GRANT`s, las políticas `TO halsa_worker` sobre `accesos` y `sellos`, y los `ALTER ROLE … SET transaction_timeout`).
4. Crear `IWormStore` y `S3WormStore`. Añadir MinIO y el contenedor `mc` de inicio al compose, y la configuración. Test de integración con Testcontainers MinIO: publicar y verificar que `DeleteObject` falla con Object Lock.
5. Crear `SelladoAuditoriaJob`, con las ventanas por tenant, los sellos vacíos, la cadena, la publicación y su reintento, y el health check. Tests de integración con un reloj falso.
6. Crear la exportación: el comando, el job, el cifrado, los endpoints y la auditoría. Tests de integración y funcionales.
7. Crear `GET /v1/auditoria/llaves-publicas`.
8. Crear `tools/Halsa.VerificadorAuditoria` y su proyecto de tests. Añadirlo a `Halsa.Backend.slnx`.

---

## Criterios de aceptación

- [ ] Con 1000 registros de `ARL_A` en una ventana, el job crea un sello con `cantidad=1000`, y su raíz coincide con la que calcula el verificador.
- [ ] Una ventana sin registros produce un sello con `cantidad=0` encadenado al anterior.
- [ ] Los registros con `tenant_id` nulo quedan en sellos con `tenant_clave='__plataforma__'`.
- [ ] Cada sello tiene en el bucket (MinIO de desarrollo) un objeto con retención `COMPLIANCE`, y `DeleteObject` sobre él falla.
- [ ] Con MinIO detenido, los sellos se crean con `worm_objeto` nulo, `/health` reporta `sellado: Degraded` después de 1 h, y al volver MinIO se publican en la siguiente ejecución.
- [ ] Como `halsa_app`, `SELECT 1 FROM auditoria.sellos` funciona, y `UPDATE auditoria.sellos SET raiz = …` falla. Como `halsa_worker`, cambiar un `worm_objeto` ya informado también falla.
- [ ] Una transacción de `halsa_app` que dura 70 s se aborta por `transaction_timeout`.
- [ ] Una exportación de `ARL_A` sobre un mes pasa a `LISTA`, y el verificador la valida con `0` / "VERIFICADO".
- [ ] Si se modifica cualquier campo de una línea `registro` del `.jsonl`, el verificador termina con `1` y señala esa línea.
- [ ] Si se altera, como dueño y desactivando el trigger, una fila de `auditoria.accesos` ya sellada y se exporta de nuevo, el verificador detecta que la prueba de inclusión no coincide con la raíz firmada.
- [ ] Si se borra un sello intermedio de la base, el verificador detecta la ruptura de la cadena `selloAnterior`.
- [ ] El objeto de la exportación en el bucket no contiene el texto de ningún `actor_id` (está cifrado).
- [ ] `POST /v1/auditoria/exportaciones` con un rango de 13 meses responde `400`, y sin `auditoria:export` responde `403`.
- [ ] Una exportación de `ARL_A` no contiene registros de `ARL_B`.
- [ ] Solicitar y descargar una exportación deja las filas `EXPORTAR / AUDITORIA` y `LEER / AUDITORIA`.
- [ ] `GET /v1/auditoria/llaves-publicas` sin token responde `200` con la llave vigente, y su `llaveId` coincide con el de los sellos.

---

## Decisiones

- **Sí:** un bucket S3 externo con Object Lock en modo compliance, solo para sellos y exportaciones cifradas. **No:** MinIO en el mismo VPS como WORM de producción, porque no resiste a root ni la pérdida del disco.
- **Sí:** los sellos se publican en claro porque solo contienen hashes, cantidades y fechas. Las exportaciones, que tienen datos personales, se cifran del lado del cliente antes de salir del VPS.
- **Sí:** ECDSA P-256, porque viene en .NET sin dependencias y lo verifica cualquier tercero con la llave pública. **Diferido:** el estampado RFC 3161 certificado, detrás de `ISelloTemporal`.
- **No:** HMAC con la KEK, porque solo Halsa podría verificar.
- **Sí:** árbol Merkle RFC 6962 (con prefijos de hoja y nodo) y pruebas de inclusión por registro. Así una exportación de un solo caso se verifica sin entregar los registros de otros casos.
- **Sí:** los sellos se encadenan por tenant (`selloAnterior`). Detecta un sello borrado o reordenado. **No:** una cadena por registro (ARQ §9), porque serializa las escrituras.
- **Sí:** se sella aunque la ventana esté vacía, para probar que no hubo actividad.
- **Sí:** la completitud se garantiza con `transaction_timeout` en los roles de la aplicación y un margen de 2 min. **No:** una tabla de pertenencia registro → sello, que duplica el volumen de la auditoría.
- **Sí:** la exportación es asíncrona y se guarda en el WORM. Queda evidencia de qué se entregó y cuándo.
- **Sí:** el verificador es una CLI autocontenida en el repo. Un perito no necesita acceso a Halsa.
- **Sí:** el sellado y las exportaciones corren con `halsa_worker`, con políticas propias de lectura entre tenants sobre `accesos`.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Si se pierde la llave privada de firma, no se pueden firmar sellos nuevos (los viejos siguen siendo verificables). | La llave tiene un backup fuera de línea junto con la KEK (SPEC 03). La rotación consiste en emitir una llave nueva y conservar la pública anterior en `LlavesPublicasAnteriores`. |
| Si se roba la llave privada, un atacante con root podría reescribir y volver a firmar sellos recientes. | Los sellos ya publicados en el WORM no se pueden reescribir. El atacante solo podría alterar lo no publicado (menos de 5 min), y el verificador con `--sellos-worm` detecta la divergencia. Para más garantía está la TSA (diferida). |
| `transaction_timeout` corta una operación legítima larga (una exportación grande). | Las exportaciones y los barridos corren con `halsa_worker`, que no tiene ese límite. Las operaciones de la API son cortas por diseño. |
| Las credenciales del bucket permiten borrar objetos. | La política IAM del usuario no tiene permisos de borrado ni de bypass, y Object Lock en modo compliance no se puede saltar ni con la cuenta raíz del proveedor. |
| La exportación cifrada queda inaccesible si se pierde la KEK. | Es el mismo riesgo y la misma mitigación que la HC (SPEC 03). |
| Costo del bucket por retención de 15 años. | Los sellos ocupan KB por día y por tenant. Las exportaciones son esporádicas. |

---

## Qué **no** está en esta spec

- El estampado RFC 3161 certificado.
- El archivado de particiones.
- El sellado de otras tablas.
- Un HSM.
- La exportación periódica automática.

Cada uno, si se aborda, va en su propia spec.

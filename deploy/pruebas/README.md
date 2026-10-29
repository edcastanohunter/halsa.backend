# Fase de pruebas en producción: datos de prueba y cierre

Este directorio contiene todo lo necesario para probar Halsa en el sitio real con datos ficticios y **borrarlos por completo** al terminar.

| Pieza | Qué hace |
| --- | --- |
| `deploy/pruebas/sembrar.sql` | Crea 2 ARL, 2 IPS y 19 casos que cubren todos los estados, plazos, incapacidades, dictámenes, códigos y solicitudes de HC. Idempotente. |
| `deploy/pruebas/purgar.sql` | Borra todo lo de las ARL de prueba: lo sembrado **y lo que los testers creen por la API**. Transaccional y con verificaciones. |
| `deploy/scripts/pruebas.sh` | `sembrar`, `estado`, `purgar [--dry] [--sin-parar] [--si]`. Corre en el VPS. |
| `deploy/keycloak/crear-usuarios-prueba.sh` | `crear`, `listar`, `eliminar` los 15 usuarios de Keycloak de prueba, con `sub` fijo. |

## Qué es "de prueba" (y por qué el borrado es seguro)

Todo cuelga de identificadores que ninguna entidad real usará. La purga solo toca estos, y se niega a ejecutarse si no cumplen el formato:

| Dato | Valor |
| --- | --- |
| ARL (tenant) | `PRUEBA_A`, `PRUEBA_B` |
| IPS (REPS ficticios) | `990000000001`, `990000000002` |
| Trabajadores | CC `99100001` … `99100013`, `99200001`; empleadores NIT `9990000011`, `9990000022`, `9990000033` |
| Usuarios (sub) | `01999f00-0000-7000-8000-…` |

Como el tenant sale del token, **todo lo que los testers hagan con estos usuarios queda bajo `PRUEBA_A`/`PRUEBA_B`** (o bajo las IPS de prueba) y la purga lo recoge sin listarlo. La RLS aísla estos tenants de las ARL reales mientras conviven en la misma base.

## Puesta en marcha

En el VPS, como `edhunter`, con `deploy/` ya desplegado:

```bash
~/halsa/deploy/scripts/pruebas.sh sembrar                     # imprime el código de activación PRB013-… (se muestra una sola vez)
~/halsa/deploy/keycloak/crear-usuarios-prueba.sh crear       # pide el admin de master y una contraseña temporal
~/halsa/deploy/scripts/pruebas.sh estado                      # 16 casos en PRUEBA_A y 3 en PRUEBA_B
```

Cada usuario cambia la contraseña al primer ingreso y el personal registra su OTP. Los correos son `<usuario>@pruebas.halsa.invalid`; si necesitan recuperar la contraseña por correo, define `HALSA_PRUEBAS_DOMINIO` con un dominio cuyo buzón controles antes de `crear`.

## Usuarios

Realm `halsa` (personal). Todos llevan `actor_tipo` y el `tenant_id` o `ips_id` que lee la API.

| Usuario | Rol | Pertenece a |
| --- | --- | --- |
| `analista.a` | analista-arl | PRUEBA_A |
| `laboral.a` | medico-laboral | PRUEBA_A |
| `calificador.a` | medico-calificador + `clinico:emergencia` | PRUEBA_A |
| `privacidad.a` | oficial-privacidad | PRUEBA_A |
| `admin.a` | administrador | PRUEBA_A |
| `analista.b`, `laboral.b`, `privacidad.b` | analista / laboral / privacidad | PRUEBA_B |
| `coordinador.ips1`, `medico.ips1` | coordinador-ips / medico-ips | IPS `990000000001` |
| `coordinador.ips2`, `medico.ips2` | coordinador-ips / medico-ips | IPS `990000000002` |

Realm `halsa-pacientes`: `paciente.1` (vinculado al CC 99100001), `paciente.2` (vinculado al CC 99100002) y `paciente.3` (**sin vínculo**: canjea el código `PRB013`).

El sub de cada usuario está fijo en el SQL (asignaciones, solicitudes, vínculos). El script usa `partialImport` porque el `create users` normal de Keycloak ignora el id pedido; si aun así el id no coincide, se deshace y se detiene.

## Casos sembrados

`A0n` = `PRUEBA_A`, `B0n` = `PRUEBA_B`. Los ids son `01999f00-0000-7000-8001-00000000{a|b}0NN`; el radicado, `ATEL-PRUEBA_A-<año>-00000N`.

| Caso | Estado | Trabajador | Qué ejercita |
| --- | --- | --- | --- |
| A01 | Radicado (AT) | 99100001 | Plazo `REPORTE_FURAT` que **vence en 10 min**: el Worker envía `plazo.proximo-a-vencer` ya, y `plazo.vencido` a los 10 min. Validación pendiente. |
| A02 | Validado (EL) | 99100002 | Sin remisión ni asignación: remitir y asignar desde cero. Reporte cumplido. |
| A03 | Validado (AT) | 99100001 | Remitido a IPS 1; asignados `laboral.a` y `medico.ips1`. Cadena de 2 incapacidades (30 días) y `TOPE_IT_180` abierto. Solicitud de copia HC **RECIBIDA**. |
| A04 | Validado (AT **grave**) | 99100004 | Asignado a `calificador.a`. Plazos de investigación y de remisión abiertos. **Registrar aquí el dictamen en vivo.** |
| A05 | Rechazado `NO_AFILIADO` | 99100005 | Plazos `SUSPENDIDO`. No admite HC ni dictamen. No admite código de activación. |
| A06 | Rechazado `DUPLICADO` | 99100001 | `duplicado_de` = A03. |
| A07 | En calificación PCL | 99100002 | Dictamen **NOTIFICADO** (35,5 %) con `CONTROVERSIA_DICTAMEN` abierto. Solicitud de copia **EN_TRAMITE**. |
| A08 | En calificación PCL | 99100002 | Dictamen **EN_FIRME** (62 %), controversia `CUMPLIDO`. Solicitud de copia **ENTREGADA**. |
| A09 | Validado (AT) | 99100007 | 5 incapacidades de 30 días (**150 acumulados**): la alerta del tope de 180 días ya venció sin enviarse. Remitido a IPS 1. |
| A10 | Validado (AT) | 99100008 | Remisión a IPS 1 **REVOCADA**: la IPS no debe verlo. |
| A11 | Validado (EL) | 99100009 | Remitido a IPS 2: **IPS 1 no debe verlo** (aislamiento entre IPS). |
| A12 | Radicado (AT) | 99100010 | Radicado hace 30 días: reporte (extemporáneo) e investigación `VENCIDO`. |
| A13 | Validado (AT) | 99100003 | Código de activación `PRB013` **ACTIVO**; `paciente.3` lo canjea. |
| A14 | Validado (AT) | 99100011 | Código `PRB014` **BLOQUEADO** (5 intentos). |
| A15 | Validado (EL) | 99100012 | Código `PRB015` **EXPIRADO**. |
| A16 | Validado (AT **mortal**) | 99100013 | Plazos de investigación y remisión; asignado a `calificador.a`. |
| B01 | Validado (AT) | 99100001 | Mismo trabajador que A01/A03 en **otra ARL**: el portal ve ambos casos; las ARL no se ven entre sí. Remitido a IPS 1. |
| B02 | Radicado (EL) | 99200001 | Reporte corriendo en la otra ARL. |
| B03 | Validado (AT) | 99100002 | Remitido a IPS 2. |

Los plazos cubren los cuatro estados (`ABIERTO`, `CUMPLIDO`, `VENCIDO`, `SUSPENDIDO`). Las fechas son relativas al momento de sembrar.

## Qué probar, por actor

Referencia de endpoints y reglas: `docs/diagrams/workflows/` (un archivo por actor) y `docs/api/atel-api.openapi.yaml`.

**Analista (`analista.a`)**: listar casos (A01–A16 y nada de B); asignar y desasignar sobre A02; remitir A02 a la IPS 1 y revocar una remisión; emitir un código de activación sobre A02, y otro sobre A13 (invalida `PRB013`); emitir uno sobre A05 → debe rechazarse; consultar plazos y cumplir uno a mano con nota ≥ 10 caracteres. `analista.b` no ve nada de PRUEBA_A.

**Coordinador y médico de IPS**: `coordinador.ips1` ve A03, A09, B01 y **no** A10 (revocado) ni A11 (IPS 2); asigna `medico.ips1` a B01. `medico.ips1` registra un concepto médico y una incapacidad nueva sobre A03 (el `TOPE_IT_180` se recalcula); prueba una prórroga con un hueco de fechas mayor que `Incapacidades:MaxDiasEntreProrrogas` (30) → debe rechazarse. `medico.ips2` solo ve A11 y B03.

**Calificador (`calificador.a`)**: registra el dictamen PCL sobre A04 (el caso pasa a *En calificación PCL*), lo notifica por cada medio, lo corrige y observa que `CONTROVERSIA_DICTAMEN` se abre. Intenta el dictamen sobre A05 → rechazado. Acceso de emergencia (*break-the-glass*) a un caso no asignado: debe exigir motivo de 20+ caracteres y dejar una revisión pendiente.

**Oficial de privacidad (`privacidad.a`)**: lista los accesos auditados (debe ver los de los demás testers de PRUEBA_A, no los de PRUEBA_B); cierra la revisión de emergencia que generó el calificador (`JUSTIFICADO`/`INJUSTIFICADO`); atiende la solicitud de copia de A03 (`RECIBIDA` → `EN_TRAMITE` → `ENTREGADA` con nota ≥ 10 caracteres, que cumple el plazo `SOLICITUD_COPIA_HC`).

**Administrador (`admin.a`)**: ve casos, incapacidades, plazos y auditoría de PRUEBA_A, y **no** HC, PCL ni emergencia.

**Paciente**: `paciente.1` ve A01, A03, A06 y B01 (de dos ARL); `paciente.2` ve A02, A07, A08 y B03. `paciente.3` canjea `PRB013-<secreto>` y entonces ve A13. Probar: código equivocado 5 veces → `BLOQUEADO`; `PRB015` → expirado; solicitar copia de la HC de un caso propio (se abre el plazo) y de uno ajeno → no existe. Ningún endpoint de paciente recibe ids de trabajador.

**Worker (sin intervención)**: A01 genera `plazo.proximo-a-vencer` al instante y `plazo.vencido` a los 10 min; A09 dispara la alerta del tope; A07 deja pasar la controversia y debe quedar en firme al vencer. Los webhooks solo salen si hay una suscripción (ver abajo).

### Lo que NO está sembrado y se crea por la API

Hay datos que no se pueden fabricar con SQL sin fingirlos, porque están cifrados por tenant o entran por mTLS:

- **Historia clínica y soportes PDF**: regístralos con `medico.ips1` sobre A03 (y con `laboral.a`/`calificador.a`). Las incapacidades y los dictámenes de A03, A07, A08 y A09 sembrados referencian registros clínicos y soportes **sintéticos** (no existen en el esquema `clinico`): sirven para listar, para los plazos y para el portal, pero leer su detalle clínico, descargar su soporte o hacer `GET` del dictamen de A07/A08 no devuelve contenido. Para ejercitar esas lecturas usa lo que registres tú en A03/A04/A16.
- **FURAT/FUREP (ingesta)**: requiere un cliente `int-arl-PRUEBA_A` con certificado de la PKI de Halsa (runbook 06). Si lo creas, es parte de la fase de pruebas: revócalo al cerrar. Los radicados siguen desde el 17 (PRUEBA_A) y el 4 (PRUEBA_B).
- **Webhooks**: la suscripción lleva su secreto cifrado; créala por la API con `integracion-arl`/`integracion-ips` apuntando a un receptor tuyo. La purga la borra.
- **Auditoría y revisiones de emergencia**: las genera el uso. No se siembran porque cada fila lleva un hash que valida la cadena de sellado.

## Cierre: borrar todo

```bash
~/halsa/deploy/scripts/pruebas.sh purgar --dry     # cuenta lo que se borraría; no cambia nada
~/halsa/deploy/scripts/pruebas.sh purgar           # pide escribir PURGAR
~/halsa/deploy/keycloak/crear-usuarios-prueba.sh eliminar
```

`purgar`:

1. muestra el ensayo (`--dry` se detiene aquí);
2. detiene `halsa-bff`, `halsa-api` y `halsa-worker` (`--sin-parar` lo omite) para que nadie escriba durante la purga, y los **vuelve a arrancar siempre**, aunque la purga falle;
3. ejecuta `purgar.sql` en **una sola transacción**: borra por catálogo toda tabla con `tenant_id` de los tenants de prueba, más lo derivado que no lo lleva (vínculos y canjes del portal, webhooks de las IPS de prueba, idempotencia, outbox, event_store, auditoría y revisiones de emergencia de esos tenants, IPS y usuarios), **incluidas las llaves de cifrado del tenant** (`clinico.llaves_tenant`);
4. comprueba antes de confirmar que no queda ninguna fila de prueba y que los casos y la auditoría de **cualquier otro tenant** tienen exactamente las mismas filas; si algo falla, `ROLLBACK`;
5. borra del volumen `halsa-archivos` las carpetas `PRUEBA_A/` y `PRUEBA_B/` (los soportes cifrados);
6. imprime el estado final, que debe estar en ceros.

La auditoría es inmutable por trigger y todo está bajo `FORCE ROW LEVEL SECURITY`. La purga no los desactiva: usa el superusuario `postgres` (por el socket local) con `session_replication_role = replica` **solo dentro de su transacción**. Al terminar, triggers, políticas y esquema quedan como estaban; las reglas de plazos (`sst.reglas_termino`) y los festivos no se tocan.

### Lo que la purga no alcanza

| Qué | Qué hacer |
| --- | --- |
| Usuarios de Keycloak | `crear-usuarios-prueba.sh eliminar`. Los pacientes que se registraron por su cuenta se borran a mano en la consola del realm `halsa-pacientes`. |
| Clientes de integración (`int-*-PRUEBA_*`) y sus certificados | Revocarlos (runbook 06). |
| Backups de pgBackRest | Conservan lo anterior a la purga hasta que venza su retención. Los datos clínicos de esos backups solo se abren con la llave del tenant, que la purga ya borró de la base viva pero **no** de un backup anterior: si importa, deja que venza la retención o restaura sin ese backup. |
| Redis y Seq | La caché caduca sola en minutos; los logs de Seq pueden mencionar ids de prueba. |
| Receptores de webhooks que hayas puesto | Fuera del sistema. |

### Antes de activar el sellado de la auditoría (SPEC 11)

El sellado Merkle publica en un bucket WORM con retención de **15 años en modo compliance**: nada publicado se puede borrar, ni la purga ni la cuenta raíz. Hoy el sellado no está implementado, así que la auditoría de prueba aún se puede borrar. **Purga antes de activar el sellado**, o los sellos de `PRUEBA_*` quedarán 15 años en el bucket y, además, la purga rompería la cadena de sellos del tenant.

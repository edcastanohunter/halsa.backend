# SPEC 03 — Aislamiento y cifrado del detalle clínico

> **Estado:** Aprobado 
> **Depende de:** SPEC 01, SPEC 02
> **Fecha:** 2026-10-09
> **Objetivo:** Guardar el detalle clínico de cada caso en un esquema `clinico` cifrado por tenant, al que solo llega un `DbContext` y un rol de PostgreSQL propios, de modo que ni el código administrativo ni un volcado de la base puedan leerlo.

---

## Por qué existe esta spec

La reserva de la historia clínica (Res. 1995/1999) y el secreto profesional (Ley 23/1981) exigen que una consulta administrativa **no pueda** alcanzar el detalle clínico.
No basta con que "no deba".
Esta spec construye las barreras 1, 2, 3 y 5 de ARQ §3.1 (dominio, `DbContext`, rol y cifrado) y la RLS clínica (barrera 4).

La plataforma se despliega primero en un **VPS**, sin un KMS gestionado de nube.
Por eso la llave maestra (KEK) es un archivo secreto, detrás de una interfaz que permite migrarla a OpenBao o Vault sin tocar el dominio.

La spec no expone endpoints HTTP.
Ningún acceso clínico sale por la API hasta que existan la auditoría (SPEC 04) y la autorización por recurso (SPEC 08).

---

## Alcance

**Incluido:**

- El dominio `Halsa.Backend.Domain/Clinico`: `HistoriaClinicaDetalle` (append-only), `ContenidoClinico`, `CampoCifrado`, `ProfesionalRef` y `CodigoClinico`.
- `ClinicalDbContext`, con su cadena de conexión `Clinical` (rol `halsa_clinico`) y su propio historial de migraciones.
- El esquema `clinico`, con las tablas `historia_clinica_detalle` y `llaves_tenant`, sus permisos, la RLS y un trigger de inmutabilidad.
- La función `atel.tenant_del_caso_remitido`, `SECURITY DEFINER`, que sirve a la RLS clínica de la IPS.
- El cifrado de campo con AES-256-GCM: `IClinicalCipher`, `IKeyEncryptionProvider` y `LocalFileKeyEncryptionProvider`.
- Las DEK por tenant y por propósito, envueltas con la KEK y generadas en el primer uso.
- El índice ciego HMAC del diagnóstico principal.
- `IClinicalCaseGate`: verifica, contra el lado administrativo, que el caso existe y es visible para el actor.
- La capa de aplicación `Halsa.Backend.Application/Clinico`, con el comando de registro, el comando de corrección y la consulta de registros por caso.
- El marcador `IClinicalQuery`, sin behavior todavía.
- Tests de arquitectura del aislamiento.

**Fuera de alcance (specs futuras):**

- Endpoints HTTP clínicos (escritura de conceptos, lectura de la HC y dictamen PCL) → SPEC 06 y SPEC 08.
- `ClinicalAuditBehavior` y la auditoría en la misma transacción → SPEC 04.
- La comprobación de permisos `clinico:*` y el ABAC por asignación → SPEC 08.
- La separación clínico/administrativo del FURAT y su atomicidad entre dos conexiones → SPEC 05.
- La rotación de llaves (DEK y KEK). Esta spec solo deja el `KeyId` en cada campo para hacerla posible → spec propia.
- Un proveedor de KEK en OpenBao o Vault → spec propia, cuando el VPS lo justifique.
- El modelo específico de cada tipo de registro (dictamen PCL, rehabilitación). Esta spec usa un `ContenidoClinico` único.
- La búsqueda por diagnóstico expuesta en la API.

---

## Modelo de datos

### Dominio (`Halsa.Backend.Domain.Clinico`)

```csharp
public readonly record struct HistoriaClinicaDetalleId(Guid Value);   // UUID v7

public enum TipoRegistroClinico { ReporteInicial, ConceptoMedico, Evolucion, ConceptoRehabilitacion, DictamenPcl, CertificadoIncapacidad }   // CertificadoIncapacidad: SPEC 07

public sealed record CodigoClinico(string Sistema, string Codigo);   // ej. ("CIE-10", "S62.6")

/// <summary>Texto clínico en claro. Nunca se persiste así: se serializa a JSON y se cifra.</summary>
public sealed record ContenidoClinico(
    string? Motivo, string? Anamnesis, string? ExamenFisico, string? Analisis, string? Plan,
    IReadOnlyList<CodigoClinico> Diagnosticos, string? Observaciones);

public sealed record CampoCifrado(byte[] Ciphertext, byte[] Nonce, byte[] Tag, string KeyId);

/// <summary>RegistroRethus solo puede ser null en un registro ReporteInicial (el informante de un FURAT no es médico; ver SPEC 05).</summary>
public sealed record ProfesionalRef(string Sub, string TipoDocumento, string NumeroDocumento, string? RegistroRethus);

public sealed class HistoriaClinicaDetalle : AggregateRoot<HistoriaClinicaDetalleId>
{
    TenantId TenantId; CasoMedicoId CasoMedicoId;   // solo el id: no hay navegación hacia atel
    TipoRegistroClinico Tipo; ProfesionalRef Profesional;
    DateTimeOffset AtendidoEn;                       // momento clínico (atención o evento). No puede ser futuro.
    DateTimeOffset RegistradoEn;                     // momento en que se guardó
    DateOnly RetenerHasta;                           // RegistradoEn + 15 años (Res. 839/2017)
    HistoriaClinicaDetalleId? CorrigeA;
    CampoCifrado Contenido; CampoCifrado? DiagnosticoPrincipal; byte[]? DiagnosticoIndiceCiego;

    static HistoriaClinicaDetalle Registrar(HistoriaClinicaDetalleId id, TenantId, CasoMedicoId, TipoRegistroClinico,
        ProfesionalRef, DateTimeOffset atendidoEn, CampoCifrado contenido, CampoCifrado? diagnostico, byte[]? indiceCiego,
        HistoriaClinicaDetalleId? corrigeA, DateTimeOffset ahora);
}
```

- El `id` se genera **antes** de cifrar, porque forma parte de la AAD.
- No hay setters públicos ni métodos de cambio. Una corrección es un registro nuevo con `CorrigeA`.
- `RetenerHasta` se calcula en la fábrica.

### Abstracciones (`Halsa.Backend.Application.Abstractions.Clinico`)

```csharp
public interface IClinicalCipher
{
    Task<CampoCifrado> CifrarAsync(TenantId tenant, Guid registroId, string campo, ReadOnlyMemory<byte> claro, CancellationToken ct);
    Task<byte[]> DescifrarAsync(TenantId tenant, Guid registroId, string campo, CampoCifrado cifrado, CancellationToken ct);
    Task<byte[]> IndiceCiegoAsync(TenantId tenant, CodigoClinico codigo, CancellationToken ct);
}

public interface IClinicalCaseGate
{
    /// <summary>Tenant del caso si existe y el actor actual lo ve (por tenant o por remisión vigente). Si no, null.</summary>
    Task<TenantId?> TenantDelCasoVisibleAsync(CasoMedicoId casoId, CancellationToken ct);
}

public interface IClinicalDbContext { /* Registros (lectura y alta) y SaveChangesAsync */ }

public interface IClinicalQuery;   // marcador: la SPEC 04 le cuelga ClinicalAuditBehavior
```

### Criptografía

| Elemento | Valor |
| --- | --- |
| Algoritmo de campo | AES-256-GCM, con nonce de 12 bytes aleatorio por campo y tag de 16 bytes |
| AAD | `"{tenantId}|{registroId}|{campo}"` (UTF-8). Los campos son `contenido` y `diagnostico`. |
| DEK | 32 bytes aleatorios, una por `(tenant, proposito)`, con `proposito ∈ { CONTENIDO, INDICE, INTEGRACION, SOPORTES }` (`INTEGRACION` la añade la SPEC 05 para el mensaje crudo, y `SOPORTES` la SPEC 07 para los PDF) |
| Envoltura de la DEK | AES-256-GCM con la KEK y AAD `"{tenantId}|{proposito}|{keyId}"` |
| `KeyId` | `"{tenantId}:{proposito}:v{n}"` (ej. `ARL_A:CONTENIDO:v1`) |
| Índice ciego | `HMAC-SHA256(DEK INDICE del tenant, "{SISTEMA}|{CODIGO}")`, con el sistema y el código en mayúsculas y sin espacios |
| Caché de DEK sin envolver | En memoria del proceso, 10 min. **Nunca** en Redis. |

### KEK (`IKeyEncryptionProvider`)

```csharp
public interface IKeyEncryptionProvider   // Infrastructure: la aplicación nunca la ve
{
    string KekId { get; }
    byte[] Envolver(ReadOnlySpan<byte> dek, ReadOnlySpan<byte> aad);
    byte[] Desenvolver(ReadOnlySpan<byte> dekEnvuelta, ReadOnlySpan<byte> aad);
}
```

`LocalFileKeyEncryptionProvider` lee 32 bytes en base64 de la configuración:

```json
"Clinico": {
  "KekId": "local:v1",
  "KekPath": "/run/secrets/halsa_kek",   // producción (VPS): secreto de Docker
  "KekBase64": null                      // solo Development y tests
}
```

- En `Production`, el arranque falla si `KekBase64` tiene valor o si `KekPath` no existe o no contiene 32 bytes.
- En `Development`, `KekBase64` va en `dotnet user-secrets`, nunca en `appsettings` versionado.
- Los tests generan una KEK aleatoria en memoria.

### Tablas (esquema `clinico`)

| Tabla | Columnas | Notas |
| --- | --- | --- |
| `historia_clinica_detalle` | `id`, `tenant_id`, `caso_medico_id`, `tipo`, `profesional_sub`, `profesional_tipo_documento`, `profesional_numero_documento`, `profesional_registro_rethus`, `atendido_en`, `registrado_en`, `retener_hasta`, `corrige_a` (nullable), `contenido_ct`, `contenido_nonce`, `contenido_tag`, `contenido_key_id`, `diagnostico_*` (nullable), `diagnostico_indice_ciego` (nullable) | Sin FK hacia `atel`. Índices `(tenant_id, caso_medico_id, registrado_en)` y `(tenant_id, diagnostico_indice_ciego)`. FK de `corrige_a` hacia la misma tabla. |
| `llaves_tenant` | `key_id` (PK), `tenant_id`, `proposito`, `version`, `kek_id`, `dek_envuelta bytea`, `creada_en`, `activa` | Único `(tenant_id, proposito) WHERE activa`. **Sin RLS**: las DEK están envueltas y solo `halsa_clinico` lee la tabla. |

### Permisos, RLS e inmutabilidad

| Rol | `clinico` |
| --- | --- |
| `halsa_app` | **Sin `USAGE`.** Ni siquiera un `FromSqlRaw` llega. |
| `halsa_clinico` | `USAGE`. `SELECT, INSERT` en las dos tablas. Sin `UPDATE`, `DELETE` ni `TRUNCATE`. `EXECUTE` sobre `atel.tenant_del_caso_remitido`. Sin `USAGE` en `atel`. |
| `halsa_owner` | Dueño. Sujeto a `FORCE ROW LEVEL SECURITY` y al trigger. |

```sql
-- Dueño: halsa_owner. Devuelve el tenant del caso solo si la IPS tiene una remisión vigente sobre él.
CREATE FUNCTION atel.tenant_del_caso_remitido(p_caso uuid, p_ips text) RETURNS text
    LANGUAGE sql STABLE SECURITY DEFINER SET search_path = atel, pg_temp AS $$
    SELECT c.tenant_id FROM atel.casos_medicos c
    JOIN atel.remisiones r ON r.caso_medico_id = c.id
    WHERE c.id = p_caso AND r.ips_id = p_ips AND r.revocada_en IS NULL LIMIT 1 $$;

CREATE POLICY p_hc ON clinico.historia_clinica_detalle FOR ALL
USING (
    tenant_id = current_setting('app.tenant_id', true)
    OR tenant_id = atel.tenant_del_caso_remitido(caso_medico_id, current_setting('app.ips_id', true)))
WITH CHECK (… la misma expresión …);

-- Append-only incluso para el dueño:
CREATE TRIGGER t_hc_inmutable BEFORE UPDATE OR DELETE ON clinico.historia_clinica_detalle
    FOR EACH ROW EXECUTE FUNCTION clinico.rechazar_modificacion();
```

`WITH CHECK` impide que una IPS inserte un registro con un `tenant_id` distinto al del caso remitido.

### `ClinicalDbContext`

- Usa la cadena de conexión `Clinical` (`halsa_clinico`) y el mismo `TenantSessionInterceptor` de la SPEC 02.
- Las configuraciones viven en el namespace `Halsa.Backend.Infrastructure.Database.Clinico`. `ApplyConfigurationsFromAssembly` se filtra por namespace en ambos contextos.
- El query filter es: tenant del actor, o, si el actor es IPS, la función `atel.tenant_del_caso_remitido` mapeada con `HasDbFunction`.
- El historial de migraciones es propio (`clinico.__ef_migrations_history`) y las migraciones viven en `Database/Migrations/Clinico`. `ApplyMigrationsAsync` migra primero `ApplicationDbContext` y después `ClinicalDbContext`, siempre con la cadena `Migrations`.

### Aplicación (`Halsa.Backend.Application.Clinico`)

| Mensaje | Entrada | Comportamiento |
| --- | --- | --- |
| `RegistrarRegistroClinicoCommand` | `CasoMedicoId`, `TipoRegistroClinico`, `AtendidoEn`, `ContenidoClinico`, `ProfesionalRef` sin `Sub` | Si el gate devuelve `null`, falla con `Clinico.CasoNoEncontrado`. Si no, genera el id, cifra `contenido` y `diagnostico` (el primer diagnóstico), calcula el índice ciego y persiste. `Sub` sale de `ICurrentUser`. |
| `CorregirRegistroClinicoCommand` | Lo mismo, más `HistoriaClinicaDetalleId` del registro a corregir | Falla si el registro no existe o si no es visible. El registro nuevo lleva `CorrigeA`. |
| `GetRegistrosClinicosQuery : IClinicalQuery` | `CasoMedicoId` | Devuelve `RegistroClinicoResponse[]` descifrados, en orden cronológico. Los registros corregidos se marcan con `CorregidoPor`. |

Los datos del profesional (documento y RETHUS) llegan como entrada del comando.
De dónde salen (claims o payload de la integración) se decide en las specs que exponen los endpoints.

---

## Plan de implementación

Cada paso deja la solución compilando y con los tests en verde.

1. Crear el dominio `Domain/Clinico` con los tipos de arriba. Tests unitarios: `RetenerHasta` igual a la fecha más 15 años, `CorrigeA` se conserva, y un `ProfesionalRef` sin RETHUS solo se acepta en `ReporteInicial`.
2. Crear las abstracciones en `Application/Abstractions/Clinico` (`IClinicalCipher`, `IClinicalCaseGate`, `IClinicalDbContext` e `IClinicalQuery`).
3. Crear `IKeyEncryptionProvider` y `LocalFileKeyEncryptionProvider` en `Infrastructure/Clinico/Crypto/`, con `ClinicoOptions` y validación al arrancar. Tests unitarios: envolver y desenvolver, una AAD distinta falla, y en `Production` el arranque rechaza `KekBase64`.
4. Crear `ClinicalDbContext`, las configuraciones en `Infrastructure/Database/Clinico/`, la cadena `Clinical` (en `appsettings*` y en los fixtures) y el filtro por namespace en los dos contextos. Crear la migración `AtelFuncionRemisionClinica` (la función) en `ApplicationDbContext`, y la migración `ClinicoInicial` en `ClinicalDbContext` (tablas, `GRANT`, `REVOKE`, RLS y trigger). Extender `ApplyMigrationsAsync` y los fixtures para que migren los dos contextos.
5. Crear `AesGcmClinicalCipher` y `DekStore`. `DekStore` genera las DEK la primera vez con `INSERT … ON CONFLICT DO NOTHING` y relee, y las cachea en memoria. Tests de integración: dos tareas concurrentes del mismo tenant obtienen la misma `KeyId`, y un ciphertext copiado a otra fila no se descifra.
6. Crear `ClinicalCaseGate` en Infrastructure, que consulta `ApplicationDbContext` (con el query filter y la RLS de la SPEC 02). Crear los dos comandos y la consulta de `Application/Clinico`, y sus validadores (al menos un campo del contenido con texto, y RETHUS no vacío). Tests de integración del flujo completo como ARL y como IPS.
7. Añadir los tests de arquitectura en `tests/Halsa.Backend.ArchitectureTests/Layers/ClinicalIsolationTests.cs`, más un test que construye el modelo de `ApplicationDbContext` y verifica que ninguna entidad mapea al esquema `clinico`.

---

## Criterios de aceptación

- [ ] Como `halsa_app`, `SELECT 1 FROM clinico.historia_clinica_detalle` falla con `permission denied for schema clinico`.
- [ ] Como `halsa_clinico`, `SELECT 1 FROM atel.casos_medicos` falla con `permission denied for schema atel`.
- [ ] Después de registrar un contenido con el texto `"FRACTURA-MARCADOR-7731"`, `SELECT count(*) FROM clinico.historia_clinica_detalle WHERE position('FRACTURA-MARCADOR-7731'::bytea in contenido_ct) > 0` devuelve `0`.
- [ ] Copiar `contenido_*` de un registro a otro (como dueño, en un test que desactiva el trigger) hace que el descifrado del segundo falle con una excepción de autenticación de GCM.
- [ ] `UPDATE` y `DELETE` sobre `clinico.historia_clinica_detalle` fallan como `halsa_clinico` (por permiso) y como `halsa_owner` (por el trigger).
- [ ] Con el contexto de `ARL_A`, `GetRegistrosClinicosQuery` sobre un caso de `ARL_B` devuelve `Clinico.CasoNoEncontrado`, y una consulta cruda como `halsa_clinico` con `app.tenant_id='ARL_A'` no ve registros de `ARL_B`.
- [ ] Un actor IPS `110011234501` con una remisión vigente registra y lee la HC del caso. Después de revocar la remisión, la consulta devuelve `Clinico.CasoNoEncontrado` y la RLS no devuelve filas.
- [ ] Un `INSERT` crudo como `halsa_clinico` con `app.ips_id` informado y un `tenant_id` distinto al del caso remitido falla por la política.
- [ ] `CorregirRegistroClinicoCommand` deja dos filas. La consulta devuelve las dos, y la original lleva `CorregidoPor` apuntando a la nueva.
- [ ] Dos registros con el diagnóstico `S62.6` del mismo tenant tienen el mismo `diagnostico_indice_ciego`. En otro tenant, el valor es distinto.
- [ ] `clinico.llaves_tenant` tiene exactamente una fila activa por `(tenant, proposito)` después de 20 registros concurrentes del mismo tenant.
- [ ] Al arrancar la API en `Production` con `Clinico:KekBase64` informado, el proceso termina con un error de validación de opciones.
- [ ] El test de arquitectura falla si un tipo de `Halsa.Backend.Application.Casos` referencia `Halsa.Backend.Domain.Clinico` o `IClinicalDbContext`.
- [ ] El test de arquitectura falla si un tipo fuera de `Halsa.Backend.Application.Clinico` (y de Infrastructure) referencia `IClinicalDbContext`.
- [ ] El modelo de `ApplicationDbContext` no contiene ninguna entidad con esquema `clinico`.
- [ ] No existe ningún endpoint que lea o escriba datos clínicos (`/openapi/v1.json` no tiene rutas con `clinic` ni `registros`).

---

## Decisiones

- **Sí:** KEK en un archivo secreto de Docker en el VPS, detrás de `IKeyEncryptionProvider`. Es operable hoy y cambiar a OpenBao o Vault solo cambia el proveedor.
- **No:** OpenBao o Vault ahora. Es otro servicio crítico más que operar (unseal y backups) en un VPS.
- **No:** Vault de HashiCorp como destino futuro preferido, por su licencia BSL. OpenBao es la opción natural cuando llegue.
- **No:** un KMS multi-región (ARQ §10). No aplica en un VPS, y se reemplaza por el backup de la KEK fuera de línea (ver Riesgos).
- **Sí:** DEK por tenant y por propósito, en `clinico.llaves_tenant`, envueltas y generadas en el primer uso. Una ARL nueva no requiere despliegue.
- **Sí:** `llaves_tenant` sin RLS. Una IPS escribe en casos de varios tenants y necesita la DEK de cada uno, y las DEK envueltas son inútiles sin la KEK.
- **Sí:** DEK separadas para el contenido y para el índice ciego. Comprometer el índice no compromete el contenido.
- **Sí:** cifrado explícito en el handler a través de `IClinicalCipher` (ARQ §5.2). **No:** `ValueConverter`, porque le faltan el tenant y el id de la fila.
- **No:** `pgcrypto`, porque la llave viajaría a la base de datos.
- **Sí:** la caché de DEK sin envolver es solo en memoria. **No:** en Redis, porque pondría llaves en claro en otro proceso.
- **Sí:** la RLS clínica de la IPS usa una función `SECURITY DEFINER` con `search_path` fijo. `halsa_clinico` no recibe `USAGE` sobre `atel` (se respeta ARQ §5.3).
- **Sí:** un trigger de inmutabilidad en la HC, además del `REVOKE`. Así el dueño tampoco puede enmendar (Res. 1995/1999).
- **Sí:** un `ContenidoClinico` tipado y único para todos los tipos de registro. Es validable y se puede mapear a FHIR `Composition`. Los modelos específicos por tipo se difieren.
- **Sí:** un historial de migraciones propio para `ClinicalDbContext`. Los dos contextos evolucionan sin pisarse.
- **Sí:** sin endpoints en esta spec. **No:** exponer la escritura antes de que exista la auditoría (SPEC 04), porque habría una ventana de accesos clínicos sin auditar.
- **Sí:** los datos del profesional entran como parámetros del comando. Su origen (claims o payload) se decide en las specs que exponen los endpoints.

---

## Riesgos

| Riesgo | Mitigación |
| --- | --- |
| Si se pierde el archivo de la KEK en el VPS, la HC queda ilegible y se incumple la retención de 15 años. | Una copia cifrada de la KEK, fuera de línea y fuera del VPS (por ejemplo, en un gestor de contraseñas de la organización y en papel en custodia). Un procedimiento de restauración ensayado antes de producción. `kek_id` en cada DEK permite convivir con una KEK nueva. |
| Un backup del VPS incluye a la vez el volumen de PostgreSQL y el archivo de la KEK. | La KEK vive en `/run/secrets` (tmpfs del contenedor) y su origen en el host se excluye de los backups del volumen de datos. Se documenta en el runbook de la SPEC 13. |
| La función `SECURITY DEFINER` se usa para escalar privilegios. | `search_path` fijo, `LANGUAGE sql` sin SQL dinámico, devuelve solo el `tenant_id`, y el `EXECUTE` se concede únicamente a `halsa_clinico`. |
| Rendimiento de la función en la política, evaluada por fila. | Es `STABLE` y se apoya en el índice parcial de remisiones de la SPEC 02. Se revisa con `EXPLAIN` sobre 10 000 registros. |
| La DEK cacheada en memoria queda expuesta en un volcado del proceso. | TTL de 10 min. El VPS no habilita *core dumps* para el contenedor de la API (runbook de la SPEC 13). |
| La FURAT (SPEC 05) necesita escribir el caso y su reporte clínico de forma atómica, pero usan conexiones distintas. | Se decide explícitamente en la SPEC 05. Esta spec no promete atomicidad entre contextos. |

---

## Qué **no** está en esta spec

- Endpoints clínicos (SPEC 06, SPEC 08).
- La auditoría de accesos (SPEC 04).
- RBAC `clinico:*` y ABAC por asignación (SPEC 08).
- La rotación de DEK y de KEK.
- Un proveedor de KEK en OpenBao o Vault.
- Modelos clínicos específicos por tipo de registro.
- La búsqueda por diagnóstico expuesta en la API.

Cada uno, si se aborda, va en su propia spec.

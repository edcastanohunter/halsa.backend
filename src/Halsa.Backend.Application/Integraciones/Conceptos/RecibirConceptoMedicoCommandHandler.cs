using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using FluentValidation.Results;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Integraciones.Commands.Radicar;
using Halsa.Backend.Application.Integraciones.Contratos;
using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Application.Integraciones.Conceptos;

/// <summary>
/// El flujo de la integración de la SPEC 06: actor, llave, idempotencia, formato y una sola transacción de
/// <c>IngestaDbContext</c> con la lectura del caso (la RLS por <c>app.ips_id</c> solo devuelve los remitidos), las
/// reglas, el registro cifrado con la DEK del tenant del caso, la llave y la auditoría. La idempotencia y el
/// registro son atómicos: no hay llave sin registro, ni registro sin llave.
/// </summary>
internal sealed class RecibirConceptoMedicoCommandHandler(
    ITenantContext tenantContext,
    IValidator<ConceptoMedicoV1> validator,
    IIdempotencyStore idempotencia,
    IIngestaDbContext db,
    IClinicalCipher cipher,
    IIngestaAuditTrail auditTrail,
    TimeProvider time)
    : ICommandHandler<RecibirConceptoMedicoCommand, RegistroClinicoCreadoResponse>
{
    public async Task<Result<RegistroClinicoCreadoResponse>> Handle(
        RecibirConceptoMedicoCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. Only the integration of an IPS, which brings its REPS. The permission is checked by the endpoint.
        if (!PuedeRecibir(tenantContext))
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(ClinicoErrors.ActorNoAutorizado);
        }

        // 2. The key.
        if (!Guid.TryParse(command.IdempotencyKey, out Guid llave))
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(IntegracionErrors.IdempotencyKeyInvalida);
        }

        string emisor = Emisor(tenantContext.IpsId!.Value);
        byte[] huella = SHA256.HashData(command.Cuerpo.Span);
        DateTimeOffset ahora = time.GetUtcNow();

        // 3. A live key answers before anything else: same body, the stored response; another body, 409.
        RespuestaIdempotente? previa = await idempotencia.BuscarVigenteAsync(emisor, llave, ahora, cancellationToken);
        if (previa is not null)
        {
            return Repetir(previa, huella);
        }

        // 4. Format.
        Result<ConceptoMedicoV1> concepto = await LeerAsync(command.Cuerpo, cancellationToken);
        if (concepto.IsFailure)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(concepto.Error);
        }

        // 5. One transaction.
        return await RegistrarAsync(concepto.Value, emisor, llave, huella, ahora, cancellationToken);
    }

    private async Task<Result<ConceptoMedicoV1>> LeerAsync(ReadOnlyMemory<byte> cuerpo, CancellationToken ct)
    {
        Result<JsonObject> objeto = LecturaJson.Objeto(cuerpo);
        if (objeto.IsFailure)
        {
            return Result.Failure<ConceptoMedicoV1>(objeto.Error);
        }

        Result<ConceptoMedicoV1> leido = LecturaJson.Leer<ConceptoMedicoV1>(objeto.Value);
        if (leido.IsFailure)
        {
            return leido;
        }

        ValidationResult validacion = await validator.ValidateAsync(leido.Value, ct);

        return validacion.IsValid
            ? leido
            : Result.Failure<ConceptoMedicoV1>(LecturaJson.Errores(validacion));
    }

    private async Task<Result<RegistroClinicoCreadoResponse>> RegistrarAsync(
        ConceptoMedicoV1 concepto,
        string emisor,
        Guid llave,
        byte[] huella,
        DateTimeOffset ahora,
        CancellationToken ct)
    {
        var casoId = new CasoMedicoId(concepto.CasoId!.Value);
        DateTimeOffset atendidoEn = concepto.AtendidoEn!.Value.ToUniversalTime();
        HistoriaClinicaDetalleId? corrigeA = concepto.CorrigeA is Guid id ? new HistoriaClinicaDetalleId(id) : null;

        await using IDbContextTransaction transaccion = await db.BeginTransactionAsync(ct);

        // 6. The case, through the RLS: a case without a live remission to this IPS does not exist for it.
        CasoVisible? caso = await db.CasoVisibleAsync(casoId, ct);
        if (caso is null)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(ClinicoErrors.CasoNoEncontrado);
        }

        Result admite = RegistroClinicoEscritura.AdmiteRegistro(caso, atendidoEn);
        if (admite.IsFailure)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(admite.Error);
        }

        if (corrigeA is not null && !await db.RegistroVisibleAsync(casoId, corrigeA.Value, ct))
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(ClinicoErrors.RegistroNoEncontrado);
        }

        // 7. Encrypted with the DEK of the case's tenant. The service account signs; the doctor is the payload's.
        ProfesionalV1 profesional = concepto.Profesional!;
        if (!TiposConcepto.TryParse(concepto.Tipo, out TipoRegistroClinico tipo))
        {
            throw new InvalidOperationException("El tipo se valida antes de registrar.");
        }

        Result<HistoriaClinicaDetalle> registro = await RegistroClinicoEscritura.CifrarAsync(
            caso.TenantId,
            casoId,
            tipo,
            new ProfesionalRef(
                tenantContext.Sub,
                profesional.Identificacion!.Tipo!,
                profesional.Identificacion.Numero!,
                profesional.RegistroRethus),
            atendidoEn,
            concepto.Contenido!.ADominio(),
            corrigeA,
            cipher,
            ahora,
            ct);

        if (registro.IsFailure)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(registro.Error);
        }

        RegistroClinicoCreadoResponse respuesta = RegistroClinicoEscritura.Creado(registro.Value);

        // 8. Claim the key: a concurrent request with the same key loses here, and replays the winner.
        bool reclamada = await idempotencia.ReclamarAsync(new SolicitudIdempotente
        {
            Emisor = emisor,
            IdempotencyKey = llave,
            TenantId = caso.TenantId.Value,
            CuerpoSha256 = huella,
            SolicitudId = respuesta.RegistroId,
            Respuesta = JsonSerializer.Serialize(respuesta, LecturaJson.Opciones),
            CreadaEn = ahora,
            ExpiraEn = ahora + RadicarReporteCommandHandler.VigenciaIdempotencia
        }, ct);

        if (!reclamada)
        {
            await transaccion.RollbackAsync(ct);

            RespuestaIdempotente ganadora = await idempotencia.BuscarVigenteAsync(emisor, llave, ahora, ct)
                ?? throw new InvalidOperationException("La Idempotency-Key estaba reclamada pero no se encontró vigente.");

            return Repetir(ganadora, huella);
        }

        db.Add(registro.Value);
        auditTrail.Registrar(
            corrigeA is null ? AccionAuditoria.Crear : AccionAuditoria.Corregir,
            RegistroClinicoEscritura.Objetivo(registro.Value),
            caso.TenantId);

        // SPEC 10: the case's ARL hears of it through its webhook, in this same transaction.
        db.Encolar(RegistroClinicoEscritura.Creado(registro.Value, tenantContext.IpsId!.Value.Value));

        // 9. The clinical policy's WITH CHECK re-evaluates the remission inside this transaction.
        if (await db.GuardarAsync(ct) == ResultadoGuardado.FueraDeAlcance)
        {
            await transaccion.RollbackAsync(ct);
            return Result.Failure<RegistroClinicoCreadoResponse>(ClinicoErrors.CasoNoEncontrado);
        }

        await transaccion.CommitAsync(ct);

        return Result.Success(respuesta);
    }

    internal static bool PuedeRecibir(ITenantContext tenantContext) =>
        tenantContext.Resuelto && tenantContext.Tipo == ActorTipo.Integracion && tenantContext.IpsId is not null;

    /// <summary>El emisor de una IPS en la idempotencia: <c>ips:{REPS}</c>.</summary>
    internal static string Emisor(IpsId ips) => $"ips:{ips.Value}";

    private static Result<RegistroClinicoCreadoResponse> Repetir(RespuestaIdempotente previa, byte[] huella)
    {
        if (!CryptographicOperations.FixedTimeEquals(previa.CuerpoSha256, huella))
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(IntegracionErrors.IdempotenciaConflicto);
        }

        RegistroClinicoCreadoResponse respuesta =
            JsonSerializer.Deserialize<RegistroClinicoCreadoResponse>(previa.Respuesta, LecturaJson.Opciones)
            ?? throw new JsonException("La respuesta guardada de la Idempotency-Key está vacía.");

        return Result.Success(respuesta);
    }
}

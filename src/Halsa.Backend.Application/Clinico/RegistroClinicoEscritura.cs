using System.Text.Json;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Clinico;

/// <summary>Datos del profesional que firma. El <c>Sub</c> no viaja en el comando: sale de <see cref="ICurrentUser"/>.</summary>
public sealed record DatosProfesional(string TipoDocumento, string NumeroDocumento, string? RegistroRethus);

/// <summary>
/// <c>RegistroClinicoCreadoResponse</c> del contrato (SPEC 06): la respuesta <c>201</c> de los dos canales de carga.
/// </summary>
public sealed record RegistroClinicoCreadoResponse(Guid RegistroId, Guid CasoId, DateTimeOffset RegistradoEn);

/// <summary>Lo común a registrar y corregir. Lo usa el validador compartido.</summary>
internal interface IRegistroClinicoInput
{
    CasoMedicoId CasoId { get; }

    TipoRegistroClinico Tipo { get; }

    DateTimeOffset AtendidoEn { get; }

    ContenidoClinico Contenido { get; }

    DatosProfesional Profesional { get; }
}

/// <summary>
/// Cifrado y alta de un registro clínico, común a los comandos de registro y de corrección.
/// </summary>
internal static class RegistroClinicoEscritura
{
    /// <summary>Nombres de campo de la AAD: <c>"{tenantId}|{registroId}|{campo}"</c>.</summary>
    public const string CampoContenido = "contenido";

    public const string CampoDiagnostico = "diagnostico";

    /// <summary>
    /// Cuánto puede preceder la atención a la fecha del evento del caso (SPEC 06): absorbe la zona horaria y los
    /// eventos que el reporte fecha a las 00:00.
    /// </summary>
    public static readonly TimeSpan ToleranciaFechaEvento = TimeSpan.FromDays(1);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Serializar<T>(T valor) => JsonSerializer.SerializeToUtf8Bytes(valor, Json);

    public static T Deserializar<T>(byte[] utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Json)
        ?? throw new JsonException($"El campo cifrado no contiene un {typeof(T).Name}.");

    /// <summary>
    /// Las reglas del caso para un registro nuevo (SPEC 06): un caso rechazado o cerrado no admite registros, y
    /// la atención no puede ser anterior al evento (con <see cref="ToleranciaFechaEvento"/>).
    /// </summary>
    public static Result AdmiteRegistro(CasoVisible caso, DateTimeOffset atendidoEn)
    {
        ArgumentNullException.ThrowIfNull(caso);

        if (caso.Estado is EstadoCaso.Rechazado or EstadoCaso.Cerrado)
        {
            return Result.Failure(ClinicoErrors.CasoNoAdmiteRegistros);
        }

        if (atendidoEn < caso.FechaEvento - ToleranciaFechaEvento)
        {
            return Result.Failure(ClinicoErrors.FechaAtencionAnteriorAlEvento);
        }

        return Result.Success();
    }

    /// <summary>
    /// Cifra y guarda un registro del backoffice. La fila de auditoría y su <see cref="RegistroClinicoCreadoIntegrationEvent"/>
    /// (SPEC 10) van en el mismo <c>SaveChangesAsync</c>: si no se registran, el registro clínico tampoco.
    /// </summary>
    /// <param name="ipsEmisora">El REPS del actor, si es una IPS.</param>
    public static async Task<Result<RegistroClinicoCreadoResponse>> RegistrarAsync(
        IRegistroClinicoInput input,
        TenantId tenant,
        HistoriaClinicaDetalleId? corrigeA,
        AccionAuditoria accion,
        string? ipsEmisora,
        IClinicalCipher cipher,
        IClinicalDbContext db,
        IClinicalAuditTrail auditTrail,
        ICurrentUser currentUser,
        TimeProvider time,
        CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(ClinicoErrors.ProfesionalNoIdentificado);
        }

        var profesional = new ProfesionalRef(
            currentUser.UserId.ToString(),
            input.Profesional.TipoDocumento,
            input.Profesional.NumeroDocumento,
            input.Profesional.RegistroRethus);

        Result<HistoriaClinicaDetalle> registro = await CifrarAsync(
            tenant, input.CasoId, input.Tipo, profesional, input.AtendidoEn, input.Contenido, corrigeA, cipher,
            time.GetUtcNow(), ct);

        if (registro.IsFailure)
        {
            return Result.Failure<RegistroClinicoCreadoResponse>(registro.Error);
        }

        db.Add(registro.Value);
        auditTrail.Registrar(accion, Objetivo(registro.Value), tenant);
        db.Encolar(Creado(registro.Value, ipsEmisora));
        await db.SaveChangesAsync(ct);

        return Result.Success(Creado(registro.Value));
    }

    /// <summary>
    /// Genera el id (antes de cifrar: forma parte de la AAD), cifra el contenido y el diagnóstico principal y
    /// calcula su índice ciego. No guarda: lo hace quien llama, en su contexto y su transacción.
    /// </summary>
    public static async Task<Result<HistoriaClinicaDetalle>> CifrarAsync(
        TenantId tenant,
        CasoMedicoId casoId,
        TipoRegistroClinico tipo,
        ProfesionalRef profesional,
        DateTimeOffset atendidoEn,
        ContenidoClinico contenido,
        HistoriaClinicaDetalleId? corrigeA,
        IClinicalCipher cipher,
        DateTimeOffset ahora,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(contenido);
        ArgumentNullException.ThrowIfNull(cipher);

        HistoriaClinicaDetalleId id = HistoriaClinicaDetalleId.New();

        CampoCifrado contenidoCifrado = await cipher.CifrarAsync(
            tenant, id.Value, CampoContenido, Serializar(contenido), ct);

        CampoCifrado? diagnostico = null;
        byte[]? indiceCiego = null;

        if (contenido.Diagnosticos is [CodigoClinico principal, ..])
        {
            diagnostico = await cipher.CifrarAsync(tenant, id.Value, CampoDiagnostico, Serializar(principal), ct);
            indiceCiego = await cipher.IndiceCiegoAsync(tenant, principal, ct);
        }

        return HistoriaClinicaDetalle.Registrar(
            id, tenant, casoId, tipo, profesional, atendidoEn,
            contenidoCifrado, diagnostico, indiceCiego, corrigeA, ahora);
    }

    /// <summary>El objetivo de la fila de auditoría de un registro: el registro, con su caso.</summary>
    public static ObjetivoAuditoria Objetivo(HistoriaClinicaDetalle registro)
    {
        ArgumentNullException.ThrowIfNull(registro);

        return new ObjetivoAuditoria(
            RecursoAuditoria.RegistroClinico, registro.Id.Value.ToString(), registro.CasoMedicoId.Value);
    }

    /// <summary>El evento del registro (SPEC 10): solo ids y el tipo.</summary>
    public static RegistroClinicoCreadoIntegrationEvent Creado(HistoriaClinicaDetalle registro, string? ipsEmisora)
    {
        ArgumentNullException.ThrowIfNull(registro);

        return new RegistroClinicoCreadoIntegrationEvent(
            registro.Id.Value, registro.CasoMedicoId.Value, registro.TenantId.Value, CodigosDictamen.Codigo(registro.Tipo),
            ipsEmisora);
    }

    public static RegistroClinicoCreadoResponse Creado(HistoriaClinicaDetalle registro)
    {
        ArgumentNullException.ThrowIfNull(registro);

        return new RegistroClinicoCreadoResponse(registro.Id.Value, registro.CasoMedicoId.Value, registro.RegistradoEn);
    }
}

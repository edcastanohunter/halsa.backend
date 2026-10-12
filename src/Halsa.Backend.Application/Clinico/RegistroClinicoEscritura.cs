using System.Text.Json;
using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Clinico;

/// <summary>Datos del profesional que firma. El <c>Sub</c> no viaja en el comando: sale de <see cref="ICurrentUser"/>.</summary>
public sealed record DatosProfesional(string TipoDocumento, string NumeroDocumento, string? RegistroRethus);

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

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static byte[] Serializar<T>(T valor) => JsonSerializer.SerializeToUtf8Bytes(valor, Json);

    public static T Deserializar<T>(byte[] utf8) =>
        JsonSerializer.Deserialize<T>(utf8, Json)
        ?? throw new JsonException($"El campo cifrado no contiene un {typeof(T).Name}.");

    /// <summary>
    /// Genera el id (antes de cifrar: forma parte de la AAD), cifra el contenido y el diagnóstico
    /// principal, calcula su índice ciego y guarda.
    /// </summary>
    public static async Task<Result<Guid>> RegistrarAsync(
        IRegistroClinicoInput input,
        TenantId tenant,
        HistoriaClinicaDetalleId? corrigeA,
        IClinicalCipher cipher,
        IClinicalDbContext db,
        ICurrentUser currentUser,
        TimeProvider time,
        CancellationToken ct)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId == Guid.Empty)
        {
            return Result.Failure<Guid>(ClinicoErrors.ProfesionalNoIdentificado);
        }

        var profesional = new ProfesionalRef(
            currentUser.UserId.ToString(),
            input.Profesional.TipoDocumento,
            input.Profesional.NumeroDocumento,
            input.Profesional.RegistroRethus);

        HistoriaClinicaDetalleId id = HistoriaClinicaDetalleId.New();

        CampoCifrado contenido = await cipher.CifrarAsync(
            tenant, id.Value, CampoContenido, Serializar(input.Contenido), ct);

        CampoCifrado? diagnostico = null;
        byte[]? indiceCiego = null;

        if (input.Contenido.Diagnosticos is [CodigoClinico principal, ..])
        {
            diagnostico = await cipher.CifrarAsync(tenant, id.Value, CampoDiagnostico, Serializar(principal), ct);
            indiceCiego = await cipher.IndiceCiegoAsync(tenant, principal, ct);
        }

        Result<HistoriaClinicaDetalle> registro = HistoriaClinicaDetalle.Registrar(
            id, tenant, input.CasoId, input.Tipo, profesional, input.AtendidoEn,
            contenido, diagnostico, indiceCiego, corrigeA, time.GetUtcNow());

        if (registro.IsFailure)
        {
            return Result.Failure<Guid>(registro.Error);
        }

        db.Add(registro.Value);
        await db.SaveChangesAsync(ct);

        return Result.Success(id.Value);
    }
}

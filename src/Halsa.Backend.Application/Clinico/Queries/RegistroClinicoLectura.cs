using System.Text.Json;
using System.Text.Json.Serialization;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;
using Halsa.Backend.Domain.Clinico;

namespace Halsa.Backend.Application.Clinico.Queries;

/// <summary>
/// La lectura común a la lista y al detalle de la HC (SPEC 08): descifrar un registro y armar su respuesta. Los
/// registros <see cref="TipoRegistroClinico.DictamenPcl"/> nunca salen por aquí: su motivación solo la ve quien tiene
/// <c>pcl:dictamen:read</c> y un step-up reciente, por el endpoint del dictamen.
/// </summary>
internal static class RegistroClinicoLectura
{
    /// <summary>Los tipos que la lectura general de la HC devuelve.</summary>
    public static IQueryable<HistoriaClinicaDetalle> DeLaHistoriaGeneral(this IQueryable<HistoriaClinicaDetalle> registros) =>
        registros.Where(r => r.Tipo != TipoRegistroClinico.DictamenPcl);

    public static async Task<RegistroClinicoResponse> LeerAsync(
        IClinicalCipher cipher, HistoriaClinicaDetalle registro, Guid? corregidoPor, CancellationToken ct)
    {
        byte[] claro = await cipher.DescifrarAsync(
            registro.TenantId, registro.Id.Value, RegistroClinicoEscritura.CampoContenido, registro.Contenido, ct);

        return new RegistroClinicoResponse(
            registro.Id.Value,
            registro.CasoMedicoId.Value,
            registro.Tipo,
            registro.Profesional,
            registro.AtendidoEn,
            registro.RegistradoEn,
            registro.RetenerHasta,
            registro.CorrigeA?.Value,
            corregidoPor,
            RegistroClinicoEscritura.Deserializar<ContenidoClinico>(claro));
    }
}

/// <summary>El <c>tipo</c> en la respuesta va con el código del contrato: <c>CONCEPTO_MEDICO</c>, <c>DICTAMEN_PCL</c>, …</summary>
public sealed class TipoRegistroClinicoJsonConverter()
    : JsonStringEnumConverter<TipoRegistroClinico>(JsonNamingPolicy.SnakeCaseUpper, allowIntegerValues: false);

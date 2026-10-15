using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Pacientes;

/// <summary>Errores del portal del paciente (SPEC 12). Todos tienen código <c>Paciente.*</c>.</summary>
public static class PacienteErrors
{
    /// <summary>El mismo para un código inexistente, vencido, usado o con el secreto equivocado: no se pueden enumerar.</summary>
    public static readonly Error CodigoInvalido =
        Error.Problem("Paciente.CodigoInvalido", "El código de activación no es válido.");

    public static readonly Error CodigoBloqueado =
        Error.Locked("Paciente.CodigoBloqueado",
            "El código se bloqueó por demasiados intentos. Pide uno nuevo a tu ARL.");

    public static readonly Error DemasiadosIntentos =
        Error.TooManyRequests("Paciente.DemasiadosIntentos",
            "Demasiados intentos fallidos. Vuelve a intentarlo en 24 horas.");

    public static readonly Error VinculoDistinto =
        Error.Conflict("Paciente.VinculoDistinto", "La cuenta ya está vinculada a otro documento.");

    public static readonly Error CasoNoValidado =
        Error.Conflict("Paciente.CasoNoValidado", "Solo un caso validado o posterior admite códigos de activación.");

    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Paciente.ActorNoAutorizado", "Esta operación es solo para el actor indicado.");
}

internal static class PacientesActores
{
    /// <summary>Un paciente de <c>halsa-pacientes</c>: el único actor de <c>/v1/paciente/*</c>.</summary>
    public static bool EsPaciente(this ITenantContext tenantContext) =>
        tenantContext.Resuelto && tenantContext.Tipo == ActorTipo.Paciente;

    /// <summary>El personal o la integración de una ARL: emiten los códigos de sus casos.</summary>
    public static bool EsDeArl(this ITenantContext tenantContext) =>
        tenantContext.Resuelto &&
        tenantContext.Tipo is ActorTipo.Arl or ActorTipo.Integracion &&
        tenantContext.TenantId is not null;
}

/// <summary>El cuerpo de <c>POST /v1/paciente/vinculos</c>.</summary>
public sealed record CanjearCodigoRequest(string? Codigo);

/// <summary>La respuesta <c>201</c> de <c>POST /v1/paciente/vinculos</c>.</summary>
public sealed record VinculoCreadoResponse(TrabajadorVinculadoResponse Trabajador);

/// <param name="NumeroEnmascarado">Solo los últimos dígitos: <c>******4050</c>.</param>
public sealed record TrabajadorVinculadoResponse(string Tipo, string NumeroEnmascarado)
{
    public static string Enmascarar(string numero)
    {
        ArgumentNullException.ThrowIfNull(numero);

        int visibles = Math.Min(4, numero.Length / 2);
        return string.Concat(new string('*', numero.Length - visibles), numero[^visibles..]);
    }
}

/// <summary>La respuesta <c>201</c> de <c>POST /v1/casos/{id}/codigos-activacion</c>.</summary>
/// <param name="Codigo">El código completo. Se muestra una sola vez: en la base solo quedan la sal y el hash.</param>
public sealed record CodigoActivacionResponse(string Codigo, DateTimeOffset ExpiraEn);

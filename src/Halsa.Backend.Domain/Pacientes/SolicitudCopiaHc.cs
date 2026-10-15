using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Pacientes;

public readonly record struct SolicitudCopiaHcId(Guid Value)
{
    public static SolicitudCopiaHcId New() => new(Guid.CreateVersion7());
}

/// <summary>Se guarda como <c>CORREO_CERTIFICADO</c>, <c>PRESENCIAL</c> o <c>ELECTRONICO</c>.</summary>
public enum MedioEntregaCopiaHc
{
    CorreoCertificado,
    Presencial,
    Electronico
}

/// <summary>Se guarda como <c>RECIBIDA</c>, <c>EN_TRAMITE</c>, <c>ENTREGADA</c> o <c>RECHAZADA</c>.</summary>
public enum EstadoSolicitudCopiaHc
{
    Recibida,
    EnTramite,
    Entregada,
    Rechazada
}

/// <summary>
/// Una solicitud de copia de la historia clínica (SPEC 12; Res. 1995/1999, art. 14), en <c>atel.solicitudes_copia_hc</c>.
/// La crea el paciente sobre un caso que ve; la atiende la ARL del caso (su <see cref="TenantId"/>) por el canal formal:
/// el portal nunca entrega la copia. Su plazo legal es <c>SOLICITUD_COPIA_HC</c>, que se cumple al cerrarla.
/// </summary>
public sealed class SolicitudCopiaHc
{
    public const int MaximoDireccion = 500;

    public const int MaximoObservaciones = 1000;

    public const int MinimoNota = 10;

    public const int MaximoNota = 1000;

    // Parameterless constructor required by EF Core materializer.
    private SolicitudCopiaHc() { }

    public SolicitudCopiaHcId Id { get; private set; }

    /// <summary>El tenant del caso: la ARL que atiende la solicitud.</summary>
    public TenantId TenantId { get; private set; }

    public CasoMedicoId CasoMedicoId { get; private set; }

    public string PacienteSub { get; private set; } = string.Empty;

    public MedioEntregaCopiaHc MedioEntrega { get; private set; }

    /// <summary>La dirección (correo certificado), el correo (electrónico) o nada (presencial).</summary>
    public string? DireccionOCorreo { get; private set; }

    public string? Observaciones { get; private set; }

    public DateTimeOffset SolicitadaEn { get; private set; }

    public EstadoSolicitudCopiaHc Estado { get; private set; }

    /// <summary>La última vez que la ARL la tramitó o la cerró.</summary>
    public DateTimeOffset? AtendidaEn { get; private set; }

    public string? AtendidaPor { get; private set; }

    /// <summary>La nota del cierre: qué se entregó, o por qué se rechazó.</summary>
    public string? NotaAtencion { get; private set; }

    /// <summary>Mientras está <c>RECIBIDA</c> o <c>EN_TRAMITE</c>: solo una abierta por caso.</summary>
    public bool Abierta => Estado is EstadoSolicitudCopiaHc.Recibida or EstadoSolicitudCopiaHc.EnTramite;

    public static SolicitudCopiaHc Crear(
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        string pacienteSub,
        MedioEntregaCopiaHc medioEntrega,
        string? direccionOCorreo,
        string? observaciones,
        DateTimeOffset ahora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pacienteSub);

        return new SolicitudCopiaHc
        {
            Id = SolicitudCopiaHcId.New(),
            TenantId = tenantId,
            CasoMedicoId = casoMedicoId,
            PacienteSub = pacienteSub,
            MedioEntrega = medioEntrega,
            DireccionOCorreo = string.IsNullOrWhiteSpace(direccionOCorreo) ? null : direccionOCorreo.Trim(),
            Observaciones = string.IsNullOrWhiteSpace(observaciones) ? null : observaciones.Trim(),
            SolicitadaEn = ahora.ToUniversalTime(),
            Estado = EstadoSolicitudCopiaHc.Recibida
        };
    }

    /// <summary>La ARL la toma: de <c>RECIBIDA</c> a <c>EN_TRAMITE</c>.</summary>
    public Result Tramitar(string atendidaPor, DateTimeOffset ahora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(atendidaPor);

        if (Estado != EstadoSolicitudCopiaHc.Recibida)
        {
            return Result.Failure(SolicitudCopiaHcErrors.TransicionInvalida);
        }

        Estado = EstadoSolicitudCopiaHc.EnTramite;
        AtendidaEn = ahora.ToUniversalTime();
        AtendidaPor = atendidaPor;

        return Result.Success();
    }

    /// <summary>La ARL la cierra como <c>ENTREGADA</c> o <c>RECHAZADA</c>, con una nota. Desde cualquier estado abierto.</summary>
    public Result Cerrar(EstadoSolicitudCopiaHc resultado, string nota, string atendidaPor, DateTimeOffset ahora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nota);
        ArgumentException.ThrowIfNullOrWhiteSpace(atendidaPor);

        if (resultado is not (EstadoSolicitudCopiaHc.Entregada or EstadoSolicitudCopiaHc.Rechazada))
        {
            return Result.Failure(SolicitudCopiaHcErrors.ResultadoInvalido);
        }

        if (!Abierta)
        {
            return Result.Failure(SolicitudCopiaHcErrors.TransicionInvalida);
        }

        Estado = resultado;
        AtendidaEn = ahora.ToUniversalTime();
        AtendidaPor = atendidaPor;
        NotaAtencion = nota;

        return Result.Success();
    }
}

/// <summary>Errores de las solicitudes de copia de la HC. Todos tienen código <c>SolicitudCopiaHc.*</c>.</summary>
public static class SolicitudCopiaHcErrors
{
    public static readonly Error YaAbierta =
        Error.Conflict("SolicitudCopiaHc.YaAbierta", "Ya hay una solicitud de copia abierta para este caso.");

    public static readonly Error NoEncontrada =
        Error.NotFound("SolicitudCopiaHc.NoEncontrada", "La solicitud no existe o no es de este tenant.");

    public static readonly Error TransicionInvalida =
        Error.Conflict("SolicitudCopiaHc.TransicionInvalida", "El estado actual de la solicitud no admite esta transición.");

    public static readonly Error ResultadoInvalido =
        Error.Problem("SolicitudCopiaHc.ResultadoInvalido", "El resultado debe ser ENTREGADA o RECHAZADA.");
}

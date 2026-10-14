using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Terminos;

public readonly record struct PlazoId(Guid Value)
{
    public static PlazoId New() => new(Guid.CreateVersion7());
}

/// <summary>Se guarda como <c>ABIERTO</c>, <c>CUMPLIDO</c>, <c>VENCIDO</c> o <c>SUSPENDIDO</c>.</summary>
public enum EstadoPlazo
{
    Abierto,
    Cumplido,
    Vencido,
    Suspendido
}

/// <summary>
/// Un plazo legal de un caso (SPEC 09), en <c>sst.plazos</c>: una fila calculada, no un job. Lo abre un evento
/// (radicación, incapacidad, notificación del dictamen), lo alerta y lo vence <c>BarridoPlazosJob</c>, y lo cumple otro
/// evento o una persona. Único por <c>(caso, regla, referencia)</c>: un evento repetido no lo duplica.
///
/// <see cref="Radicado"/> y <see cref="UltimaFechaFin"/> se copian del caso y de la cadena de incapacidades, porque el
/// barrido corre como <c>halsa_worker</c>, que no tiene acceso a <c>atel</c>. Los instantes se guardan en UTC.
/// </summary>
public sealed class Plazo
{
    /// <summary>La longitud mínima de la nota del cumplimiento manual.</summary>
    public const int MinimoNota = 10;

    public const int MaximoNota = 1000;

    /// <summary>Los días sin una incapacidad nueva tras el último fin de la cadena que la dan por cerrada.</summary>
    public const int DiasCierreCadena = 30;

    public const string NotaCadenaCerrada = "cadena cerrada antes del tope";

    // Parameterless constructor required by EF Core materializer.
    private Plazo() { }

    public PlazoId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public CasoMedicoId CasoMedicoId { get; private set; }

    public Guid ReglaId { get; private set; }

    public string ReglaCodigo { get; private set; } = string.Empty;

    /// <summary>La cadena de incapacidades o el dictamen del que cuelga. Null para los de la radicación.</summary>
    public Guid? ReferenciaId { get; private set; }

    public string Radicado { get; private set; } = string.Empty;

    public DateTimeOffset IniciaEn { get; private set; }

    public DateTimeOffset VenceEn { get; private set; }

    public DateTimeOffset AlertaEn { get; private set; }

    public EstadoPlazo Estado { get; private set; }

    public DateTimeOffset? AlertadoEn { get; private set; }

    public DateTimeOffset? CumplidoEn { get; private set; }

    /// <summary>El <c>sub</c> de quien lo cumplió a mano, o <c>sistema</c>.</summary>
    public string? CumplidoPor { get; private set; }

    public string? NotaCumplimiento { get; private set; }

    /// <summary>Se cumplió (o llegó) después de <see cref="VenceEn"/>.</summary>
    public bool Extemporaneo { get; private set; }

    /// <summary>Solo <see cref="CodigosRegla.TopeIt180"/>: el último <c>fecha_fin</c> de la cadena.</summary>
    public DateOnly? UltimaFechaFin { get; private set; }

    /// <summary>Abre un plazo de <paramref name="regla"/>, con su vencimiento y su alerta calculados.</summary>
    public static Plazo Abrir(
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        string radicado,
        ReglaTermino regla,
        Guid? referenciaId,
        DateTimeOffset iniciaEn,
        CalculadoraTerminos calculadora)
    {
        ArgumentNullException.ThrowIfNull(regla);

        (DateTimeOffset venceEn, DateTimeOffset alertaEn) = regla.Calcular(calculadora, iniciaEn);

        return Nuevo(tenantId, casoMedicoId, radicado, regla, referenciaId, iniciaEn, venceEn, alertaEn);
    }

    /// <summary>
    /// Abre el <see cref="CodigosRegla.TopeIt180"/> de una cadena: vence <c>fecha_fin + (180 − acumulados)</c> días y
    /// alerta <c>30</c> días antes (el día 150), al final del día. <paramref name="iniciaEn"/> es el inicio de la cadena.
    /// </summary>
    public static Plazo AbrirTope(
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        string radicado,
        ReglaTermino regla,
        Guid cadenaId,
        DateTimeOffset iniciaEn,
        DateOnly fechaFin,
        int diasAcumulados)
    {
        ArgumentNullException.ThrowIfNull(regla);

        Plazo plazo = Nuevo(tenantId, casoMedicoId, radicado, regla, cadenaId, iniciaEn, default, default);
        plazo.RecalcularTope(regla, fechaFin, diasAcumulados);

        return plazo;
    }

    /// <summary>Una incapacidad nueva de la cadena: el tope se recalcula con su fin y sus días acumulados.</summary>
    public void RecalcularTope(ReglaTermino regla, DateOnly fechaFin, int diasAcumulados)
    {
        ArgumentNullException.ThrowIfNull(regla);

        DateOnly vence = fechaFin.AddDays(regla.Cantidad - diasAcumulados);

        VenceEn = CalculadoraTerminos.FinDelDia(vence).ToUniversalTime();
        AlertaEn = CalculadoraTerminos.FinDelDia(vence.AddDays(-regla.AlertaCantidad)).ToUniversalTime();
        UltimaFechaFin = fechaFin;
    }

    /// <summary>
    /// Un plazo de reporte se evalúa al abrirse: si la radicación llegó a tiempo queda <see cref="EstadoPlazo.Cumplido"/>;
    /// si no, <see cref="EstadoPlazo.Vencido"/> y extemporáneo. Devuelve <c>true</c> cuando nace vencido.
    /// </summary>
    public bool EvaluarRadicacion(DateTimeOffset radicadoEn, string cumplidoPor)
    {
        if (radicadoEn <= VenceEn)
        {
            Estado = EstadoPlazo.Cumplido;
            CumplidoEn = radicadoEn.ToUniversalTime();
            CumplidoPor = cumplidoPor;
            return false;
        }

        Estado = EstadoPlazo.Vencido;
        Extemporaneo = true;
        return true;
    }

    /// <summary>
    /// El cumplimiento manual, con su nota. Solo desde <see cref="EstadoPlazo.Abierto"/> o <see cref="EstadoPlazo.Vencido"/>;
    /// uno vencido, o cumplido después de <see cref="VenceEn"/>, queda extemporáneo.
    /// </summary>
    public Result Cumplir(string nota, DateTimeOffset cumplidoEn, string cumplidoPor, DateTimeOffset ahora)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nota);
        ArgumentException.ThrowIfNullOrWhiteSpace(cumplidoPor);

        if (Estado is not (EstadoPlazo.Abierto or EstadoPlazo.Vencido))
        {
            return Result.Failure(PlazoErrors.NoCumplible);
        }

        if (cumplidoEn > ahora)
        {
            return Result.Failure(PlazoErrors.CumplidoEnFuturo);
        }

        Extemporaneo = Extemporaneo || Estado == EstadoPlazo.Vencido || cumplidoEn > VenceEn;
        Estado = EstadoPlazo.Cumplido;
        CumplidoEn = cumplidoEn.ToUniversalTime();
        CumplidoPor = cumplidoPor;
        NotaCumplimiento = nota;

        return Result.Success();
    }

    /// <summary>El caso se rechazó: un plazo abierto deja de correr.</summary>
    public bool Suspender()
    {
        if (Estado != EstadoPlazo.Abierto)
        {
            return false;
        }

        Estado = EstadoPlazo.Suspendido;
        return true;
    }

    private static Plazo Nuevo(
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        string radicado,
        ReglaTermino regla,
        Guid? referenciaId,
        DateTimeOffset iniciaEn,
        DateTimeOffset venceEn,
        DateTimeOffset alertaEn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(radicado);

        return new Plazo
        {
            Id = PlazoId.New(),
            TenantId = tenantId,
            CasoMedicoId = casoMedicoId,
            ReglaId = regla.Id,
            ReglaCodigo = regla.Codigo,
            ReferenciaId = referenciaId,
            Radicado = radicado,
            IniciaEn = iniciaEn.ToUniversalTime(),
            VenceEn = venceEn.ToUniversalTime(),
            AlertaEn = alertaEn.ToUniversalTime(),
            Estado = EstadoPlazo.Abierto
        };
    }
}

/// <summary>Errores de los plazos. Todos tienen código <c>Plazo.*</c>.</summary>
public static class PlazoErrors
{
    public static readonly Error NoEncontrado =
        Error.NotFound("Plazo.NoEncontrado", "El plazo no existe o no es de este caso.");

    public static readonly Error NoCumplible =
        Error.Conflict("Plazo.NoCumplible", "Solo un plazo ABIERTO o VENCIDO se puede cumplir.");

    public static readonly Error CumplidoEnFuturo =
        Error.Problem("Plazo.CumplidoEnFuturo", "La fecha de cumplimiento no puede ser futura.");

    public static readonly Error ActorNoAutorizado =
        Error.Forbidden("Plazo.ActorNoAutorizado", "Solo el personal de la ARL del caso consulta y cumple sus plazos.");
}

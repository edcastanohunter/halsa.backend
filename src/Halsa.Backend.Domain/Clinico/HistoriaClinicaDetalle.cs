using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Clinico;

/// <summary>
/// Un registro de la historia clínica de un caso. Append-only: no hay métodos de cambio, y una
/// corrección es un registro nuevo que apunta al original con <see cref="CorrigeA"/>.
///
/// Solo referencia el caso por id: el contexto clínico no navega hacia <c>atel</c> (SPEC 03).
/// El contenido llega ya cifrado, porque la AAD incluye el id y el id se genera antes de cifrar.
/// </summary>
public sealed class HistoriaClinicaDetalle : AggregateRoot<HistoriaClinicaDetalleId>
{
    /// <summary>Res. 839/2017: la historia clínica se conserva 15 años desde el último registro.</summary>
    public const int AniosRetencion = 15;

    // Parameterless constructor required by EF Core materializer.
    private HistoriaClinicaDetalle() { }

    private HistoriaClinicaDetalle(
        HistoriaClinicaDetalleId id,
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        TipoRegistroClinico tipo,
        ProfesionalRef profesional,
        DateTimeOffset atendidoEn,
        DateTimeOffset registradoEn,
        CampoCifrado contenido,
        CampoCifrado? diagnosticoPrincipal,
        byte[]? diagnosticoIndiceCiego,
        HistoriaClinicaDetalleId? corrigeA)
        : base(id)
    {
        TenantId = tenantId;
        CasoMedicoId = casoMedicoId;
        Tipo = tipo;
        Profesional = profesional;
        AtendidoEn = atendidoEn;
        RegistradoEn = registradoEn;
        RetenerHasta = DateOnly.FromDateTime(registradoEn.UtcDateTime).AddYears(AniosRetencion);
        Contenido = contenido;
        DiagnosticoPrincipal = diagnosticoPrincipal;
        DiagnosticoIndiceCiego = diagnosticoIndiceCiego;
        CorrigeA = corrigeA;
    }

    // ── State ────────────────────────────────────────────────────────────────

    public TenantId TenantId { get; private set; }

    public CasoMedicoId CasoMedicoId { get; private set; }

    public TipoRegistroClinico Tipo { get; private set; }

    public ProfesionalRef Profesional { get; private set; } = null!;

    /// <summary>Momento clínico (atención o evento). No puede ser futuro.</summary>
    public DateTimeOffset AtendidoEn { get; private set; }

    /// <summary>Momento en que se guardó el registro.</summary>
    public DateTimeOffset RegistradoEn { get; private set; }

    /// <summary><see cref="RegistradoEn"/> más <see cref="AniosRetencion"/> años.</summary>
    public DateOnly RetenerHasta { get; private set; }

    public HistoriaClinicaDetalleId? CorrigeA { get; private set; }

    public CampoCifrado Contenido { get; private set; } = null!;

    public CampoCifrado? DiagnosticoPrincipal { get; private set; }

    public byte[]? DiagnosticoIndiceCiego { get; private set; }

    // ── Factory ───────────────────────────────────────────────────────────────

    public static Result<HistoriaClinicaDetalle> Registrar(
        HistoriaClinicaDetalleId id,
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        TipoRegistroClinico tipo,
        ProfesionalRef profesional,
        DateTimeOffset atendidoEn,
        CampoCifrado contenido,
        CampoCifrado? diagnostico,
        byte[]? indiceCiego,
        HistoriaClinicaDetalleId? corrigeA,
        DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(profesional);
        ArgumentNullException.ThrowIfNull(contenido);

        if (atendidoEn > ahora)
        {
            return Result.Failure<HistoriaClinicaDetalle>(ClinicoErrors.AtendidoEnFuturo);
        }

        if (string.IsNullOrWhiteSpace(profesional.RegistroRethus) && tipo != TipoRegistroClinico.ReporteInicial)
        {
            return Result.Failure<HistoriaClinicaDetalle>(ClinicoErrors.RethusRequerido);
        }

        return Result.Success(new HistoriaClinicaDetalle(
            id, tenantId, casoMedicoId, tipo, profesional,
            atendidoEn, ahora, contenido, diagnostico, indiceCiego, corrigeA));
    }
}

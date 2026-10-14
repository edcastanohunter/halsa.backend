using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Dictamenes;

/// <summary>
/// La parte administrativa del dictamen de pérdida de capacidad laboral (SPEC 08), en <c>atel.dictamenes_pcl</c>: lo
/// que la ARL necesita para liquidar. La motivación clínica (fundamentos, diagnósticos y componentes) es un registro
/// <c>DICTAMEN_PCL</c> de la historia clínica, cifrado, al que apunta <see cref="RegistroClinicoId"/>.
///
/// Un solo dictamen vigente por caso (<see cref="CorregidoPor"/> nulo). Corregir es un dictamen nuevo que apunta al
/// anterior con <see cref="CorrigeA"/>, y solo mientras el vigente esté <see cref="EstadoNotificacionDictamen.Pendiente"/>.
/// </summary>
public sealed class DictamenPcl
{
    // Parameterless constructor required by EF Core materializer.
    private DictamenPcl() { }

    public DictamenPclId Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public CasoMedicoId CasoMedicoId { get; private set; }

    /// <summary>El registro <c>DICTAMEN_PCL</c> de la historia clínica (en <c>clinico</c>: sin FK entre esquemas).</summary>
    public Guid RegistroClinicoId { get; private set; }

    public decimal PorcentajePcl { get; private set; }

    public OrigenPcl Origen { get; private set; }

    public DateOnly FechaEstructuracion { get; private set; }

    public DateOnly FechaDictamen { get; private set; }

    public string CalificadorSub { get; private set; } = string.Empty;

    public string? CalificadorNombre { get; private set; }

    public string CalificadorRegistroRethus { get; private set; } = string.Empty;

    public EstadoNotificacionDictamen EstadoNotificacion { get; private set; }

    public DateTimeOffset? NotificadoEn { get; private set; }

    public MedioNotificacion? MedioNotificacion { get; private set; }

    public DictamenPclId? CorrigeA { get; private set; }

    public DictamenPclId? CorregidoPor { get; private set; }

    public bool Vigente => CorregidoPor is null;

    /// <param name="hoy">Hoy en Colombia: la fecha del dictamen no puede ser posterior.</param>
    public static Result<DictamenPcl> Registrar(
        DictamenPclId id,
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        Guid registroClinicoId,
        decimal porcentajePcl,
        OrigenPcl origen,
        DateOnly fechaEstructuracion,
        DateOnly fechaDictamen,
        string calificadorSub,
        string? calificadorNombre,
        string calificadorRegistroRethus,
        DictamenPclId? corrigeA,
        DateOnly hoy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(calificadorSub);
        ArgumentException.ThrowIfNullOrWhiteSpace(calificadorRegistroRethus);

        if (porcentajePcl is < 0 or > 100 || decimal.Round(porcentajePcl, 2) != porcentajePcl)
        {
            return Result.Failure<DictamenPcl>(DictamenErrors.PorcentajeFueraDeRango);
        }

        if (fechaEstructuracion > fechaDictamen || fechaDictamen > hoy)
        {
            return Result.Failure<DictamenPcl>(DictamenErrors.FechasInvalidas);
        }

        return Result.Success(new DictamenPcl
        {
            Id = id,
            TenantId = tenantId,
            CasoMedicoId = casoMedicoId,
            RegistroClinicoId = registroClinicoId,
            PorcentajePcl = porcentajePcl,
            Origen = origen,
            FechaEstructuracion = fechaEstructuracion,
            FechaDictamen = fechaDictamen,
            CalificadorSub = calificadorSub,
            CalificadorNombre = calificadorNombre,
            CalificadorRegistroRethus = calificadorRegistroRethus,
            EstadoNotificacion = EstadoNotificacionDictamen.Pendiente,
            CorrigeA = corrigeA
        });
    }

    /// <summary>
    /// Deja de ser el vigente: lo reemplaza <paramref name="nuevo"/>. Solo si aún no se notificó; un dictamen
    /// notificado se recalifica con otro procedimiento (fuera de la SPEC 08).
    /// </summary>
    public Result CorregirCon(DictamenPclId nuevo)
    {
        if (!Vigente)
        {
            return Result.Failure(DictamenErrors.CorrigeANoVigente);
        }

        if (EstadoNotificacion != EstadoNotificacionDictamen.Pendiente)
        {
            return Result.Failure(DictamenErrors.YaNotificado);
        }

        CorregidoPor = nuevo;
        return Result.Success();
    }
}

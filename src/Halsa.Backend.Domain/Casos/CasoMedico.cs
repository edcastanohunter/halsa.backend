using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Casos;

/// <summary>
/// Raíz de agregado del caso de salud laboral.
///
/// Se persiste por estado (no por eventos) con <c>xmin</c> como token de concurrencia (SPEC 02).
/// El event sourcing queda diferido para cuando el volumen lo justifique (ARQ §5.5).
/// </summary>
public sealed class CasoMedico : AggregateRoot<CasoMedicoId>
{
    private readonly List<Remision> _remisiones = [];
    private readonly List<Asignacion> _asignaciones = [];

    // Parameterless constructor required by EF Core materializer.
    private CasoMedico() { }

    private CasoMedico(
        CasoMedicoId id,
        TenantId tenantId,
        string radicado,
        TipoEvento tipoEvento,
        Identificacion trabajador,
        Identificacion empleador,
        DateTimeOffset fechaEvento,
        string municipioDivipola,
        bool esGraveOMortal,
        DateTimeOffset radicadoEn)
        : base(id)
    {
        TenantId = tenantId;
        Radicado = radicado;
        TipoEvento = tipoEvento;
        Trabajador = trabajador;
        Empleador = empleador;
        FechaEvento = fechaEvento;
        MunicipioDivipola = municipioDivipola;
        EsGraveOMortal = esGraveOMortal;
        Estado = EstadoCaso.Radicado;
        RadicadoEn = radicadoEn;
    }

    // ── State ────────────────────────────────────────────────────────────────

    public TenantId TenantId { get; private set; }

    public string Radicado { get; private set; } = string.Empty;

    public TipoEvento TipoEvento { get; private set; }

    public DateTimeOffset FechaEvento { get; private set; }

    public Identificacion Trabajador { get; private set; } = null!;

    public Identificacion Empleador { get; private set; } = null!;

    public string MunicipioDivipola { get; private set; } = string.Empty;

    public EstadoCaso Estado { get; private set; }

    public bool EsGraveOMortal { get; private set; }

    public ResultadoCalificacion? Calificacion { get; private set; }

    public DateTimeOffset RadicadoEn { get; private set; }

    /// <summary>Cuándo pasó la validación asíncrona (SPEC 05). Lo muestra la línea de tiempo del paciente (SPEC 12).</summary>
    public DateTimeOffset? ValidadoEn { get; private set; }

    /// <summary>Cuándo la validación asíncrona lo rechazó (SPEC 05). Lo muestra la línea de tiempo del paciente (SPEC 12).</summary>
    public DateTimeOffset? RechazadoEn { get; private set; }

    /// <summary>Solo en un caso <see cref="EstadoCaso.Rechazado"/>.</summary>
    public MotivoRechazo? MotivoRechazo { get; private set; }

    /// <summary>El caso anterior del que este es duplicado. Solo con <see cref="Casos.MotivoRechazo.Duplicado"/>.</summary>
    public CasoMedicoId? DuplicadoDe { get; private set; }

    /// <summary>
    /// Token de concurrencia optimista: PostgreSQL incrementa <c>xmin</c> en cada UPDATE.
    /// EF Core lo compara en el WHERE de las actualizaciones.
    /// </summary>
    public uint Version { get; private set; }

    public IReadOnlyCollection<Remision> Remisiones => _remisiones.AsReadOnly();

    public IReadOnlyCollection<Asignacion> Asignaciones => _asignaciones.AsReadOnly();

    // ── Factory ───────────────────────────────────────────────────────────────

    public static Result<CasoMedico> Radicar(
        TenantId tenantId,
        string radicado,
        TipoEvento tipoEvento,
        Identificacion trabajador,
        Identificacion empleador,
        DateTimeOffset fechaEvento,
        string municipioDivipola,
        bool esGraveOMortal,
        DateTimeOffset ahora)
    {
        if (fechaEvento > ahora)
        {
            return Result.Failure<CasoMedico>(CasoMedicoErrors.FechaEventoFutura);
        }

        CasoMedicoId id = CasoMedicoId.New();

        var caso = new CasoMedico(
            id, tenantId, radicado, tipoEvento,
            trabajador, empleador, fechaEvento,
            municipioDivipola, esGraveOMortal, ahora);

        caso.Raise(new CasoRadicadoDomainEvent(
            id, tenantId.Value, radicado, tipoEvento, fechaEvento, ahora));

        return Result.Success(caso);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    /// <summary>
    /// La validación asíncrona de la radicación pasó: ni duplicado ni sin afiliación (SPEC 05).
    /// Solo desde <see cref="EstadoCaso.Radicado"/>.
    /// </summary>
    public Result Validar(DateTimeOffset ahora)
    {
        if (Estado != EstadoCaso.Radicado)
        {
            return Result.Failure(CasoMedicoErrors.TransicionInvalida);
        }

        Estado = EstadoCaso.Validado;
        ValidadoEn = ahora;
        Raise(new CasoValidadoDomainEvent(Id, TenantId.Value, Radicado, ahora));

        return Result.Success();
    }

    /// <summary>
    /// La validación asíncrona rechazó la radicación (SPEC 05). <paramref name="duplicadoDe"/> es el caso
    /// anterior cuando el motivo es <see cref="Casos.MotivoRechazo.Duplicado"/>. Solo desde <see cref="EstadoCaso.Radicado"/>.
    /// </summary>
    public Result Rechazar(MotivoRechazo motivo, CasoMedicoId? duplicadoDe, DateTimeOffset ahora)
    {
        if (Estado != EstadoCaso.Radicado)
        {
            return Result.Failure(CasoMedicoErrors.TransicionInvalida);
        }

        Estado = EstadoCaso.Rechazado;
        MotivoRechazo = motivo;
        DuplicadoDe = duplicadoDe;
        RechazadoEn = ahora;
        Raise(new CasoRechazadoDomainEvent(Id, TenantId.Value, Radicado, motivo, duplicadoDe, ahora));

        return Result.Success();
    }

    /// <summary>
    /// El dictamen PCL quedó registrado (SPEC 08): el caso guarda su resultado administrativo (lo que la ARL necesita
    /// para liquidar) y pasa a <see cref="EstadoCaso.EnCalificacionPcl"/>. Una corrección del dictamen lo reemplaza.
    /// Un caso rechazado o cerrado no se califica.
    /// </summary>
    public Result Calificar(ResultadoCalificacion calificacion)
    {
        ArgumentNullException.ThrowIfNull(calificacion);

        if (Estado is EstadoCaso.Rechazado or EstadoCaso.Cerrado)
        {
            return Result.Failure(CasoMedicoErrors.TransicionInvalida);
        }

        Calificacion = calificacion;
        Estado = EstadoCaso.EnCalificacionPcl;

        return Result.Success();
    }

    /// <summary>
    /// Crea una nueva remisión a la IPS indicada.
    /// Rechaza si ya existe una remisión vigente a la misma IPS.
    /// </summary>
    public Result<RemisionId> Remitir(IpsId ips, string remitidoPor, DateTimeOffset ahora)
    {
        bool duplicada = _remisiones.Any(r => r.IpsId == ips && r.Vigente);
        if (duplicada)
        {
            return Result.Failure<RemisionId>(CasoMedicoErrors.RemisionDuplicada);
        }

        RemisionId remisionId = RemisionId.New();
        _remisiones.Add(new Remision(remisionId, Id, TenantId, ips, remitidoPor, ahora));

        return Result.Success(remisionId);
    }

    /// <summary>
    /// Revoca una remisión existente. Falla si no existe o si ya está revocada.
    /// </summary>
    public Result RevocarRemision(RemisionId remisionId, string revocadoPor, DateTimeOffset ahora)
    {
        Remision? remision = _remisiones.FirstOrDefault(r => r.Id == remisionId);

        if (remision is null)
        {
            return Result.Failure(CasoMedicoErrors.RemisionNoEncontrada);
        }

        if (!remision.Vigente)
        {
            return Result.Failure(CasoMedicoErrors.RemisionYaRevocada);
        }

        remision.Revocar(revocadoPor, ahora);
        return Result.Success();
    }

    /// <summary>
    /// Asigna un usuario al caso. Idempotente: si el sub ya está asignado, no hace nada.
    /// <paramref name="porIps"/> es la IPS del actor que asigna, o <c>null</c> si es la ARL (SPEC 08).
    /// </summary>
    public void Asignar(string sub, IpsId? porIps, string asignadoPor, DateTimeOffset ahora)
    {
        bool yaAsignado = _asignaciones.Any(a => a.Sub == sub);
        if (yaAsignado)
        {
            return;
        }

        _asignaciones.Add(new Asignacion(Id, TenantId, sub, porIps, asignadoPor, ahora));
    }

    /// <summary>
    /// Elimina la asignación de un usuario. Falla si el sub no está asignado. Una IPS
    /// (<paramref name="porIps"/> no nulo) solo ve, y por tanto solo borra, las asignaciones que hizo ella misma.
    /// </summary>
    public Result Desasignar(string sub, IpsId? porIps)
    {
        Asignacion? asignacion = _asignaciones.FirstOrDefault(a =>
            a.Sub == sub && (porIps is null || a.IpsId == porIps));

        if (asignacion is null)
        {
            return Result.Failure(CasoMedicoErrors.AsignacionNoEncontrada);
        }

        _asignaciones.Remove(asignacion);
        return Result.Success();
    }
}

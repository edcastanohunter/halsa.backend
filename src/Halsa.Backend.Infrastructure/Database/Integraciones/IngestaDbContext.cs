using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Dictamenes;
using Halsa.Backend.Application.Abstractions.Incapacidades;
using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.Infrastructure.Database.Clinico;
using Halsa.Backend.Infrastructure.Database.Configurations;
using Halsa.Backend.Infrastructure.Outbox;
using Halsa.Backend.SharedKernel.Primitives;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Halsa.Backend.Infrastructure.Database.Integraciones;

/// <summary>
/// El contexto de la ingesta (SPEC 05), conectado con la cadena <c>Ingesta</c> (rol <c>halsa_ingesta</c>) y el
/// <see cref="TenantSessionInterceptor"/>. Escribe en <c>atel</c>, <c>integracion</c>, <c>clinico</c>,
/// <c>outbox</c> y <c>auditoria</c> en una sola transacción, pero sobre la historia clínica el rol solo tiene
/// <c>INSERT</c>: nada aquí la lee.
///
/// Mapea solo lo que escribe, y no tiene migraciones: las tablas son de <see cref="ApplicationDbContext"/> y de
/// <see cref="ClinicalDbContext"/>. Sin reintentos automáticos: la transacción es explícita y no se repite.
/// </summary>
public sealed class IngestaDbContext(
    DbContextOptions<IngestaDbContext> options,
    ITenantContext tenantContext)
    : DbContext(options), IIngestaDbContext, IIncapacidadesDbContext, IDictamenesDbContext
{
    internal DbSet<CasoMedico> CasosMedicos => Set<CasoMedico>();

    internal DbSet<ReporteEvento> ReportesEvento => Set<ReporteEvento>();

    internal DbSet<SolicitudIntegracion> Solicitudes => Set<SolicitudIntegracion>();

    internal DbSet<SolicitudIdempotente> SolicitudesIdempotentes => Set<SolicitudIdempotente>();

    internal DbSet<MensajeEntrante> MensajesEntrantes => Set<MensajeEntrante>();

    internal DbSet<HistoriaClinicaDetalle> HistoriaClinica => Set<HistoriaClinicaDetalle>();

    internal DbSet<Soporte> Soportes => Set<Soporte>();

    internal DbSet<Incapacidad> Incapacidades => Set<Incapacidad>();

    internal DbSet<DictamenPcl> DictamenesPcl => Set<DictamenPcl>();

    internal DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    internal DbSet<AccesoAuditado> AccesosAuditados => Set<AccesoAuditado>();

    // ── IIngestaDbContext ─────────────────────────────────────────────────────

    void IIngestaDbContext.Add(CasoMedico caso) => CasosMedicos.Add(caso);

    void IIngestaDbContext.Add(ReporteEvento reporte) => ReportesEvento.Add(reporte);

    void IIngestaDbContext.Add(SolicitudIntegracion solicitud) => Solicitudes.Add(solicitud);

    void IIngestaDbContext.Add(MensajeEntrante mensaje) => MensajesEntrantes.Add(mensaje);

    void IIngestaDbContext.Add(HistoriaClinicaDetalle registro) => HistoriaClinica.Add(registro);

    void IIngestaDbContext.Encolar(IIntegrationEvent evento) => OutboxMessages.Add(OutboxMessage.Desde(evento));

    Task<IDbContextTransaction> IIngestaDbContext.BeginTransactionAsync(CancellationToken ct) =>
        Database.BeginTransactionAsync(ct);

    async Task<ResultadoGuardado> IIngestaDbContext.GuardarAsync(CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
            return ResultadoGuardado.Guardado;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IntegracionConstraints.ReporteYaRadicado
        })
        {
            // Nothing of this attempt may be saved later by accident.
            ChangeTracker.Clear();
            return ResultadoGuardado.ReporteYaRadicado;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.InsufficientPrivilege,
            MessageText: var mensaje
        } && mensaje.Contains("row-level security", StringComparison.Ordinal))
        {
            // The WITH CHECK of the clinical policy: the remission was revoked after the case was read.
            ChangeTracker.Clear();
            return ResultadoGuardado.FueraDeAlcance;
        }
    }

    async Task<string?> IIngestaDbContext.RadicadoDelReporteAsync(
        TipoReporte tipo, string numeroReporteOrigen, CancellationToken ct)
    {
        TenantId tenant = tenantContext.TenantId
            ?? throw new InvalidOperationException("La ingesta necesita un tenant en el contexto.");

        // Through the request, not casos_medicos: the case's SELECT policy reads atel.remisiones (see above).
        return await ReportesEvento
            .AsNoTracking()
            .Where(r => r.TenantId == tenant && r.Tipo == tipo && r.NumeroReporteOrigen == numeroReporteOrigen)
            .Join(Solicitudes, r => r.CasoMedicoId, s => s.CasoMedicoId, (_, s) => s.Radicado)
            .FirstOrDefaultAsync(ct);
    }

    Task<CasoVisible?> IIngestaDbContext.CasoVisibleAsync(CasoMedicoId casoId, CancellationToken ct) =>
        CasosMedicos
            .AsNoTracking()
            .Where(c => c.Id == casoId)
            .Select(c => new CasoVisible(c.TenantId, c.FechaEvento, c.Estado))
            .FirstOrDefaultAsync(ct);

    /// <summary>
    /// <c>clinico.registro_del_caso_visible</c> (migración <c>ConceptosMedicosIngestaClinico</c>): corre como su
    /// dueño, sujeto a la RLS de la HC con las variables de esta sesión, y no devuelve nada del registro.
    /// </summary>
    Task<bool> IIngestaDbContext.RegistroVisibleAsync(
        CasoMedicoId casoId, HistoriaClinicaDetalleId registroId, CancellationToken ct) =>
        Database
            .SqlQuery<bool>($"SELECT clinico.registro_del_caso_visible({registroId.Value}, {casoId.Value}) AS \"Value\"")
            .SingleAsync(ct);

    // ── IIncapacidadesDbContext (SPEC 07) ─────────────────────────────────────

    void IIncapacidadesDbContext.Add(Incapacidad incapacidad) => Incapacidades.Add(incapacidad);

    void IIncapacidadesDbContext.Add(Soporte soporte) => Soportes.Add(soporte);

    void IIncapacidadesDbContext.Add(HistoriaClinicaDetalle registro) => HistoriaClinica.Add(registro);

    void IIncapacidadesDbContext.Encolar(IIntegrationEvent evento) => OutboxMessages.Add(OutboxMessage.Desde(evento));

    Task<IDbContextTransaction> IIncapacidadesDbContext.BeginTransactionAsync(CancellationToken ct) =>
        Database.BeginTransactionAsync(ct);

    /// <summary>
    /// A transaction-scoped advisory lock on the case: <c>halsa_ingesta</c> has no <c>UPDATE</c> on
    /// <c>casos_medicos</c>, so <c>SELECT ... FOR UPDATE</c> is not an option, and it needs no privilege.
    /// </summary>
    async Task IIncapacidadesDbContext.BloquearCasoAsync(CasoMedicoId casoId, CancellationToken ct)
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("El caso se bloquea dentro de la transacción de la carga.");
        }

        string clave = $"incapacidades:{casoId.Value}";
        await Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", ct);
    }

    Task<CasoVisible?> IIncapacidadesDbContext.CasoVisibleAsync(CasoMedicoId casoId, CancellationToken ct) =>
        ((IIngestaDbContext)this).CasoVisibleAsync(casoId, ct);

    async Task<IReadOnlyList<Incapacidad>> IIncapacidadesDbContext.IncapacidadesDelCasoAsync(
        CasoMedicoId casoId, CancellationToken ct) =>
        await Incapacidades.AsNoTracking().Where(i => i.CasoMedicoId == casoId).ToListAsync(ct);

    async Task<ResultadoGuardadoIncapacidad> IIncapacidadesDbContext.GuardarAsync(CancellationToken ct)
    {
        try
        {
            await SaveChangesAsync(ct);
            return ResultadoGuardadoIncapacidad.Guardado;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
        {
            SqlState: PostgresErrorCodes.UniqueViolation,
            ConstraintName: IncapacidadConfiguration.CertificadoUnico
        })
        {
            ChangeTracker.Clear();
            return ResultadoGuardadoIncapacidad.CertificadoDuplicado;
        }
        catch (DbUpdateException ex) when (EsRls(ex))
        {
            // A WITH CHECK: the remission was revoked after the case was read.
            ChangeTracker.Clear();
            return ResultadoGuardadoIncapacidad.FueraDeAlcance;
        }
    }

    // ── IDictamenesDbContext (SPEC 08) ────────────────────────────────────────

    Task<IDbContextTransaction> IDictamenesDbContext.BeginTransactionAsync(CancellationToken ct) =>
        Database.BeginTransactionAsync(ct);

    /// <summary>The same advisory lock as the incapacidades, with its own key.</summary>
    async Task IDictamenesDbContext.BloquearCasoAsync(CasoMedicoId casoId, CancellationToken ct)
    {
        if (Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("El caso se bloquea dentro de la transacción del dictamen.");
        }

        string clave = $"dictamen-pcl:{casoId.Value}";
        await Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({clave}, 0))", ct);
    }

    /// <summary>
    /// Tracked, so saving it updates only what <c>Calificar</c> changed: <c>estado</c> and <c>calificacion_*</c>, the only
    /// columns <c>halsa_ingesta</c> may update. Without <c>Version</c>, the UPDATE needs no <c>RETURNING</c>.
    /// </summary>
    Task<CasoMedico?> IDictamenesDbContext.CasoAsync(CasoMedicoId casoId, CancellationToken ct) =>
        CasosMedicos.FirstOrDefaultAsync(c => c.Id == casoId, ct);

    Task<DictamenPcl?> IDictamenesDbContext.VigenteAsync(CasoMedicoId casoId, CancellationToken ct) =>
        DictamenesPcl.FirstOrDefaultAsync(d => d.CasoMedicoId == casoId && d.CorregidoPor == null, ct);

    void IDictamenesDbContext.Add(DictamenPcl dictamen) => DictamenesPcl.Add(dictamen);

    void IDictamenesDbContext.Add(HistoriaClinicaDetalle registro) => HistoriaClinica.Add(registro);

    Task IDictamenesDbContext.GuardarAsync(CancellationToken ct) => SaveChangesAsync(ct);

    private static bool EsRls(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege, MessageText: var mensaje } &&
        mensaje.Contains("row-level security", StringComparison.Ordinal);

    // ── Model ─────────────────────────────────────────────────────────────────

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schemas.Atel);

        // CasoMedico brings its owned collections; the ingestion never writes them.
        modelBuilder.ApplyConfiguration(new CasoMedicoConfiguration());
        modelBuilder.ApplyConfiguration(new RemisionConfiguration());
        modelBuilder.ApplyConfiguration(new AsignacionConfiguration());

        // Nothing generated by the database, so no INSERT ... RETURNING: returning xmin would evaluate the
        // SELECT policy of casos_medicos, which reads atel.remisiones, and halsa_ingesta has no grant there.
        // The ingestion only inserts the case, so it never needs the concurrency token.
        modelBuilder.Entity<CasoMedico>().Ignore(c => c.Version);

        modelBuilder.ApplyConfiguration(new ReporteEventoConfiguration());
        modelBuilder.ApplyConfiguration(new SolicitudIntegracionConfiguration());
        modelBuilder.ApplyConfiguration(new SolicitudIdempotenteConfiguration());
        modelBuilder.ApplyConfiguration(new MensajeEntranteConfiguration());
        modelBuilder.ApplyConfiguration(new HistoriaClinicaDetalleConfiguration());

        // SPEC 07: the incapacidad, its clinical record and its support, in the same transaction.
        modelBuilder.ApplyConfiguration(new SoporteConfiguration());
        modelBuilder.ApplyConfiguration(new IncapacidadConfiguration());
        // SPEC 08: the dictamen, its clinical record and the case's calificación, in the same transaction.
        modelBuilder.ApplyConfiguration(new DictamenPclConfiguration());
        modelBuilder.ApplyConfiguration(new OutboxMessageConfiguration());
        modelBuilder.ApplyConfiguration(new AccesoAuditadoConfiguration(excluirDeMigraciones: true));

        base.OnModelCreating(modelBuilder);
    }
}

using System.Reflection;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Infrastructure.Database.Clinico;

/// <summary>
/// El único contexto que alcanza el esquema <c>clinico</c>. Se conecta con la cadena <c>Clinical</c>
/// (rol <c>halsa_clinico</c>, sin <c>USAGE</c> sobre <c>atel</c>) y tiene su propio historial de
/// migraciones, así que evoluciona sin pisar a <see cref="ApplicationDbContext"/>.
///
/// El query filter replica la RLS clínica: el tenant del actor o, para una IPS, el tenant del caso
/// que tiene remitido. Use <see cref="Tenancy.NullTenantContext"/> fuera de un request (migraciones).
/// </summary>
public sealed class ClinicalDbContext(
    DbContextOptions<ClinicalDbContext> options,
    ITenantContext tenantContext)
    : DbContext(options), IClinicalDbContext
{
    /// <summary>Historial de migraciones propio: <c>clinico.__ef_migrations_history</c>.</summary>
    public const string MigrationsHistoryTable = "__ef_migrations_history";

    private static readonly string? ConfigurationsNamespace = typeof(ClinicalDbContext).Namespace;

    private static readonly MethodInfo TenantDelCasoRemitidoMethod =
        typeof(ClinicalDbContext).GetMethod(nameof(TenantDelCasoRemitido), BindingFlags.Public | BindingFlags.Static)!;

    internal DbSet<HistoriaClinicaDetalle> HistoriaClinica => Set<HistoriaClinicaDetalle>();

    internal DbSet<LlaveTenant> LlavesTenant => Set<LlaveTenant>();

    /// <summary>La metadata de los archivos soporte (SPEC 07). La ingesta los inserta; la SPEC 08 los lee aquí.</summary>
    internal DbSet<Soporte> Soportes => Set<Soporte>();

    /// <summary>Solo para insertar la auditoría de las escrituras clínicas en su misma transacción (SPEC 04).</summary>
    internal DbSet<AccesoAuditado> AccesosAuditados => Set<AccesoAuditado>();

    // ── IClinicalDbContext ────────────────────────────────────────────────────

    IQueryable<HistoriaClinicaDetalle> IClinicalDbContext.Registros => HistoriaClinica;

    void IClinicalDbContext.Add(HistoriaClinicaDetalle registro) => HistoriaClinica.Add(registro);

    // ── Función de la RLS clínica ─────────────────────────────────────────────

    /// <summary>
    /// <c>clinico.tenant_del_caso_remitido</c>: el tenant del caso si la IPS tiene una remisión vigente
    /// sobre él. Envuelve a <c>atel.tenant_del_caso_remitido</c> (migración <c>ClinicoInicial</c>),
    /// porque <c>halsa_clinico</c> no puede nombrar nada en <c>atel</c>. Solo se traduce a SQL.
    /// </summary>
    public static TenantId TenantDelCasoRemitido(CasoMedicoId casoId, string ipsId) =>
        throw new NotSupportedException($"{nameof(TenantDelCasoRemitido)} solo se usa dentro de una consulta LINQ.");

    // ── Model ─────────────────────────────────────────────────────────────────

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        ArgumentNullException.ThrowIfNull(configurationBuilder);

        configurationBuilder.DefaultTypeMapping<TenantId>().HasConversion<TenantIdConverter>();
        configurationBuilder.DefaultTypeMapping<CasoMedicoId>().HasConversion<CasoMedicoIdConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.HasDefaultSchema(Schemas.Clinico);
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ClinicalDbContext).Assembly,
            type => type.Namespace == ConfigurationsNamespace);

        // auditoria.accesos belongs to ApplicationDbContext's migrations; this context only inserts into it.
        modelBuilder.ApplyConfiguration(new AccesoAuditadoConfiguration(excluirDeMigraciones: true));

        modelBuilder.HasDbFunction(TenantDelCasoRemitidoMethod)
            .HasSchema(Schemas.Clinico)
            .HasName("tenant_del_caso_remitido");

        // ── Query filter: mirrors the clinical RLS policy. ───────────────────────
        // Fails closed: with no resolved context neither branch matches.
        modelBuilder.Entity<HistoriaClinicaDetalle>().HasQueryFilter(h =>
            (TieneTenant && h.TenantId == CurrentTenant) ||
            (TieneIps && h.TenantId == TenantDelCasoRemitido(h.CasoMedicoId, CurrentIps)));

        // The same rule for the supports: their RLS policy is the clinical history's.
        modelBuilder.Entity<Soporte>().HasQueryFilter(s =>
            (TieneTenant && s.TenantId == CurrentTenant) ||
            (TieneIps && s.TenantId == TenantDelCasoRemitido(s.CasoMedicoId, CurrentIps)));

        base.OnModelCreating(modelBuilder);
    }

    private bool TieneTenant => tenantContext.Resuelto && tenantContext.TenantId is not null;

    private TenantId CurrentTenant => TieneTenant ? tenantContext.TenantId!.Value : default;

    private bool TieneIps => tenantContext.Resuelto && tenantContext.IpsId is not null;

    private string CurrentIps => TieneIps ? tenantContext.IpsId!.Value.Value : string.Empty;
}

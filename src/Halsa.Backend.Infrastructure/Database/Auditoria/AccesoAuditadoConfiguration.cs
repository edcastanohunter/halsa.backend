using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Auditoria;

/// <summary>
/// Configuración compartida por los dos contextos: cada uno escribe su fila en su propia transacción.
/// Vive fuera de los namespaces que los contextos escanean, así que cada uno la aplica a mano.
/// La tabla la crea la migración <c>AuditoriaAccesos</c> de <see cref="ApplicationDbContext"/>
/// (particionada, con SQL propio); <see cref="Clinico.ClinicalDbContext"/> la excluye de sus migraciones.
/// </summary>
internal sealed class AccesoAuditadoConfiguration(bool excluirDeMigraciones)
    : IEntityTypeConfiguration<AccesoAuditado>
{
    public const string Tabla = "accesos";

    /// <summary>The owning side. Parameterless also keeps the contexts' assembly scan from warning about this type.</summary>
    public AccesoAuditadoConfiguration()
        : this(excluirDeMigraciones: false)
    {
    }

    public void Configure(EntityTypeBuilder<AccesoAuditado> builder)
    {
        builder.ToTable(Tabla, Schemas.Auditoria, table =>
        {
            if (excluirDeMigraciones)
            {
                table.ExcludeFromMigrations();
            }
        });

        // La partición por ocurrido_en obliga a incluirla en la PK.
        builder.HasKey(a => new { a.Id, a.OcurridoEn }).HasName("pk_accesos");

        // Nada generado por la base: el INSERT no lleva RETURNING, que halsa_clinico (sin SELECT) no podría ejecutar.
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.OcurridoEn).ValueGeneratedNever();

        builder.Property(a => a.TenantId).HasMaxLength(20);
        builder.Property(a => a.ActorId).HasMaxLength(64);
        builder.Property(a => a.ActorTipo).HasMaxLength(20);
        builder.Property(a => a.ActorTenantId).HasMaxLength(20);
        builder.Property(a => a.ActorIpsId).HasMaxLength(12);
        builder.Property(a => a.ActorRoles).HasColumnType("text[]");
        builder.Property(a => a.ClientId).HasMaxLength(100);
        builder.Property(a => a.Ip).HasColumnType("inet");
        builder.Property(a => a.UserAgent).HasMaxLength(300);
        builder.Property(a => a.CorrelationId).HasMaxLength(64);
        builder.Property(a => a.Accion).HasMaxLength(20);
        builder.Property(a => a.Resultado).HasMaxLength(20);
        builder.Property(a => a.RecursoTipo).HasMaxLength(30);
        builder.Property(a => a.RecursoId).HasMaxLength(64);
        builder.Property(a => a.Motivo).HasMaxLength(500);
        builder.Property(a => a.Acr).HasMaxLength(10);
        builder.Property(a => a.HashRegistro);

        builder.HasIndex(a => new { a.TenantId, a.CasoMedicoId, a.OcurridoEn })
            .HasDatabaseName("ix_accesos_tenant_caso_ocurrido_en");
        builder.HasIndex(a => new { a.TenantId, a.ActorId, a.OcurridoEn })
            .HasDatabaseName("ix_accesos_tenant_actor_ocurrido_en");
    }
}

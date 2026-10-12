using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class AsignacionConfiguration : IEntityTypeConfiguration<Asignacion>
{
    public void Configure(EntityTypeBuilder<Asignacion> builder)
    {
        builder.ToTable("asignaciones", Schemas.Atel);

        // Natural key: one sub can only be assigned once per case.
        builder.HasKey(a => new { a.CasoMedicoId, a.Sub })
            .HasName("pk_asignaciones");

        builder.Property(a => a.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(a => a.Sub)
            .HasColumnName("sub")
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(a => a.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .IsRequired()
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(a => a.AsignadoEn)
            .HasColumnName("asignado_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(a => a.AsignadoPor)
            .HasColumnName("asignado_por")
            .HasMaxLength(200)
            .IsRequired();
    }
}

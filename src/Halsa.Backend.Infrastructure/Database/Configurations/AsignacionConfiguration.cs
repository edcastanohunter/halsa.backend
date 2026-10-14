using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class AsignacionConfiguration : IEntityTypeConfiguration<Asignacion>
{
    // EF applies a converter of the underlying struct to the nullable property, and leaves null as NULL.
    private static readonly ValueConverter<IpsId, string> IpsConverter = new(v => v.Value, v => new IpsId(v));

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

        // SPEC 08: the IPS that assigned; null for the ARL's staff.
        builder.Property(a => a.IpsId)
            .HasColumnName("ips_id")
            .HasMaxLength(12)
            .HasConversion(IpsConverter);

        builder.Property(a => a.AsignadoEn)
            .HasColumnName("asignado_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(a => a.AsignadoPor)
            .HasColumnName("asignado_por")
            .HasMaxLength(200)
            .IsRequired();
    }
}

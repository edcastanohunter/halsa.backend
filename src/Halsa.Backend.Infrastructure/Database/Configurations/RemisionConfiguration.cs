using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class RemisionConfiguration : IEntityTypeConfiguration<Remision>
{
    public void Configure(EntityTypeBuilder<Remision> builder)
    {
        builder.ToTable("remisiones", Schemas.Atel);

        builder.HasKey(r => r.Id)
            .HasName("pk_remisiones");

        builder.Property(r => r.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new RemisionId(v));

        builder.Property(r => r.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(r => r.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .IsRequired()
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(r => r.IpsId)
            .HasColumnName("ips_id")
            .HasMaxLength(12)
            .IsRequired()
            .HasConversion(v => v.Value, v => new IpsId(v));

        builder.Property(r => r.RemitidoPor)
            .HasColumnName("remitido_por")
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(r => r.RemitidoEn)
            .HasColumnName("remitido_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(r => r.RevocadaEn)
            .HasColumnName("revocada_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(r => r.RevocadaPor)
            .HasColumnName("revocada_por")
            .HasMaxLength(200);

        // Vigente is a computed property (RevocadaEn IS NULL) — not stored separately.
        builder.Ignore(r => r.Vigente);

        builder.HasIndex(r => r.CasoMedicoId)
            .HasDatabaseName("ix_remisiones_caso_medico_id");

        // Sirve a la subconsulta EXISTS de la política RLS de la IPS.
        builder.HasIndex(r => new { r.IpsId, r.CasoMedicoId })
            .HasFilter("revocada_en IS NULL")
            .HasDatabaseName("ix_remisiones_ips_vigentes");
    }
}

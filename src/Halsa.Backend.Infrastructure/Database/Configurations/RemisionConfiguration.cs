using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class RemisionConfiguration : IEntityTypeConfiguration<Remision>
{
    public void Configure(EntityTypeBuilder<Remision> builder)
    {
        builder.ToTable("remision", Schemas.Atel);

        builder.HasKey(r => r.Id)
            .HasName("pk_remision");

        builder.Property(r => r.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new RemisionId(v));

        builder.Property(r => r.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

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
            .HasDatabaseName("ix_remision_caso_medico_id");

        builder.HasIndex(r => r.IpsId)
            .HasDatabaseName("ix_remision_ips_id");
    }
}

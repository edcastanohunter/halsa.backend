using Halsa.Backend.Infrastructure.Clinico.Crypto;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Clinico;

internal sealed class LlaveTenantConfiguration : IEntityTypeConfiguration<LlaveTenant>
{
    public void Configure(EntityTypeBuilder<LlaveTenant> builder)
    {
        builder.ToTable("llaves_tenant", Schemas.Clinico);

        builder.HasKey(l => l.KeyId)
            .HasName("pk_llaves_tenant");

        builder.Property(l => l.KeyId)
            .HasColumnName("key_id")
            .HasMaxLength(64);

        builder.Property(l => l.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20);

        builder.Property(l => l.Proposito)
            .HasColumnName("proposito")
            .HasMaxLength(20);

        builder.Property(l => l.Version)
            .HasColumnName("version");

        builder.Property(l => l.KekId)
            .HasColumnName("kek_id")
            .HasMaxLength(64);

        builder.Property(l => l.DekEnvuelta)
            .HasColumnName("dek_envuelta")
            .HasColumnType("bytea");

        builder.Property(l => l.CreadaEn)
            .HasColumnName("creada_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(l => l.Activa)
            .HasColumnName("activa");

        // One active DEK per (tenant, purpose). DekStore's INSERT ... ON CONFLICT DO NOTHING
        // relies on it so that concurrent first uses converge on a single key.
        builder.HasIndex(l => new { l.TenantId, l.Proposito })
            .IsUnique()
            .HasFilter("activa")
            .HasDatabaseName("ux_llaves_tenant_activa");
    }
}

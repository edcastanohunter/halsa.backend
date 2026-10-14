using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Clinico;

/// <summary>
/// <c>clinico.soportes</c> (SPEC 07). La crea la historia de migraciones de <see cref="ClinicalDbContext"/>, que la lee
/// (SPEC 08); <c>IngestaDbContext</c> aplica esta misma configuración para insertarla. Nada lo genera la base, así que
/// el insert no necesita <c>RETURNING</c>.
/// </summary>
internal sealed class SoporteConfiguration : IEntityTypeConfiguration<Soporte>
{
    public void Configure(EntityTypeBuilder<Soporte> builder)
    {
        builder.ToTable("soportes", Schemas.Clinico);

        builder.HasKey(s => s.Id).HasName("pk_soportes");

        builder.Property(s => s.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();

        builder.Property(s => s.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .IsRequired()
            .HasConversion(v => v.Value, v => new TenantId(v));

        // Only the id: no navigation, and no FK, towards atel.
        builder.Property(s => s.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(s => s.Tipo)
            .HasColumnName("tipo")
            .HasMaxLength(20)
            .HasConversion(v => Codigo(v), v => Parse(v));

        builder.Property(s => s.Ruta).HasColumnName("ruta").HasMaxLength(200).IsRequired();
        builder.Property(s => s.KeyId).HasColumnName("key_id").HasMaxLength(64).IsRequired();
        builder.Property(s => s.Sha256Claro).HasColumnName("sha256_claro").HasColumnType("bytea").IsRequired();
        builder.Property(s => s.Tamano).HasColumnName("tamano").HasColumnType("bigint");
        builder.Property(s => s.SubidoEn).HasColumnName("subido_en").HasColumnType("timestamp with time zone");
        builder.Property(s => s.RetenerHasta).HasColumnName("retener_hasta").HasColumnType("date");

        builder.HasIndex(s => new { s.TenantId, s.CasoMedicoId })
            .HasDatabaseName("ix_soportes_tenant_caso");
    }

    private static string Codigo(TipoSoporte tipo) => tipo switch
    {
        TipoSoporte.Incapacidad => "INCAPACIDAD",
        _ => throw new ArgumentOutOfRangeException(nameof(tipo), tipo, null)
    };

    private static TipoSoporte Parse(string codigo) => codigo switch
    {
        "INCAPACIDAD" => TipoSoporte.Incapacidad,
        _ => throw new ArgumentOutOfRangeException(nameof(codigo), codigo, null)
    };
}

using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Clinico;

internal sealed class HistoriaClinicaDetalleConfiguration : IEntityTypeConfiguration<HistoriaClinicaDetalle>
{
    // Defined on the non-nullable id so it also serves the nullable CorrigeA: EF handles the nulls.
    private static readonly ValueConverter<HistoriaClinicaDetalleId, Guid> IdConverter =
        new(v => v.Value, v => new HistoriaClinicaDetalleId(v));

    public void Configure(EntityTypeBuilder<HistoriaClinicaDetalle> builder)
    {
        builder.ToTable("historia_clinica_detalle", Schemas.Clinico);

        builder.HasKey(h => h.Id)
            .HasName("pk_historia_clinica_detalle");

        builder.Property(h => h.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(IdConverter);

        builder.Property(h => h.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .IsRequired()
            .HasConversion(v => v.Value, v => new TenantId(v));

        // Only the id: there is no navigation, and no FK, towards atel.
        builder.Property(h => h.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(h => h.Tipo)
            .HasColumnName("tipo")
            .HasMaxLength(30)
            .HasConversion<string>();

        builder.Property(h => h.AtendidoEn)
            .HasColumnName("atendido_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(h => h.RegistradoEn)
            .HasColumnName("registrado_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(h => h.RetenerHasta)
            .HasColumnName("retener_hasta")
            .HasColumnType("date");

        builder.Property(h => h.CorrigeA)
            .HasColumnName("corrige_a")
            .HasColumnType("uuid")
            .HasConversion(IdConverter);

        builder.Property(h => h.DiagnosticoIndiceCiego)
            .HasColumnName("diagnostico_indice_ciego")
            .HasColumnType("bytea");

        // ── Owned types ────────────────────────────────────────────────────────

        builder.OwnsOne(h => h.Profesional, o =>
        {
            o.Property(p => p.Sub)
                .HasColumnName("profesional_sub")
                .HasMaxLength(64)
                .IsRequired();
            o.Property(p => p.TipoDocumento)
                .HasColumnName("profesional_tipo_documento")
                .HasMaxLength(10)
                .IsRequired();
            o.Property(p => p.NumeroDocumento)
                .HasColumnName("profesional_numero_documento")
                .HasMaxLength(30)
                .IsRequired();
            o.Property(p => p.RegistroRethus)
                .HasColumnName("profesional_registro_rethus")
                .HasMaxLength(30);
        });
        builder.Navigation(h => h.Profesional).IsRequired();

        builder.OwnsOne(h => h.Contenido, o => MapCampoCifrado(o, "contenido"));
        builder.Navigation(h => h.Contenido).IsRequired();

        builder.OwnsOne(h => h.DiagnosticoPrincipal, o => MapCampoCifrado(o, "diagnostico"));

        // ── Corrections ────────────────────────────────────────────────────────

        builder.HasOne<HistoriaClinicaDetalle>()
            .WithMany()
            .HasForeignKey(h => h.CorrigeA)
            .HasConstraintName("fk_historia_clinica_detalle_corrige_a")
            .OnDelete(DeleteBehavior.Restrict);

        // ── Indexes ────────────────────────────────────────────────────────────

        builder.HasIndex(h => new { h.TenantId, h.CasoMedicoId, h.RegistradoEn })
            .HasDatabaseName("ix_historia_clinica_detalle_tenant_caso_registrado_en");

        builder.HasIndex(h => new { h.TenantId, h.DiagnosticoIndiceCiego })
            .HasDatabaseName("ix_historia_clinica_detalle_tenant_indice_ciego");

        builder.HasIndex(h => h.CorrigeA)
            .HasDatabaseName("ix_historia_clinica_detalle_corrige_a");
    }

    private static void MapCampoCifrado<TOwner>(OwnedNavigationBuilder<TOwner, CampoCifrado> o, string prefijo)
        where TOwner : class
    {
        o.Property(c => c.Ciphertext)
            .HasColumnName($"{prefijo}_ct")
            .HasColumnType("bytea")
            .IsRequired();
        o.Property(c => c.Nonce)
            .HasColumnName($"{prefijo}_nonce")
            .HasColumnType("bytea")
            .IsRequired();
        o.Property(c => c.Tag)
            .HasColumnName($"{prefijo}_tag")
            .HasColumnType("bytea")
            .IsRequired();
        o.Property(c => c.KeyId)
            .HasColumnName($"{prefijo}_key_id")
            .HasMaxLength(64)
            .IsRequired();
    }
}

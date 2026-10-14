using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

/// <summary>
/// <c>atel.incapacidades</c> (SPEC 07). <see cref="ApplicationDbContext"/> la crea y la lee como <c>halsa_app</c>;
/// <c>IngestaDbContext</c> aplica esta configuración para insertarla. Nada lo genera la base: el insert de
/// <c>halsa_ingesta</c> no necesita <c>RETURNING</c>.
/// </summary>
internal sealed class IncapacidadConfiguration : IEntityTypeConfiguration<Incapacidad>
{
    /// <summary>La restricción de <c>(tenant_id, ips_emisora_reps, numero_certificado)</c>: un certificado se carga una vez.</summary>
    public const string CertificadoUnico = "ux_incapacidades_tenant_ips_certificado";

    // Defined on the non-nullable id so it also serves the nullable ProrrogaDe: EF handles the nulls.
    private static readonly ValueConverter<IncapacidadId, Guid> IdConverter = new(v => v.Value, v => new IncapacidadId(v));

    public void Configure(EntityTypeBuilder<Incapacidad> builder)
    {
        builder.ToTable("incapacidades", Schemas.Atel);

        builder.HasKey(i => i.Id).HasName("pk_incapacidades");

        builder.Property(i => i.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(IdConverter)
            .ValueGeneratedNever();

        builder.Property(i => i.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(i => i.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(i => i.NumeroCertificado).HasColumnName("numero_certificado").HasMaxLength(40);

        builder.OwnsOne(i => i.IpsEmisora, o =>
        {
            o.Property(p => p.Reps).HasColumnName("ips_emisora_reps").HasMaxLength(12).IsRequired();
            o.Property(p => p.Nombre).HasColumnName("ips_emisora_nombre").HasMaxLength(200);
        });
        builder.Navigation(i => i.IpsEmisora).IsRequired();

        builder.Property(i => i.Tipo)
            .HasColumnName("tipo")
            .HasMaxLength(10)
            .HasConversion(v => Codigo(v), v => Enum.Parse<TipoIncapacidad>(v, true));

        builder.Property(i => i.ProrrogaDe)
            .HasColumnName("prorroga_de")
            .HasColumnType("uuid")
            .HasConversion(IdConverter);

        builder.Property(i => i.CadenaId)
            .HasColumnName("cadena_id")
            .HasColumnType("uuid")
            .HasConversion(IdConverter);

        builder.Property(i => i.FechaInicio).HasColumnName("fecha_inicio").HasColumnType("date");
        builder.Property(i => i.Dias).HasColumnName("dias");
        builder.Property(i => i.FechaFin).HasColumnName("fecha_fin").HasColumnType("date");
        builder.Property(i => i.DiasAcumulados).HasColumnName("dias_acumulados");

        builder.Property(i => i.Modalidad)
            .HasColumnName("modalidad")
            .HasMaxLength(15)
            .HasConversion(v => Codigo(v), v => Enum.Parse<ModalidadIncapacidad>(v, true));

        builder.Property(i => i.Estado)
            .HasColumnName("estado")
            .HasMaxLength(15)
            .HasConversion(v => Codigo(v), v => Enum.Parse<EstadoIncapacidad>(v, true));

        builder.OwnsOne(i => i.Profesional, o =>
        {
            o.Property(p => p.TipoDocumento).HasColumnName("profesional_tipo_documento").HasMaxLength(10).IsRequired();
            o.Property(p => p.NumeroDocumento).HasColumnName("profesional_numero_documento").HasMaxLength(30).IsRequired();
            o.Property(p => p.Nombre).HasColumnName("profesional_nombre").HasMaxLength(200);
            o.Property(p => p.RegistroRethus).HasColumnName("profesional_registro_rethus").HasMaxLength(30).IsRequired();
        });
        builder.Navigation(i => i.Profesional).IsRequired();

        builder.Property(i => i.DiagnosticoCategoriaIndice)
            .HasColumnName("diagnostico_categoria_indice")
            .HasColumnType("bytea");

        // Ids in clinico, without FK: atel never points into the clinical schema.
        builder.Property(i => i.SoporteId).HasColumnName("soporte_id").HasColumnType("uuid");
        builder.Property(i => i.RegistroClinicoId).HasColumnName("registro_clinico_id").HasColumnType("uuid");

        builder.Property(i => i.RegistradaEn).HasColumnName("registrada_en").HasColumnType("timestamp with time zone");
        builder.Property(i => i.RegistradaPor).HasColumnName("registrada_por").HasMaxLength(64);
        builder.Property(i => i.ClientId).HasColumnName("client_id").HasMaxLength(100);

        // ── Relationships ──────────────────────────────────────────────────────

        builder.HasOne<CasoMedico>()
            .WithMany()
            .HasForeignKey(i => i.CasoMedicoId)
            .HasConstraintName("fk_incapacidades_casos_medicos")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Incapacidad>()
            .WithMany()
            .HasForeignKey(i => i.ProrrogaDe)
            .HasConstraintName("fk_incapacidades_prorroga_de")
            .OnDelete(DeleteBehavior.Restrict);

        // ── Indexes ────────────────────────────────────────────────────────────

        builder.HasIndex(i => new { i.TenantId, i.CasoMedicoId, i.FechaInicio })
            .HasDatabaseName("ix_incapacidades_tenant_caso_fecha_inicio");

        builder.HasIndex(i => i.ProrrogaDe).HasDatabaseName("ix_incapacidades_prorroga_de");

        // CertificadoUnico mixes the owner's columns with IpsEmisora's: EF cannot index across an owned type, so the
        // AtelIncapacidades migration creates it in SQL.
    }

    private static string Codigo<TEnum>(TEnum valor) where TEnum : struct, Enum =>
        valor.ToString().ToUpperInvariant();
}

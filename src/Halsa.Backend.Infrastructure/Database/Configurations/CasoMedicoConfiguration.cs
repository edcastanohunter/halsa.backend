using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class CasoMedicoConfiguration : IEntityTypeConfiguration<CasoMedico>
{
    // Defined on the non-nullable id so it also serves the nullable DuplicadoDe: EF handles the nulls.
    private static readonly ValueConverter<CasoMedicoId, Guid> IdConverter =
        new(v => v.Value, v => new CasoMedicoId(v));

    public void Configure(EntityTypeBuilder<CasoMedico> builder)
    {
        builder.ToTable("casos_medicos", Schemas.Atel);

        builder.HasKey(c => c.Id)
            .HasName("pk_casos_medicos");

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .IsRequired()
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(c => c.Radicado)
            .HasColumnName("radicado")
            .HasMaxLength(40)
            .IsRequired();

        builder.Property(c => c.TipoEvento)
            .HasColumnName("tipo_evento")
            .HasMaxLength(20)
            .HasConversion<string>();

        builder.Property(c => c.FechaEvento)
            .HasColumnName("fecha_evento")
            .HasColumnType("timestamp with time zone");

        builder.Property(c => c.MunicipioDivipola)
            .HasColumnName("municipio_divipola")
            .HasColumnType("char(5)")
            .IsRequired();

        builder.Property(c => c.Estado)
            .HasColumnName("estado")
            .HasMaxLength(30)
            .HasConversion<string>();

        builder.Property(c => c.EsGraveOMortal)
            .HasColumnName("es_grave_o_mortal");

        builder.Property(c => c.RadicadoEn)
            .HasColumnName("radicado_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(c => c.ValidadoEn)
            .HasColumnName("validado_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(c => c.RechazadoEn)
            .HasColumnName("rechazado_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(c => c.MotivoRechazo)
            .HasColumnName("motivo_rechazo")
            .HasMaxLength(30)
            .HasConversion<string>();

        builder.Property(c => c.DuplicadoDe)
            .HasColumnName("duplicado_de")
            .HasColumnType("uuid")
            .HasConversion(IdConverter);

        // ── xmin concurrency token ─────────────────────────────────────────────
        // PostgreSQL increments xmin on every UPDATE so EF Core can detect concurrent
        // modifications without a separate version column.
        // Npgsql maps a uint row version to the xmin system column; migrations never create it.
        builder.Property(c => c.Version)
            .IsRowVersion();

        // ── Owned types ────────────────────────────────────────────────────────

        builder.OwnsOne(c => c.Trabajador, o =>
        {
            o.Property(t => t.Tipo)
                .HasColumnName("trabajador_tipo")
                .HasMaxLength(10)
                .IsRequired();
            o.Property(t => t.Numero)
                .HasColumnName("trabajador_numero")
                .HasMaxLength(30)
                .IsRequired();
        });

        builder.OwnsOne(c => c.Empleador, o =>
        {
            o.Property(e => e.Tipo)
                .HasColumnName("empleador_tipo")
                .HasMaxLength(10)
                .IsRequired();
            o.Property(e => e.Numero)
                .HasColumnName("empleador_numero")
                .HasMaxLength(30)
                .IsRequired();
        });

        builder.OwnsOne(c => c.Calificacion, o =>
        {
            o.Property(q => q.PorcentajePcl)
                .HasColumnName("calificacion_pcl")
                .HasColumnType("numeric(5,2)");
            o.Property(q => q.FechaEstructuracion)
                .HasColumnName("calificacion_fecha_estructuracion");
            o.Property(q => q.Origen)
                .HasColumnName("calificacion_origen")
                .HasMaxLength(50);
        });

        // ── Navigation: Remisiones ─────────────────────────────────────────────
        builder.HasMany(c => c.Remisiones)
            .WithOne()
            .HasForeignKey(r => r.CasoMedicoId)
            .HasConstraintName("fk_remisiones_casos_medicos")
            .OnDelete(DeleteBehavior.Cascade);

        // EF Core needs access to the private backing field.
        builder.Navigation(c => c.Remisiones)
            .HasField("_remisiones")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // ── Navigation: Asignaciones ───────────────────────────────────────────
        builder.HasMany(c => c.Asignaciones)
            .WithOne()
            .HasForeignKey(a => a.CasoMedicoId)
            .HasConstraintName("fk_asignaciones_casos_medicos")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.Asignaciones)
            .HasField("_asignaciones")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // ── Indexes ────────────────────────────────────────────────────────────

        builder.HasIndex(c => new { c.TenantId, c.Radicado })
            .IsUnique()
            .HasDatabaseName("ux_casos_medicos_tenant_radicado");

        builder.HasIndex(c => new { c.TenantId, c.Estado, c.RadicadoEn })
            .IsDescending(false, false, true)
            .HasDatabaseName("ix_casos_medicos_tenant_estado_radicado_en");

        // ix_casos_medicos_deduplicacion (tenant_id, trabajador_tipo, trabajador_numero, fecha_evento) lo crea
        // la migración IntegracionEsquema con SQL: EF no admite un índice que mezcle columnas del dueño y de un owned.
    }
}

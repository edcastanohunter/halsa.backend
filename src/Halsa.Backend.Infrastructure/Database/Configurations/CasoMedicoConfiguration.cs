using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

internal sealed class CasoMedicoConfiguration : IEntityTypeConfiguration<CasoMedico>
{
    public void Configure(EntityTypeBuilder<CasoMedico> builder)
    {
        builder.ToTable("caso_medico", Schemas.Atel);

        builder.HasKey(c => c.Id)
            .HasName("pk_caso_medico");

        builder.Property(c => c.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(100)
            .IsRequired()
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(c => c.Radicado)
            .HasColumnName("radicado")
            .HasMaxLength(100)
            .IsRequired();

        builder.Property(c => c.TipoEvento)
            .HasColumnName("tipo_evento")
            .HasConversion<int>();

        builder.Property(c => c.FechaEvento)
            .HasColumnName("fecha_evento")
            .HasColumnType("timestamp with time zone");

        builder.Property(c => c.MunicipioDivipola)
            .HasColumnName("municipio_divipola")
            .HasMaxLength(10)
            .IsRequired();

        builder.Property(c => c.Estado)
            .HasColumnName("estado")
            .HasConversion<int>();

        builder.Property(c => c.EsGraveOMortal)
            .HasColumnName("es_grave_o_mortal");

        builder.Property(c => c.RadicadoEn)
            .HasColumnName("radicado_en")
            .HasColumnType("timestamp with time zone");

        // ── xmin concurrency token ─────────────────────────────────────────────
        // PostgreSQL increments xmin on every UPDATE so EF Core can detect concurrent
        // modifications without a separate version column.
        builder.Property(c => c.Version)
            .HasColumnName("xmin")
            .HasColumnType("xid")
            .ValueGeneratedOnAddOrUpdate()
            .IsConcurrencyToken();

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
            .HasConstraintName("fk_remision_caso_medico")
            .OnDelete(DeleteBehavior.Cascade);

        // EF Core needs access to the private backing field.
        builder.Navigation(c => c.Remisiones)
            .HasField("_remisiones")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // ── Navigation: Asignaciones ───────────────────────────────────────────
        builder.HasMany(c => c.Asignaciones)
            .WithOne()
            .HasForeignKey(a => a.CasoMedicoId)
            .HasConstraintName("fk_asignacion_caso_medico")
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(c => c.Asignaciones)
            .HasField("_asignaciones")
            .UsePropertyAccessMode(PropertyAccessMode.Field);

        // ── Indexes ────────────────────────────────────────────────────────────

        builder.HasIndex(c => c.TenantId)
            .HasDatabaseName("ix_caso_medico_tenant_id");

        builder.HasIndex(c => new { c.TenantId, c.Estado })
            .HasDatabaseName("ix_caso_medico_tenant_estado");
    }
}

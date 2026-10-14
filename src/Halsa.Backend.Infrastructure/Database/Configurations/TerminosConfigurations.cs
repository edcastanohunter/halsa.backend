using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.Infrastructure.Terminos;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

// The codes of the enums reuse the dictamen convention: the name in capitals, with '_' between words.

/// <summary><c>sst.reglas_termino</c> (SPEC 09): global, sin tenant. <c>halsa_app</c> y <c>halsa_worker</c> la leen.</summary>
internal sealed class ReglaTerminoConfiguration : IEntityTypeConfiguration<ReglaTermino>
{
    internal static readonly ValueConverter<UnidadTermino, string> UnidadConverter =
        new(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<UnidadTermino>(v));

    public void Configure(EntityTypeBuilder<ReglaTermino> builder)
    {
        builder.ToTable("reglas_termino", Schemas.Sst);

        builder.HasKey(r => r.Id).HasName("pk_reglas_termino");
        builder.Property(r => r.Id).HasColumnName("id").ValueGeneratedNever();

        builder.Property(r => r.Codigo).HasColumnName("codigo").HasMaxLength(50);
        builder.Property(r => r.EventoDisparador).HasColumnName("evento_disparador").HasMaxLength(50);
        builder.Property(r => r.Cantidad).HasColumnName("cantidad");
        builder.Property(r => r.Unidad).HasColumnName("unidad").HasMaxLength(10).HasConversion(UnidadConverter);
        builder.Property(r => r.AlertaCantidad).HasColumnName("alerta_cantidad");
        builder.Property(r => r.AlertaUnidad).HasColumnName("alerta_unidad").HasMaxLength(10).HasConversion(UnidadConverter);
        builder.Property(r => r.BaseLegal).HasColumnName("base_legal").HasMaxLength(300);
        builder.Property(r => r.VigenteDesde).HasColumnName("vigente_desde");
        builder.Property(r => r.VigenteHasta).HasColumnName("vigente_hasta");

        builder.HasIndex(r => new { r.Codigo, r.VigenteDesde })
            .HasDatabaseName("ux_reglas_termino_codigo_vigente_desde")
            .IsUnique();
    }
}

/// <summary><c>sst.festivos</c> (SPEC 09): global. <c>FestivosJob</c> inserta los <c>CALCULADO</c> sin pisar los <c>MANUAL</c>.</summary>
internal sealed class FestivoConfiguration : IEntityTypeConfiguration<FestivoFila>
{
    public void Configure(EntityTypeBuilder<FestivoFila> builder)
    {
        builder.ToTable("festivos", Schemas.Sst);

        builder.HasKey(f => f.Fecha).HasName("pk_festivos");
        builder.Property(f => f.Fecha).HasColumnName("fecha");
        builder.Property(f => f.Descripcion).HasColumnName("descripcion").HasMaxLength(200);
        builder.Property(f => f.Origen).HasColumnName("origen").HasMaxLength(10);
    }
}

/// <summary>
/// <c>sst.plazos</c> (SPEC 09), con RLS por tenant. <see cref="ApplicationDbContext"/> los abre, los lee y los cumple
/// como <c>halsa_app</c>; el barrido los actualiza en SQL como <c>halsa_worker</c>.
/// </summary>
internal sealed class PlazoConfiguration : IEntityTypeConfiguration<Plazo>
{
    /// <summary>Un plazo por caso, regla y referencia (los null cuentan como iguales).</summary>
    public const string Unico = "ux_plazos_caso_regla_referencia";

    public void Configure(EntityTypeBuilder<Plazo> builder)
    {
        builder.ToTable("plazos", Schemas.Sst);

        builder.HasKey(p => p.Id).HasName("pk_plazos");

        builder.Property(p => p.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new PlazoId(v))
            .ValueGeneratedNever();

        builder.Property(p => p.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(p => p.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.HasOne<CasoMedico>()
            .WithMany()
            .HasForeignKey(p => p.CasoMedicoId)
            .HasConstraintName("fk_plazos_casos_medicos")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(p => p.ReglaId).HasColumnName("regla_id");

        builder.HasOne<ReglaTermino>()
            .WithMany()
            .HasForeignKey(p => p.ReglaId)
            .HasConstraintName("fk_plazos_reglas_termino")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(p => p.ReglaCodigo).HasColumnName("regla_codigo").HasMaxLength(50);
        builder.Property(p => p.ReferenciaId).HasColumnName("referencia_id");
        builder.Property(p => p.Radicado).HasColumnName("radicado").HasMaxLength(40);
        builder.Property(p => p.IniciaEn).HasColumnName("inicia_en").HasColumnType("timestamp with time zone");
        builder.Property(p => p.VenceEn).HasColumnName("vence_en").HasColumnType("timestamp with time zone");
        builder.Property(p => p.AlertaEn).HasColumnName("alerta_en").HasColumnType("timestamp with time zone");

        builder.Property(p => p.Estado)
            .HasColumnName("estado")
            .HasMaxLength(12)
            .HasConversion(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<EstadoPlazo>(v));

        builder.Property(p => p.AlertadoEn).HasColumnName("alertado_en").HasColumnType("timestamp with time zone");
        builder.Property(p => p.CumplidoEn).HasColumnName("cumplido_en").HasColumnType("timestamp with time zone");
        builder.Property(p => p.CumplidoPor).HasColumnName("cumplido_por").HasMaxLength(64);
        builder.Property(p => p.NotaCumplimiento).HasColumnName("nota_cumplimiento").HasMaxLength(Plazo.MaximoNota);
        builder.Property(p => p.Extemporaneo).HasColumnName("extemporaneo");
        builder.Property(p => p.UltimaFechaFin).HasColumnName("ultima_fecha_fin");

        // NULLS NOT DISTINCT (in the migration): the radication's plazos have no referencia and must still be unique.
        builder.HasIndex(p => new { p.CasoMedicoId, p.ReglaCodigo, p.ReferenciaId })
            .HasDatabaseName(Unico)
            .IsUnique()
            .AreNullsDistinct(false);

        // The sweep only ever scans open plazos.
        builder.HasIndex(p => p.VenceEn)
            .HasDatabaseName("ix_plazos_abiertos_vence_en")
            .HasFilter("estado = 'ABIERTO'");

        builder.HasIndex(p => new { p.TenantId, p.Estado, p.VenceEn })
            .HasDatabaseName("ix_plazos_tenant_estado_vence_en");
    }
}

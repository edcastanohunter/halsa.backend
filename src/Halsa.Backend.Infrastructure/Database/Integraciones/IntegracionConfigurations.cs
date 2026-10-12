using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Integraciones;

/// <summary>
/// Configuraciones de las tablas de la ingesta (SPEC 05). Viven fuera de los namespaces que los contextos
/// escanean: <see cref="ApplicationDbContext"/> las aplica para crear las tablas en sus migraciones e
/// <c>IngestaDbContext</c> para escribirlas. Ninguna columna la genera la base: <c>halsa_ingesta</c> no
/// necesita <c>RETURNING</c>.
/// </summary>
internal static class IntegracionConverters
{
    public static readonly ValueConverter<TenantId, string> Tenant = new(v => v.Value, v => new TenantId(v));

    public static readonly ValueConverter<CasoMedicoId, Guid> CasoId = new(v => v.Value, v => new CasoMedicoId(v));

    public static readonly ValueConverter<TipoReporte, string> Tipo =
        new(v => v.Codigo(), v => TipoReporteCodigos.Parse(v));
}

internal sealed class ReporteEventoConfiguration : IEntityTypeConfiguration<ReporteEvento>
{
    public void Configure(EntityTypeBuilder<ReporteEvento> builder)
    {
        builder.ToTable("reportes_evento", Schemas.Atel);

        builder.HasKey(r => r.CasoMedicoId).HasName("pk_reportes_evento");

        builder.Property(r => r.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(IntegracionConverters.CasoId)
            .ValueGeneratedNever();

        builder.Property(r => r.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .HasConversion(IntegracionConverters.Tenant);

        builder.Property(r => r.Tipo)
            .HasColumnName("tipo")
            .HasMaxLength(10)
            .HasConversion(IntegracionConverters.Tipo);

        builder.Property(r => r.NumeroReporteOrigen).HasColumnName("numero_reporte_origen").HasMaxLength(40);
        builder.Property(r => r.Version).HasColumnName("version").HasMaxLength(10);
        builder.Property(r => r.Datos).HasColumnName("datos").HasColumnType("jsonb");
        builder.Property(r => r.RecibidoEn).HasColumnName("recibido_en").HasColumnType("timestamp with time zone");

        builder.HasOne<CasoMedico>()
            .WithOne()
            .HasForeignKey<ReporteEvento>(r => r.CasoMedicoId)
            .HasConstraintName("fk_reportes_evento_casos_medicos")
            .OnDelete(DeleteBehavior.Restrict);

        // La clave de negocio: un reporte del core de la ARL se radica una sola vez.
        builder.HasIndex(r => new { r.TenantId, r.Tipo, r.NumeroReporteOrigen })
            .IsUnique()
            .HasDatabaseName(IntegracionConstraints.ReporteYaRadicado);
    }
}

internal sealed class SolicitudIntegracionConfiguration : IEntityTypeConfiguration<SolicitudIntegracion>
{
    public void Configure(EntityTypeBuilder<SolicitudIntegracion> builder)
    {
        builder.ToTable("solicitudes", Schemas.Integracion);

        builder.HasKey(s => s.Id).HasName("pk_solicitudes");

        builder.Property(s => s.Id).HasColumnName("id").HasColumnType("uuid").ValueGeneratedNever();

        builder.Property(s => s.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .HasConversion(IntegracionConverters.Tenant);

        builder.Property(s => s.Tipo)
            .HasColumnName("tipo")
            .HasMaxLength(10)
            .HasConversion(IntegracionConverters.Tipo);

        builder.Property(s => s.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(IntegracionConverters.CasoId);

        builder.Property(s => s.Radicado).HasColumnName("radicado").HasMaxLength(40);
        builder.Property(s => s.RecibidoEn).HasColumnName("recibido_en").HasColumnType("timestamp with time zone");
        builder.Property(s => s.ClientId).HasColumnName("client_id").HasMaxLength(100);
        builder.Property(s => s.CorrelationId).HasColumnName("correlation_id").HasMaxLength(64);

        builder.HasOne<CasoMedico>()
            .WithMany()
            .HasForeignKey(s => s.CasoMedicoId)
            .HasConstraintName("fk_solicitudes_casos_medicos")
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(s => s.CasoMedicoId).HasDatabaseName("ix_solicitudes_caso_medico_id");
    }
}

internal sealed class SolicitudIdempotenteConfiguration : IEntityTypeConfiguration<SolicitudIdempotente>
{
    public void Configure(EntityTypeBuilder<SolicitudIdempotente> builder)
    {
        builder.ToTable("solicitudes_idempotentes", Schemas.Integracion);

        builder.HasKey(s => new { s.Emisor, s.IdempotencyKey }).HasName(IntegracionConstraints.LlaveIdempotencia);

        builder.Property(s => s.Emisor).HasColumnName("emisor").HasMaxLength(80);
        builder.Property(s => s.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("uuid");
        builder.Property(s => s.TenantId).HasColumnName("tenant_id").HasMaxLength(20);
        builder.Property(s => s.CuerpoSha256).HasColumnName("cuerpo_sha256").HasColumnType("bytea");
        builder.Property(s => s.SolicitudId).HasColumnName("solicitud_id").HasColumnType("uuid");
        builder.Property(s => s.Respuesta).HasColumnName("respuesta").HasColumnType("jsonb");
        builder.Property(s => s.CreadaEn).HasColumnName("creada_en").HasColumnType("timestamp with time zone");
        builder.Property(s => s.ExpiraEn).HasColumnName("expira_en").HasColumnType("timestamp with time zone");

        // La purga de la SPEC 09 borra por vencimiento.
        builder.HasIndex(s => s.ExpiraEn).HasDatabaseName("ix_solicitudes_idempotentes_expira_en");
    }
}

internal sealed class MensajeEntranteConfiguration : IEntityTypeConfiguration<MensajeEntrante>
{
    public void Configure(EntityTypeBuilder<MensajeEntrante> builder)
    {
        builder.ToTable("mensajes_entrantes", Schemas.Integracion);

        builder.HasKey(m => m.SolicitudId).HasName("pk_mensajes_entrantes");

        builder.Property(m => m.SolicitudId).HasColumnName("solicitud_id").HasColumnType("uuid").ValueGeneratedNever();

        builder.Property(m => m.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .HasConversion(IntegracionConverters.Tenant);

        builder.Property(m => m.Tipo)
            .HasColumnName("tipo")
            .HasMaxLength(10)
            .HasConversion(IntegracionConverters.Tipo);

        builder.Property(m => m.RecibidoEn).HasColumnName("recibido_en").HasColumnType("timestamp with time zone");
        builder.Property(m => m.RetenerHasta).HasColumnName("retener_hasta").HasColumnType("date");

        builder.OwnsOne(m => m.Cuerpo, o =>
        {
            o.Property(c => c.Ciphertext).HasColumnName("cuerpo_ct").HasColumnType("bytea").IsRequired();
            o.Property(c => c.Nonce).HasColumnName("cuerpo_nonce").HasColumnType("bytea").IsRequired();
            o.Property(c => c.Tag).HasColumnName("cuerpo_tag").HasColumnType("bytea").IsRequired();
            o.Property(c => c.KeyId).HasColumnName("cuerpo_key_id").HasMaxLength(64).IsRequired();
        });
        builder.Navigation(m => m.Cuerpo).IsRequired();

        builder.HasOne<SolicitudIntegracion>()
            .WithOne()
            .HasForeignKey<MensajeEntrante>(m => m.SolicitudId)
            .HasConstraintName("fk_mensajes_entrantes_solicitudes")
            .OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Nombres de las restricciones únicas que la ingesta distingue al chocar.</summary>
internal static class IntegracionConstraints
{
    public const string LlaveIdempotencia = "pk_solicitudes_idempotentes";

    public const string ReporteYaRadicado = "ux_reportes_evento_tenant_tipo_numero";
}

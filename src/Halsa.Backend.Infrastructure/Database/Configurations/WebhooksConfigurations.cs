using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Webhooks;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

/// <summary>
/// <c>integracion.suscripciones_webhook</c> (SPEC 10), con RLS por emisor. <see cref="ApplicationDbContext"/> la gestiona
/// como <c>halsa_app</c> desde la API de suscripciones; el Worker la lee y la suspende en SQL como <c>halsa_worker</c>.
/// </summary>
internal sealed class SuscripcionWebhookConfiguration : IEntityTypeConfiguration<SuscripcionWebhook>
{
    // On the non-nullable ids, so they also serve the nullable columns: EF handles the nulls.
    private static readonly ValueConverter<TenantId, string> TenantConverter = new(v => v.Value, v => new TenantId(v));

    private static readonly ValueConverter<IpsId, string> IpsConverter = new(v => v.Value, v => new IpsId(v));

    public void Configure(EntityTypeBuilder<SuscripcionWebhook> builder)
    {
        builder.ToTable("suscripciones_webhook", Schemas.Integracion);

        builder.HasKey(s => s.Id).HasName("pk_suscripciones_webhook");

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new SuscripcionWebhookId(v))
            .ValueGeneratedNever();

        builder.Property(s => s.Emisor).HasColumnName("emisor").HasMaxLength(80);

        builder.Property(s => s.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .HasConversion(TenantConverter);

        builder.Property(s => s.IpsId)
            .HasColumnName("ips_id")
            .HasMaxLength(12)
            .HasConversion(IpsConverter);

        builder.Property(s => s.Url).HasColumnName("url").HasMaxLength(SuscripcionWebhook.MaximoUrl);
        builder.Property(s => s.Eventos).HasColumnName("eventos").HasColumnType("text[]");

        builder.Property(s => s.Estado)
            .HasColumnName("estado")
            .HasMaxLength(12)
            .HasConversion(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<EstadoSuscripcion>(v));

        builder.Property(s => s.SecretoCt).HasColumnName("secreto_ct").HasColumnType("bytea");
        builder.Property(s => s.SecretoKeyId).HasColumnName("secreto_key_id").HasMaxLength(64);
        builder.Property(s => s.SecretoAnteriorCt).HasColumnName("secreto_anterior_ct").HasColumnType("bytea");
        builder.Property(s => s.SecretoAnteriorExpiraEn)
            .HasColumnName("secreto_anterior_expira_en")
            .HasColumnType("timestamp with time zone");
        builder.Property(s => s.FallosConsecutivos).HasColumnName("fallos_consecutivos");
        builder.Property(s => s.CreadaEn).HasColumnName("creada_en").HasColumnType("timestamp with time zone");
        builder.Property(s => s.CreadaPor).HasColumnName("creada_por").HasMaxLength(64);

        builder.Ignore(s => s.Destinatario);

        // The fan-out looks up the active subscriptions of one recipient.
        builder.HasIndex(s => s.Emisor).HasDatabaseName("ix_suscripciones_webhook_emisor");
    }
}

/// <summary>
/// <c>integracion.entregas_webhook</c> (SPEC 10). La RLS de <c>halsa_app</c> pasa por la suscripción; el fan-out y el
/// dispatcher la escriben en SQL como <c>halsa_worker</c>.
/// </summary>
internal sealed class EntregaWebhookConfiguration : IEntityTypeConfiguration<EntregaWebhook>
{
    /// <summary>Una entrega por suscripción y evento: el fan-out repetido no duplica.</summary>
    public const string Unica = "ux_entregas_webhook_suscripcion_evento";

    public void Configure(EntityTypeBuilder<EntregaWebhook> builder)
    {
        builder.ToTable("entregas_webhook", Schemas.Integracion);

        builder.HasKey(e => e.Id).HasName("pk_entregas_webhook");

        builder.Property(e => e.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new EntregaWebhookId(v))
            .ValueGeneratedNever();

        builder.Property(e => e.SuscripcionId)
            .HasColumnName("suscripcion_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new SuscripcionWebhookId(v));

        builder.HasOne<SuscripcionWebhook>()
            .WithMany()
            .HasForeignKey(e => e.SuscripcionId)
            .HasConstraintName("fk_entregas_webhook_suscripciones_webhook")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(e => e.EventoId).HasColumnName("evento_id");
        builder.Property(e => e.Tipo).HasColumnName("tipo").HasMaxLength(40);
        builder.Property(e => e.Payload).HasColumnName("payload").HasColumnType("jsonb");

        builder.Property(e => e.Estado)
            .HasColumnName("estado")
            .HasMaxLength(12)
            .HasConversion(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<EstadoEntrega>(v));

        builder.Property(e => e.Intentos).HasColumnName("intentos");
        builder.Property(e => e.PrimerIntentoEn).HasColumnName("primer_intento_en").HasColumnType("timestamp with time zone");
        builder.Property(e => e.ProximoIntentoEn).HasColumnName("proximo_intento_en").HasColumnType("timestamp with time zone");
        builder.Property(e => e.UltimoStatus).HasColumnName("ultimo_status");
        builder.Property(e => e.UltimoError).HasColumnName("ultimo_error").HasMaxLength(EntregaWebhook.MaximoError);
        builder.Property(e => e.EntregadaEn).HasColumnName("entregada_en").HasColumnType("timestamp with time zone");

        builder.HasIndex(e => new { e.SuscripcionId, e.EventoId })
            .HasDatabaseName(Unica)
            .IsUnique();

        // The dispatcher only ever scans pending deliveries.
        builder.HasIndex(e => e.ProximoIntentoEn)
            .HasDatabaseName("ix_entregas_webhook_pendientes")
            .HasFilter("estado = 'PENDIENTE'");
    }
}

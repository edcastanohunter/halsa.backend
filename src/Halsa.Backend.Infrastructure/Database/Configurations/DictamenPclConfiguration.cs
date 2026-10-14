using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

/// <summary>
/// <c>atel.dictamenes_pcl</c> (SPEC 08). <see cref="ApplicationDbContext"/> la crea y la lee como <c>halsa_app</c>;
/// <c>IngestaDbContext</c> aplica esta configuración para insertarla y marcar <c>corregido_por</c>. Nada lo genera la
/// base. <c>corrige_a</c> y <c>corregido_por</c> no son navegaciones: sus FKs están en la migración (la de
/// <c>corregido_por</c>, diferida).
/// </summary>
internal sealed class DictamenPclConfiguration : IEntityTypeConfiguration<DictamenPcl>
{
    /// <summary>Un solo dictamen vigente por caso.</summary>
    public const string VigenteUnico = "ux_dictamenes_pcl_caso_vigente";

    // Defined on the non-nullable id so it also serves CorrigeA and CorregidoPor: EF handles the nulls.
    private static readonly ValueConverter<DictamenPclId, Guid> IdConverter = new(v => v.Value, v => new DictamenPclId(v));

    private static readonly ValueConverter<MedioNotificacion, string> MedioConverter =
        new(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<MedioNotificacion>(v));

    public void Configure(EntityTypeBuilder<DictamenPcl> builder)
    {
        builder.ToTable("dictamenes_pcl", Schemas.Atel);

        builder.HasKey(d => d.Id).HasName("pk_dictamenes_pcl");

        builder.Property(d => d.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(IdConverter)
            .ValueGeneratedNever();

        builder.Property(d => d.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(d => d.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.HasOne<CasoMedico>()
            .WithMany()
            .HasForeignKey(d => d.CasoMedicoId)
            .HasConstraintName("fk_dictamenes_pcl_casos_medicos")
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(d => d.RegistroClinicoId).HasColumnName("registro_clinico_id");

        builder.Property(d => d.PorcentajePcl).HasColumnName("porcentaje_pcl").HasColumnType("numeric(5,2)");

        builder.Property(d => d.Origen)
            .HasColumnName("origen")
            .HasMaxLength(10)
            .HasConversion(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<OrigenPcl>(v));

        builder.Property(d => d.FechaEstructuracion).HasColumnName("fecha_estructuracion");
        builder.Property(d => d.FechaDictamen).HasColumnName("fecha_dictamen");

        builder.Property(d => d.CalificadorSub).HasColumnName("calificador_sub").HasMaxLength(64);
        builder.Property(d => d.CalificadorNombre).HasColumnName("calificador_nombre").HasMaxLength(200);
        builder.Property(d => d.CalificadorRegistroRethus).HasColumnName("calificador_registro_rethus").HasMaxLength(30);

        builder.Property(d => d.EstadoNotificacion)
            .HasColumnName("estado_notificacion")
            .HasMaxLength(20)
            .HasConversion(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<EstadoNotificacionDictamen>(v));

        builder.Property(d => d.NotificadoEn)
            .HasColumnName("notificado_en")
            .HasColumnType("timestamp with time zone");

        builder.Property(d => d.MedioNotificacion)
            .HasColumnName("medio_notificacion")
            .HasMaxLength(20)
            .HasConversion(MedioConverter);

        builder.Property(d => d.CorrigeA).HasColumnName("corrige_a").HasConversion(IdConverter);
        builder.Property(d => d.CorregidoPor).HasColumnName("corregido_por").HasConversion(IdConverter);

        builder.Ignore(d => d.Vigente);

        builder.HasIndex(d => d.CasoMedicoId)
            .HasDatabaseName(VigenteUnico)
            .IsUnique()
            .HasFilter("corregido_por IS NULL");
    }
}

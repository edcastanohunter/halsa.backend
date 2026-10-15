using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Pacientes;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Halsa.Backend.Infrastructure.Database.Configurations;

// SPEC 12: the activation codes and the links of the patient portal. intentos_canje and the redemption function are
// SQL only (migration PacienteVinculos): the API never reads them, only atel.canjear_codigo_activacion does.

internal sealed class CodigoActivacionConfiguration : IEntityTypeConfiguration<CodigoActivacion>
{
    public void Configure(EntityTypeBuilder<CodigoActivacion> builder)
    {
        builder.ToTable("codigos_activacion", Schemas.Atel);

        builder.HasKey(c => c.Id).HasName("pk_codigos_activacion");

        builder.Property(c => c.Id).HasColumnName("id").HasColumnType("char(6)");

        builder.Property(c => c.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .IsRequired()
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(c => c.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(c => c.TrabajadorTipo).HasColumnName("trabajador_tipo").HasMaxLength(10).IsRequired();
        builder.Property(c => c.TrabajadorNumero).HasColumnName("trabajador_numero").HasMaxLength(30).IsRequired();
        builder.Property(c => c.SecretoHash).HasColumnName("secreto_hash").HasColumnType("bytea").IsRequired();
        builder.Property(c => c.SecretoSal).HasColumnName("secreto_sal").HasColumnType("bytea").IsRequired();
        builder.Property(c => c.EmitidoEn).HasColumnName("emitido_en");
        builder.Property(c => c.EmitidoPor).HasColumnName("emitido_por").HasMaxLength(64).IsRequired();
        builder.Property(c => c.ExpiraEn).HasColumnName("expira_en");
        builder.Property(c => c.UsadoEn).HasColumnName("usado_en");
        builder.Property(c => c.UsadoPor).HasColumnName("usado_por").HasMaxLength(64);
        builder.Property(c => c.IntentosFallidos).HasColumnName("intentos_fallidos");

        builder.Property(c => c.Estado)
            .HasColumnName("estado")
            .HasMaxLength(10)
            .HasConversion(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<EstadoCodigoActivacion>(v));

        builder.HasOne<CasoMedico>()
            .WithMany()
            .HasForeignKey(c => c.CasoMedicoId)
            .HasConstraintName("fk_codigos_activacion_casos_medicos")
            .OnDelete(DeleteBehavior.Restrict);

        // At most one ACTIVO per case: issuing a new one expires the previous one in the same transaction.
        builder.HasIndex(c => c.CasoMedicoId)
            .IsUnique()
            .HasFilter("estado = 'ACTIVO'")
            .HasDatabaseName("ux_codigos_activacion_caso_activo");
    }
}

internal sealed class VinculoPacienteConfiguration : IEntityTypeConfiguration<VinculoPaciente>
{
    public void Configure(EntityTypeBuilder<VinculoPaciente> builder)
    {
        builder.ToTable("vinculos_paciente", Schemas.Atel);

        builder.HasKey(v => new { v.PacienteSub, v.TrabajadorTipo, v.TrabajadorNumero }).HasName("pk_vinculos_paciente");

        builder.Property(v => v.PacienteSub).HasColumnName("paciente_sub").HasMaxLength(64);
        builder.Property(v => v.TrabajadorTipo).HasColumnName("trabajador_tipo").HasMaxLength(10);
        builder.Property(v => v.TrabajadorNumero).HasColumnName("trabajador_numero").HasMaxLength(30);
        builder.Property(v => v.CodigoId).HasColumnName("codigo_id").HasColumnType("char(6)").IsRequired();
        builder.Property(v => v.VinculadoEn).HasColumnName("vinculado_en");

        builder.HasOne<CodigoActivacion>()
            .WithMany()
            .HasForeignKey(v => v.CodigoId)
            .HasConstraintName("fk_vinculos_paciente_codigos_activacion")
            .OnDelete(DeleteBehavior.Restrict);

        // The patient RLS of casos_medicos looks the links up by document.
        builder.HasIndex(v => new { v.TrabajadorTipo, v.TrabajadorNumero })
            .HasDatabaseName("ix_vinculos_paciente_trabajador");
    }
}

internal sealed class SolicitudCopiaHcConfiguration : IEntityTypeConfiguration<SolicitudCopiaHc>
{
    public void Configure(EntityTypeBuilder<SolicitudCopiaHc> builder)
    {
        builder.ToTable("solicitudes_copia_hc", Schemas.Atel);

        builder.HasKey(s => s.Id).HasName("pk_solicitudes_copia_hc");

        builder.Property(s => s.Id)
            .HasColumnName("id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new SolicitudCopiaHcId(v));

        builder.Property(s => s.TenantId)
            .HasColumnName("tenant_id")
            .HasMaxLength(20)
            .IsRequired()
            .HasConversion(v => v.Value, v => new TenantId(v));

        builder.Property(s => s.CasoMedicoId)
            .HasColumnName("caso_medico_id")
            .HasColumnType("uuid")
            .HasConversion(v => v.Value, v => new CasoMedicoId(v));

        builder.Property(s => s.PacienteSub).HasColumnName("paciente_sub").HasMaxLength(64).IsRequired();

        builder.Property(s => s.MedioEntrega)
            .HasColumnName("medio_entrega")
            .HasMaxLength(20)
            .HasConversion(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<MedioEntregaCopiaHc>(v));

        builder.Property(s => s.DireccionOCorreo)
            .HasColumnName("direccion_o_correo")
            .HasMaxLength(SolicitudCopiaHc.MaximoDireccion);

        builder.Property(s => s.Observaciones)
            .HasColumnName("observaciones")
            .HasMaxLength(SolicitudCopiaHc.MaximoObservaciones);

        builder.Property(s => s.SolicitadaEn).HasColumnName("solicitada_en");

        builder.Property(s => s.Estado)
            .HasColumnName("estado")
            .HasMaxLength(12)
            .HasConversion(v => CodigosDictamen.Codigo(v), v => CodigosDictamen.Parse<EstadoSolicitudCopiaHc>(v));

        builder.Property(s => s.AtendidaEn).HasColumnName("atendida_en");
        builder.Property(s => s.AtendidaPor).HasColumnName("atendida_por").HasMaxLength(64);
        builder.Property(s => s.NotaAtencion).HasColumnName("nota_atencion").HasMaxLength(SolicitudCopiaHc.MaximoNota);

        builder.HasOne<CasoMedico>()
            .WithMany()
            .HasForeignKey(s => s.CasoMedicoId)
            .HasConstraintName("fk_solicitudes_copia_hc_casos_medicos")
            .OnDelete(DeleteBehavior.Restrict);

        // At most one open request per case. The patient cannot see another account's request on the same case, so the
        // index is what answers its 409 then.
        builder.HasIndex(s => s.CasoMedicoId)
            .IsUnique()
            .HasFilter("estado IN ('RECIBIDA', 'EN_TRAMITE')")
            .HasDatabaseName("ux_solicitudes_copia_hc_caso_abierta");

        // The ARL's inbox.
        builder.HasIndex(s => new { s.TenantId, s.Estado, s.SolicitadaEn })
            .HasDatabaseName("ix_solicitudes_copia_hc_tenant_estado_solicitada_en");

        builder.HasIndex(s => new { s.PacienteSub, s.SolicitadaEn })
            .HasDatabaseName("ix_solicitudes_copia_hc_paciente_solicitada_en");
    }
}

using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Clinico;

/// <summary>Para qué se subió un soporte. Se guarda como <c>INCAPACIDAD</c>.</summary>
public enum TipoSoporte
{
    Incapacidad
}

/// <summary>
/// La metadata de un archivo soporte (SPEC 07), en <c>clinico.soportes</c>: el PDF contiene el diagnóstico, así que es
/// un documento clínico. El archivo vive cifrado fuera de la base, en la ruta que devolvió el almacenamiento; aquí
/// solo queda dónde está, con qué DEK se cifró y la huella del original. Inmutable, como la historia clínica.
/// </summary>
public sealed class Soporte
{
    // Parameterless constructor required by EF Core materializer.
    private Soporte() { }

    public Guid Id { get; private set; }

    public TenantId TenantId { get; private set; }

    public CasoMedicoId CasoMedicoId { get; private set; }

    public TipoSoporte Tipo { get; private set; }

    /// <summary>Relativa a la raíz del almacenamiento de archivos: <c>{tenant}/{yyyy}/{MM}/{id}.bin</c>.</summary>
    public string Ruta { get; private set; } = string.Empty;

    public string KeyId { get; private set; } = string.Empty;

    /// <summary>SHA-256 del archivo original, antes de cifrar.</summary>
    public byte[] Sha256Claro { get; private set; } = [];

    /// <summary>Bytes del archivo original.</summary>
    public long Tamano { get; private set; }

    public DateTimeOffset SubidoEn { get; private set; }

    /// <summary><see cref="SubidoEn"/> más <see cref="HistoriaClinicaDetalle.AniosRetencion"/> años, como la HC.</summary>
    public DateOnly RetenerHasta { get; private set; }

    public static Soporte Registrar(
        Guid id,
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        TipoSoporte tipo,
        string ruta,
        string keyId,
        byte[] sha256Claro,
        long tamano,
        DateTimeOffset subidoEn)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ruta);
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        ArgumentNullException.ThrowIfNull(sha256Claro);

        return new Soporte
        {
            Id = id,
            TenantId = tenantId,
            CasoMedicoId = casoMedicoId,
            Tipo = tipo,
            Ruta = ruta,
            KeyId = keyId,
            Sha256Claro = sha256Claro,
            Tamano = tamano,
            SubidoEn = subidoEn,
            RetenerHasta = DateOnly.FromDateTime(subidoEn.UtcDateTime).AddYears(HistoriaClinicaDetalle.AniosRetencion)
        };
    }
}

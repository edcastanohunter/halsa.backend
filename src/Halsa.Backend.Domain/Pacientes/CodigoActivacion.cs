using Halsa.Backend.Domain.Casos;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Domain.Pacientes;

/// <summary>Se guarda como <c>ACTIVO</c>, <c>USADO</c>, <c>BLOQUEADO</c> o <c>EXPIRADO</c>.</summary>
public enum EstadoCodigoActivacion
{
    Activo,
    Usado,
    Bloqueado,
    Expirado
}

/// <summary>
/// Un código de activación del portal del paciente (SPEC 12), en <c>atel.codigos_activacion</c>. Lo emite la ARL del caso,
/// que ya conoce al trabajador, y lo canjea el paciente para vincular su cuenta al documento del trabajador. Es de un
/// solo uso y vale <see cref="VigenciaDias"/> días.
///
/// Del secreto solo se guardan <see cref="SecretoSal"/> y <see cref="SecretoHash"/> (<c>SHA-256(sal ‖ secreto)</c>): el
/// código completo se muestra una sola vez, al emitirlo. El canje no pasa por esta clase: lo hace la función
/// <c>atel.canjear_codigo_activacion</c>, porque el paciente no tiene tenant y la RLS no le muestra ningún código.
/// </summary>
public sealed class CodigoActivacion
{
    public const int VigenciaDias = 30;

    /// <summary>Los secretos equivocados que lo dejan <see cref="EstadoCodigoActivacion.Bloqueado"/>.</summary>
    public const int MaxIntentosFallidos = 5;

    // Parameterless constructor required by EF Core materializer.
    private CodigoActivacion() { }

    /// <summary>Los <see cref="FormatoCodigoActivacion.LargoId"/> caracteres visibles antes del guion.</summary>
    public string Id { get; private set; } = string.Empty;

    public TenantId TenantId { get; private set; }

    public CasoMedicoId CasoMedicoId { get; private set; }

    public string TrabajadorTipo { get; private set; } = string.Empty;

    public string TrabajadorNumero { get; private set; } = string.Empty;

    public byte[] SecretoHash { get; private set; } = [];

    public byte[] SecretoSal { get; private set; } = [];

    public DateTimeOffset EmitidoEn { get; private set; }

    /// <summary>El <c>sub</c> de quien lo emitió (analista o integración de la ARL).</summary>
    public string EmitidoPor { get; private set; } = string.Empty;

    public DateTimeOffset ExpiraEn { get; private set; }

    public DateTimeOffset? UsadoEn { get; private set; }

    /// <summary>El <c>sub</c> del paciente que lo canjeó.</summary>
    public string? UsadoPor { get; private set; }

    public int IntentosFallidos { get; private set; }

    public EstadoCodigoActivacion Estado { get; private set; }

    /// <summary>
    /// Emite un código <see cref="EstadoCodigoActivacion.Activo"/> para el trabajador del caso: un id y un secreto
    /// aleatorios, de los que solo se guardan la sal y el hash. <c>CodigoCompleto</c> es la única vez que el secreto
    /// existe en claro.
    /// </summary>
    public static (CodigoActivacion Codigo, string CodigoCompleto) Emitir(
        TenantId tenantId,
        CasoMedicoId casoMedicoId,
        Identificacion trabajador,
        string emitidoPor,
        DateTimeOffset ahora)
    {
        ArgumentNullException.ThrowIfNull(trabajador);
        ArgumentException.ThrowIfNullOrWhiteSpace(emitidoPor);

        string id = FormatoCodigoActivacion.Aleatorio(FormatoCodigoActivacion.LargoId);
        string secreto = FormatoCodigoActivacion.Aleatorio(FormatoCodigoActivacion.LargoSecreto);
        byte[] sal = FormatoCodigoActivacion.NuevaSal();

        var codigo = new CodigoActivacion
        {
            Id = id,
            TenantId = tenantId,
            CasoMedicoId = casoMedicoId,
            TrabajadorTipo = trabajador.Tipo,
            TrabajadorNumero = trabajador.Numero,
            SecretoSal = sal,
            SecretoHash = FormatoCodigoActivacion.Hash(sal, secreto),
            EmitidoEn = ahora.ToUniversalTime(),
            EmitidoPor = emitidoPor,
            ExpiraEn = ahora.AddDays(VigenciaDias).ToUniversalTime(),
            Estado = EstadoCodigoActivacion.Activo
        };

        return (codigo, FormatoCodigoActivacion.Componer(id, secreto));
    }

    /// <summary>Si un caso en <paramref name="estado"/> admite códigos: <c>Validado</c> o posterior, nunca rechazado.</summary>
    public static bool AdmiteCodigos(EstadoCaso estado) =>
        estado is EstadoCaso.Validado or EstadoCaso.EnAtencion or EstadoCaso.EnCalificacionOrigen
            or EstadoCaso.EnCalificacionPcl or EstadoCaso.Cerrado;

    /// <summary>
    /// Un código nuevo para el mismo caso reemplaza a este: si sigue <see cref="EstadoCodigoActivacion.Activo"/>, pasa a
    /// <see cref="EstadoCodigoActivacion.Expirado"/> y su canje responde como el de uno vencido. Devuelve si cambió.
    /// </summary>
    public bool Invalidar()
    {
        if (Estado != EstadoCodigoActivacion.Activo)
        {
            return false;
        }

        Estado = EstadoCodigoActivacion.Expirado;
        return true;
    }
}

/// <summary>
/// El vínculo de una cuenta de <c>halsa-pacientes</c> con el documento de un trabajador (SPEC 12), en
/// <c>atel.vinculos_paciente</c>. Es por documento, no por caso: vale para los casos de cualquier ARL. Lo crea solo la
/// función de canje; la API lo lee con la RLS del paciente (<c>paciente_sub = app.sub</c>).
/// </summary>
public sealed class VinculoPaciente
{
    // Parameterless constructor required by EF Core materializer.
    private VinculoPaciente() { }

    public string PacienteSub { get; private set; } = string.Empty;

    public string TrabajadorTipo { get; private set; } = string.Empty;

    public string TrabajadorNumero { get; private set; } = string.Empty;

    /// <summary>El código con que se vinculó.</summary>
    public string CodigoId { get; private set; } = string.Empty;

    public DateTimeOffset VinculadoEn { get; private set; }
}

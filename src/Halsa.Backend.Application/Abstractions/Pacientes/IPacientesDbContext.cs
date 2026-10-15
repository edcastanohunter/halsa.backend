using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Pacientes;
using Halsa.Backend.SharedKernel.Tenancy;

namespace Halsa.Backend.Application.Abstractions.Pacientes;

/// <summary>
/// Las escrituras del portal del paciente (SPEC 12) sobre el contexto de <c>halsa_app</c>. Cada operación es atómica: el
/// cambio y lo que <c>registrar</c> agregue (la fila de auditoría) se confirman en la misma transacción.
///
/// Solo la usa <c>Halsa.Backend.Application.Pacientes</c>.
/// </summary>
public interface IPacientesDbContext
{
    /// <summary>
    /// Emite un código para el caso: bloquea el caso, vence los códigos <c>ACTIVO</c> que tenga e inserta el nuevo
    /// (con otro id si el sorteado ya existe en otro tenant). La RLS limita todo al tenant de la sesión.
    /// </summary>
    Task<CodigoEmitido> EmitirCodigoAsync(
        TenantId tenantId,
        CasoMedicoId casoId,
        Identificacion trabajador,
        string emitidoPor,
        DateTimeOffset ahora,
        Action<CodigoActivacion> registrar,
        CancellationToken ct);

    /// <summary>
    /// Canjea con <c>atel.canjear_codigo_activacion</c>, que corre como su dueño: la sesión del paciente no ve ningún
    /// código. Los intentos fallidos quedan contados aunque el resultado no sea un vínculo.
    /// </summary>
    Task<ResultadoCanje> CanjearCodigoAsync(
        string id,
        string secreto,
        string pacienteSub,
        Action<ResultadoCanje> registrar,
        CancellationToken ct);

    /// <summary>Agrega una solicitud de copia de la HC: la confirma <see cref="GuardarSolicitudAsync"/>.</summary>
    void Agregar(SolicitudCopiaHc solicitud);

    /// <summary>La solicitud, rastreada, si la sesión la ve (la ARL del caso). <c>null</c> si no.</summary>
    Task<SolicitudCopiaHc?> SolicitudAsync(SolicitudCopiaHcId id, CancellationToken ct);

    /// <summary>
    /// Guarda lo agregado. <c>false</c> si el caso ya tiene una solicitud abierta (el índice único la detectó, aunque la
    /// sesión no la vea): nada se confirma.
    /// </summary>
    Task<bool> GuardarSolicitudAsync(CancellationToken ct);
}

/// <param name="Codigo">El código completo, <c>ABC123-K7Q2M9XD4P</c>. Solo existe en esta respuesta.</param>
public sealed record CodigoEmitido(string Id, string Codigo, DateTimeOffset ExpiraEn);

/// <summary>Lo que devuelve la función de canje.</summary>
/// <param name="TenantId">El tenant del código, si existe. Para la auditoría.</param>
/// <param name="Trabajador">El documento del código, solo con <see cref="ResultadoCanjeTipo.Vinculado"/> o <see cref="ResultadoCanjeTipo.VinculoDistinto"/>.</param>
public sealed record ResultadoCanje(
    ResultadoCanjeTipo Resultado,
    TenantId? TenantId,
    CasoMedicoId? CasoMedicoId,
    Identificacion? Trabajador);

/// <summary>Los resultados de <c>atel.canjear_codigo_activacion</c>.</summary>
public enum ResultadoCanjeTipo
{
    /// <summary><c>VINCULADO</c>: el código quedó <c>USADO</c> y la cuenta, vinculada al documento.</summary>
    Vinculado,

    /// <summary><c>INVALIDO</c>: inexistente, vencido, usado o con el secreto equivocado.</summary>
    Invalido,

    /// <summary><c>BLOQUEADO</c>: el código acumuló 5 secretos equivocados.</summary>
    Bloqueado,

    /// <summary><c>DEMASIADOS_INTENTOS</c>: el paciente acumuló 5 canjes fallidos en 24 h.</summary>
    DemasiadosIntentos,

    /// <summary><c>VINCULO_DISTINTO</c>: la cuenta ya está vinculada a otro documento.</summary>
    VinculoDistinto
}

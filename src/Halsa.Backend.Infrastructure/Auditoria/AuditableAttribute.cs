using Halsa.Backend.Application.Abstractions.Auditoria;

namespace Halsa.Backend.Infrastructure.Auditoria;

/// <summary>
/// Metadata de una ruta cuyos rechazos de autorización se auditan (<see cref="AuditingAuthorizationResultHandler"/>).
/// Sin <paramref name="accion"/>, la acción sale del verbo HTTP.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class AuditableAttribute(RecursoAuditoria recurso, AccionAuditoria? accion = null) : Attribute
{
    /// <summary>Nombre del parámetro de ruta con el id del caso, para <c>caso_medico_id</c>.</summary>
    public const string CasoIdRouteValue = "casoId";

    public RecursoAuditoria Recurso { get; } = recurso;

    public AccionAuditoria? Accion { get; } = accion;

    public AccionAuditoria AccionPara(string metodoHttp) => Accion ?? metodoHttp.ToUpperInvariant() switch
    {
        "POST" => AccionAuditoria.Crear,
        "PUT" or "PATCH" => AccionAuditoria.Actualizar,
        "DELETE" => AccionAuditoria.Eliminar,
        _ => AccionAuditoria.Leer
    };
}

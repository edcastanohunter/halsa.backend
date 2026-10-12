using Halsa.Backend.Application.Abstractions.Auditoria;

namespace Halsa.Backend.Application.Abstractions.Clinico;

/// <summary>
/// Marca una consulta que lee detalle clínico. Hereda de <see cref="IAuditedQuery"/>: toda lectura
/// clínica se audita, y una consulta clínica no puede olvidarlo (SPEC 04).
/// </summary>
public interface IClinicalQuery : IAuditedQuery;

using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Abstractions.Clinico;

/// <summary>
/// El profesional que firma un registro clínico del backoffice (SPEC 06). Su identidad sale del token
/// (<c>sub</c> y los claims <c>rethus</c>, <c>tipo_documento</c> y <c>numero_documento</c>), nunca del
/// payload: si viajara en el cuerpo, cualquiera con <c>clinico:write</c> podría firmar como otro médico.
/// </summary>
public interface IProfesionalActual
{
    /// <summary>
    /// El <see cref="ProfesionalRef"/> del usuario autenticado, o <see cref="ClinicoErrors.ProfesionalSinRegistro"/>
    /// si falta alguno de esos claims.
    /// </summary>
    Result<ProfesionalRef> Obtener();
}

using System.Security.Claims;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.AspNetCore.Http;

namespace Halsa.Backend.Infrastructure.Authentication;

/// <summary>
/// Lee la identidad del profesional de los claims planos que añaden los mappers <i>User Attribute</i> del
/// realm <c>halsa</c>. Como <see cref="CurrentUser"/>, los nombres son los del JWT tal cual
/// (<c>MapInboundClaims = false</c>).
/// </summary>
internal sealed class ProfesionalActual(IHttpContextAccessor httpContextAccessor) : IProfesionalActual
{
    public const string RethusClaimType = "rethus";

    public const string TipoDocumentoClaimType = "tipo_documento";

    public const string NumeroDocumentoClaimType = "numero_documento";

    public Result<ProfesionalRef> Obtener()
    {
        ClaimsPrincipal? principal = httpContextAccessor.HttpContext?.User;

        string? sub = Valor(principal, CurrentUser.SubjectClaimType);
        string? rethus = Valor(principal, RethusClaimType);
        string? tipoDocumento = Valor(principal, TipoDocumentoClaimType);
        string? numeroDocumento = Valor(principal, NumeroDocumentoClaimType);

        if (sub is null || rethus is null || tipoDocumento is null || numeroDocumento is null)
        {
            return Result.Failure<ProfesionalRef>(ClinicoErrors.ProfesionalSinRegistro);
        }

        return Result.Success(new ProfesionalRef(sub, tipoDocumento, numeroDocumento, rethus));
    }

    private static string? Valor(ClaimsPrincipal? principal, string claimType)
    {
        string? valor = principal?.FindFirstValue(claimType)?.Trim();
        return string.IsNullOrEmpty(valor) ? null : valor;
    }
}

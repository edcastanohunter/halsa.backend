using Halsa.Backend.Api.Endpoints;
using Halsa.Backend.Api.Extensions;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Infrastructure.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Halsa.Backend.FunctionalTests.Abstractions;

/// <summary>
/// An endpoint that exists only in the test assembly (SPEC 07, step 1): it exercises the step-up policy without the
/// incapacidad endpoint. <see cref="ApiFactory"/> registers it.
/// </summary>
internal sealed class StepUpProbeEndpoint : IEndpoint
{
    public static string Ruta(Guid casoId) => $"v1/pruebas/casos/{casoId}/step-up";

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("pruebas/casos/{casoId:guid}/step-up", () => Results.Ok())
            .RequireAuthorization(Policies.IncapacidadEscritura)
            .Auditable(RecursoAuditoria.Incapacidad);
}

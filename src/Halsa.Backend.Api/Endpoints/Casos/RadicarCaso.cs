using Halsa.Backend.Application.Casos.Commands.Radicar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Mediation;

namespace Halsa.Backend.Api.Endpoints.Casos;

/// <summary>
/// <c>POST /v1/casos</c> — Radica un nuevo caso de salud laboral.
///
/// Solo actores ARL (tienen TenantId) pueden radicar casos.
/// Devuelve 201 Created con el ID del nuevo caso en el header Location.
/// </summary>
internal sealed class RadicarCaso : IEndpoint
{
    internal sealed record Request(
        string Radicado,
        TipoEvento TipoEvento,
        string TrabajadorTipo,
        string TrabajadorNumero,
        string EmpleadorTipo,
        string EmpleadorNumero,
        DateTimeOffset FechaEvento,
        string MunicipioDivipola,
        bool EsGraveOMortal);

    public void MapEndpoint(IEndpointRouteBuilder app) =>
        app.MapPost("casos", async (
                Request request,
                ISender sender,
                CancellationToken ct) =>
            {
                var command = new RadicarCasoCommand(
                    request.Radicado,
                    request.TipoEvento,
                    request.TrabajadorTipo,
                    request.TrabajadorNumero,
                    request.EmpleadorTipo,
                    request.EmpleadorNumero,
                    request.FechaEvento,
                    request.MunicipioDivipola,
                    request.EsGraveOMortal);

                var result = await sender.Send(command, ct);

                return result.Match(
                    id => Results.Created($"/v1/casos/{id}", new { id }),
                    error => error.Type switch
                    {
                        ErrorType.Problem => Results.UnprocessableEntity(new { error.Code, error.Description }),
                        ErrorType.Forbidden => Results.Forbid(),
                        _ => Results.Problem(error.Description)
                    });
            })
        .WithTags(Tags.Casos)
        .WithName("RadicarCaso")
        .WithSummary("Radica un nuevo caso de salud laboral.")
        .Produces<object>(StatusCodes.Status201Created)
        .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
        .ProducesProblem(StatusCodes.Status401Unauthorized)
        .ProducesProblem(StatusCodes.Status403Forbidden)
        .RequirePermission(Permissions.CasosWrite);
}

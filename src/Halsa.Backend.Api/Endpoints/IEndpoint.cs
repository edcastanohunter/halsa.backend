namespace Halsa.Backend.Api.Endpoints;

/// <summary>
/// One endpoint per class: the vertical slice keeps its route, request shape and mapping together,
/// and <c>Program.cs</c> never becomes a thousand-line registration list.
/// </summary>
public interface IEndpoint
{
    void MapEndpoint(IEndpointRouteBuilder app);
}

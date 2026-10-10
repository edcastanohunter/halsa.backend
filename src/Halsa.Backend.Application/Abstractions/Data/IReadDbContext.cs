using Halsa.Backend.Application.WorkItems.ReadModels;

namespace Halsa.Backend.Application.Abstractions.Data;

/// <summary>
/// The read side. Exposes <see cref="IQueryable{T}"/> only: queries compose, but nothing on the
/// read path can mutate. Projections write through the concrete context in the infrastructure layer.
/// </summary>
public interface IReadDbContext
{
    IQueryable<WorkItemView> WorkItems { get; }
}

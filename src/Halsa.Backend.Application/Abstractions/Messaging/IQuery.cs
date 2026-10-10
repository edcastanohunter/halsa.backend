using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Abstractions.Messaging;

/// <summary>Marker shared by every query, so behaviours can target the read side only.</summary>
public interface IBaseQuery;

/// <summary>A question about state. Queries never mutate and never raise domain events.</summary>
public interface IQuery<TResponse> : IRequest<Result<TResponse>>, IBaseQuery;

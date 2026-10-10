using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Abstractions.Messaging;

/// <summary>Marker shared by both command shapes, so pipeline behaviours can target commands only.</summary>
public interface IBaseCommand;

/// <summary>An intent to change state that returns no payload.</summary>
public interface ICommand : IRequest<Result>, IBaseCommand;

/// <summary>An intent to change state that returns a payload (typically the new identifier).</summary>
public interface ICommand<TResponse> : IRequest<Result<TResponse>>, IBaseCommand;

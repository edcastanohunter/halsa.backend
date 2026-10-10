using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;

namespace Halsa.Backend.Application.Abstractions.Messaging;

public interface IQueryHandler<in TQuery, TResponse> : IRequestHandler<TQuery, Result<TResponse>>
    where TQuery : IQuery<TResponse>;

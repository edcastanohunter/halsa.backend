using System.Diagnostics;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.Behaviors;

/// <summary>
/// Structured logging around every request, including the error code when the result is a failure.
/// Outermost behaviour, so it also records the time spent in validation.
/// </summary>
/// <remarks>
/// Plain <c>ILogger</c> calls rather than the <c>[LoggerMessage]</c> source generator: the generator
/// does not support generic containing types, and every behaviour here is generic.
/// </remarks>
public sealed class LoggingBehavior<TRequest, TResponse>(
    ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
    where TResponse : Result
{
    private static readonly string RequestName = typeof(TRequest).Name;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("Processing {RequestName}", RequestName);

        long startingTimestamp = Stopwatch.GetTimestamp();

        TResponse response = await next(cancellationToken).ConfigureAwait(false);

        double elapsedMs = Stopwatch.GetElapsedTime(startingTimestamp).TotalMilliseconds;

        if (response.IsSuccess)
        {
            logger.LogInformation(
                "Completed {RequestName} in {ElapsedMilliseconds}ms",
                RequestName,
                elapsedMs);
        }
        else
        {
            logger.LogWarning(
                "Completed {RequestName} with error {ErrorCode}: {ErrorDescription} in {ElapsedMilliseconds}ms",
                RequestName,
                response.Error.Code,
                response.Error.Description,
                elapsedMs);
        }

        return response;
    }
}

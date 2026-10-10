using Halsa.Backend.Mediation;

namespace Halsa.Backend.UnitTests.Mediation;

public sealed record Ping(string Message) : IRequest<string>;

public sealed class PingHandler : IRequestHandler<Ping, string>
{
    public Task<string> Handle(Ping request, CancellationToken cancellationToken) =>
        Task.FromResult($"pong:{request.Message}");
}

public sealed record Unhandled : IRequest<string>;

public sealed record Announced(string Message) : INotification;

public sealed class FirstAnnouncedHandler(TrackedCalls calls) : INotificationHandler<Announced>
{
    public Task Handle(Announced notification, CancellationToken cancellationToken)
    {
        calls.Add($"first:{notification.Message}");
        return Task.CompletedTask;
    }
}

public sealed class SecondAnnouncedHandler(TrackedCalls calls) : INotificationHandler<Announced>
{
    public Task Handle(Announced notification, CancellationToken cancellationToken)
    {
        calls.Add($"second:{notification.Message}");
        return Task.CompletedTask;
    }
}

/// <summary>Shared, ordered call log used to assert pipeline and fan-out ordering.</summary>
public sealed class TrackedCalls
{
    private readonly List<string> _calls = [];

    public IReadOnlyList<string> Calls => _calls;

    public void Add(string call) => _calls.Add(call);
}

public sealed class OuterBehavior<TRequest, TResponse>(TrackedCalls calls)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        calls.Add("outer:before");
        TResponse response = await next(cancellationToken);
        calls.Add("outer:after");
        return response;
    }
}

public sealed class InnerBehavior<TRequest, TResponse>(TrackedCalls calls)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        calls.Add("inner:before");
        TResponse response = await next(cancellationToken);
        calls.Add("inner:after");
        return response;
    }
}

public sealed class ShortCircuitBehavior<TRequest, TResponse>(TResponse response)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    public Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken) => Task.FromResult(response);
}

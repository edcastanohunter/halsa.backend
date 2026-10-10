using Halsa.Backend.Mediation;
using Halsa.Backend.Mediation.DependencyInjection;
using Halsa.Backend.Mediation.Publishers;
using Microsoft.Extensions.DependencyInjection;

namespace Halsa.Backend.UnitTests.Mediation;

public sealed class MediatorTests
{
    [Fact]
    public async Task Send_ShouldReachTheRegisteredHandler()
    {
        ISender sender = BuildProvider().GetRequiredService<ISender>();

        string response = await sender.Send(new Ping("hello"), CancellationToken.None);

        response.ShouldBe("pong:hello");
    }

    [Fact]
    public async Task Send_ShouldRunBehaviorsOutsideIn()
    {
        var calls = new TrackedCalls();

        ServiceProvider provider = BuildProvider(services =>
        {
            services.AddSingleton(calls);
        },
        configure =>
        {
            configure.AddOpenBehavior(typeof(OuterBehavior<,>));
            configure.AddOpenBehavior(typeof(InnerBehavior<,>));
        });

        await provider.GetRequiredService<ISender>()
            .Send(new Ping("x"), CancellationToken.None);

        calls.Calls.ShouldBe(["outer:before", "inner:before", "inner:after", "outer:after"]);
    }

    [Fact]
    public async Task Send_ShouldThrow_WhenNoHandlerIsRegistered()
    {
        ISender sender = BuildProvider().GetRequiredService<ISender>();

        await Should.ThrowAsync<HandlerNotFoundException>(
            () => sender.Send(new Unhandled(), CancellationToken.None));
    }

    [Fact]
    public async Task Send_ShouldSupportTheWeaklyTypedOverload()
    {
        ISender sender = BuildProvider().GetRequiredService<ISender>();

        object? response = await sender.Send((object)new Ping("weak"), CancellationToken.None);

        response.ShouldBe("pong:weak");
    }

    [Fact]
    public async Task Publish_ShouldReachEveryHandler()
    {
        var calls = new TrackedCalls();

        ServiceProvider provider = BuildProvider(services => services.AddSingleton(calls));

        await provider.GetRequiredService<IPublisher>()
            .Publish(new Announced("go"), CancellationToken.None);

        calls.Calls.ShouldBe(["first:go", "second:go"]);
    }

    [Fact]
    public async Task Publish_ShouldWorkThroughTheNonGenericOverload()
    {
        var calls = new TrackedCalls();

        ServiceProvider provider = BuildProvider(services => services.AddSingleton(calls));

        INotification notification = new Announced("boxed");

        await provider.GetRequiredService<IPublisher>()
            .Publish(notification, CancellationToken.None);

        calls.Calls.ShouldBe(["first:boxed", "second:boxed"]);
    }

    [Fact]
    public async Task Send_ShouldNotCallTheHandler_WhenABehaviorShortCircuits()
    {
        ServiceProvider provider = BuildProvider(
            services => services.AddSingleton(
                typeof(IPipelineBehavior<Ping, string>),
                new ShortCircuitBehavior<Ping, string>("short-circuited")));

        string response = await provider.GetRequiredService<ISender>()
            .Send(new Ping("ignored"), CancellationToken.None);

        response.ShouldBe("short-circuited");
    }

    [Fact]
    public void AddMediation_ShouldThrow_WhenNoAssemblyIsRegistered()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddMediation(_ => { }));
    }

    private static ServiceProvider BuildProvider(
        Action<IServiceCollection>? configureServices = null,
        Action<MediationConfiguration>? configureMediation = null)
    {
        var services = new ServiceCollection();

        services.AddSingleton<TrackedCalls>();

        services.AddMediation(configuration =>
        {
            configuration.RegisterServicesFromAssemblyContaining<MediatorTests>();
            configuration.NotificationPublisherType = typeof(SequentialNotificationPublisher);
            configureMediation?.Invoke(configuration);
        });

        configureServices?.Invoke(services);

        return services.BuildServiceProvider();
    }
}

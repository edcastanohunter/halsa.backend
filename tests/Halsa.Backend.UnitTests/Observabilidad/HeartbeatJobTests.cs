using System.Net;
using Halsa.Backend.Infrastructure.Observabilidad;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Observabilidad;

/// <summary>SPEC 13, paso 9: el heartbeat del Worker hacia el <em>dead man's switch</em> externo.</summary>
public sealed class HeartbeatJobTests
{
    private const string Url = "https://hc-ping.example/0b8f9c3e";

    [Fact]
    public async Task WithAUrl_ShouldSendAGet()
    {
        var handler = new HandlerDePrueba(HttpStatusCode.OK);

        (await Job(handler, Url).LatirAsync(CancellationToken.None)).ShouldBeTrue();

        handler.Recibidos.ShouldHaveSingleItem();
        handler.Recibidos[0].Method.ShouldBe(HttpMethod.Get);
        handler.Recibidos[0].RequestUri.ShouldBe(new Uri(Url));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task WithoutAUrl_ShouldSendNothing(string? url)
    {
        var handler = new HandlerDePrueba(HttpStatusCode.OK);

        (await Job(handler, url).LatirAsync(CancellationToken.None)).ShouldBeFalse();

        handler.Recibidos.ShouldBeEmpty();
    }

    [Fact]
    public async Task AFailedPing_ShouldNotThrow()
    {
        var handler = new HandlerDePrueba(new HttpRequestException("sin red"));

        (await Job(handler, Url).LatirAsync(CancellationToken.None)).ShouldBeFalse();
    }

    [Fact]
    public async Task AnErrorStatus_ShouldCountAsNotReceived()
    {
        var handler = new HandlerDePrueba(HttpStatusCode.ServiceUnavailable);

        (await Job(handler, Url).LatirAsync(CancellationToken.None)).ShouldBeFalse();
    }

    private static HeartbeatJob Job(HttpMessageHandler handler, string? url) => new(
        new HeartbeatHttpClient(handler),
        Options.Create(new ObservabilidadOptions { HeartbeatUrl = url }),
        NullLogger<HeartbeatJob>.Instance);

    private sealed class HandlerDePrueba : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly Exception? _error;

        public HandlerDePrueba(HttpStatusCode status) => _status = status;

        public HandlerDePrueba(Exception error) => _error = error;

        public List<HttpRequestMessage> Recibidos { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Recibidos.Add(request);
            return _error is not null
                ? Task.FromException<HttpResponseMessage>(_error)
                : Task.FromResult(new HttpResponseMessage(_status));
        }
    }
}

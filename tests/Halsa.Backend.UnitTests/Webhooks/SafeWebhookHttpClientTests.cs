using System.Net;
using System.Net.Sockets;
using System.Text;
using Halsa.Backend.Infrastructure.Webhooks;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Webhooks;

/// <summary>SPEC 10, paso 3: la protección SSRF al registrar la URL y en cada conexión, y las redirecciones.</summary>
public sealed class SafeWebhookHttpClientTests
{
    private static readonly IReadOnlyDictionary<string, string> SinCabeceras = new Dictionary<string, string>();

    // ── Rangos bloqueados ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("0.0.0.0")]
    [InlineData("0.255.255.255")]
    [InlineData("10.0.0.5")]
    [InlineData("10.255.255.255")]
    [InlineData("100.64.0.1")]
    [InlineData("100.127.255.255")]
    [InlineData("127.0.0.1")]
    [InlineData("127.255.255.254")]
    [InlineData("169.254.169.254")]
    [InlineData("172.16.0.1")]
    [InlineData("172.31.255.255")]
    [InlineData("192.168.1.1")]
    [InlineData("224.0.0.1")]
    [InlineData("239.255.255.255")]
    [InlineData("::1")]
    [InlineData("::")]
    [InlineData("fc00::1")]
    [InlineData("fd12:3456::1")]
    [InlineData("fe80::1")]
    [InlineData("febf::1")]
    [InlineData("::ffff:8.8.8.8")]
    [InlineData("::ffff:127.0.0.1")]
    public void EsBloqueada_ShouldBlockEveryRangeOfTheSpec(string direccion)
    {
        DestinosBloqueados.EsBloqueada(IPAddress.Parse(direccion)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("1.1.1.1")]
    [InlineData("100.63.255.255")]
    [InlineData("100.128.0.0")]
    [InlineData("172.15.255.255")]
    [InlineData("172.32.0.0")]
    [InlineData("192.169.0.1")]
    [InlineData("223.255.255.255")]
    [InlineData("240.0.0.1")]
    [InlineData("2001:4860:4860::8888")]
    [InlineData("fec0::1")]
    public void EsBloqueada_ShouldAdmitPublicAddresses(string direccion)
    {
        DestinosBloqueados.EsBloqueada(IPAddress.Parse(direccion)).ShouldBeFalse();
    }

    // ── Validación al registrar ───────────────────────────────────────────────

    [Theory]
    [InlineData("https://ejemplo.com/hook")]
    [InlineData("https://arl.example.co:8443/webhooks/halsa?origen=halsa")]
    [InlineData("https://8.8.8.8/hook")]
    public void EsValida_ShouldAdmitPublicHttpsUrls(string url)
    {
        WebhookUrlPolicy.EsValida(url, permitirLocales: false).ShouldBeTrue();
    }

    [Theory]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://127.0.0.1/hook")]
    [InlineData("https://[::1]/hook")]
    [InlineData("https://[::ffff:10.0.0.5]/hook")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://localhost/hook")]
    [InlineData("http://ejemplo.com/hook")]
    [InlineData("ftp://ejemplo.com/hook")]
    [InlineData("https://usuario:clave@ejemplo.com/hook")]
    [InlineData("https://usuario@ejemplo.com/hook")]
    [InlineData("/hook")]
    [InlineData("")]
    [InlineData(null)]
    public void EsValida_ShouldRejectWhatTheSpecForbids(string? url)
    {
        WebhookUrlPolicy.EsValida(url, permitirLocales: false).ShouldBeFalse();
    }

    [Fact]
    public void EsValida_ShouldRejectUrlsLongerThan2048()
    {
        string url = "https://ejemplo.com/" + new string('a', 2048 - "https://ejemplo.com/".Length);

        WebhookUrlPolicy.EsValida(url, permitirLocales: false).ShouldBeTrue();
        WebhookUrlPolicy.EsValida(url + "a", permitirLocales: false).ShouldBeFalse();
    }

    [Fact]
    public void EsValida_WithPermitirDestinosLocales_ShouldAdmitHttpAndLoopback_ButNotCredentials()
    {
        WebhookUrlPolicy.EsValida("http://127.0.0.1:5000/hook", permitirLocales: true).ShouldBeTrue();
        WebhookUrlPolicy.EsValida("http://localhost:5000/hook", permitirLocales: true).ShouldBeTrue();
        WebhookUrlPolicy.EsValida("http://u:p@localhost:5000/hook", permitirLocales: true).ShouldBeFalse();
    }

    // ── En la conexión ────────────────────────────────────────────────────────

    [Fact]
    public async Task ANameThatResolvesToLoopback_ShouldBeBlockedAtConnection_EvenIfTheUrlPassedValidation()
    {
        // "localhost" resolves to 127.0.0.1 (and ::1). The URL policy would have refused the name; a public name that
        // later rebinds to 127.0.0.1 reaches this same check.
        using var client = new SafeWebhookHttpClient(Opciones(permitirLocales: false));

        HttpRequestException ex = await Should.ThrowAsync<HttpRequestException>(() =>
            client.PostAsync(new Uri("https://localhost:9/hook"), "{}", SinCabeceras, CancellationToken.None));

        ex.InnerException.ShouldBeOfType<DestinoBloqueadoException>();
    }

    [Fact]
    public async Task ConectarAsync_ShouldBlockAnIpLiteralOfAPrivateRange()
    {
        await Should.ThrowAsync<DestinoBloqueadoException>(async () =>
            await SafeWebhookHttpClient.ConectarAsync(new DnsEndPoint("10.0.0.5", 443), permitirLocales: false, CancellationToken.None));
    }

    [Fact]
    public async Task ARedirect_ShouldNotBeFollowed()
    {
        await using var receptor = new ReceptorCrudo("HTTP/1.1 302 Found\r\nLocation: /otra\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        using var client = new SafeWebhookHttpClient(Opciones(permitirLocales: true));

        using HttpResponseMessage respuesta = await client.PostAsync(
            new Uri($"http://127.0.0.1:{receptor.Puerto}/hook"), "{}", SinCabeceras, CancellationToken.None);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.Found);
        receptor.Solicitudes.ShouldBe(1);
    }

    [Fact]
    public async Task PostAsync_ShouldSendTheBodyAsIsWithTheHeaders()
    {
        await using var receptor = new ReceptorCrudo("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
        using var client = new SafeWebhookHttpClient(Opciones(permitirLocales: true));

        using HttpResponseMessage respuesta = await client.PostAsync(
            new Uri($"http://127.0.0.1:{receptor.Puerto}/hook"),
            """{"a":1}""",
            new Dictionary<string, string> { ["Halsa-Event-Id"] = "abc" },
            CancellationToken.None);

        respuesta.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        receptor.UltimaSolicitud.ShouldContain("Halsa-Event-Id: abc");
        receptor.UltimaSolicitud.ShouldContain("application/json");
        receptor.UltimaSolicitud.ShouldEndWith("""{"a":1}""");
    }

    // ── Arranque ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Production", true, false)]
    [InlineData("Staging", true, false)]
    [InlineData("Production", false, true)]
    [InlineData("Development", true, true)]
    [InlineData("Testing", true, true)]
    public void Validator_ShouldRefuseDestinosLocalesOutsideDevelopmentAndTesting(
        string entorno, bool permitirLocales, bool valido)
    {
        var validator = new WebhooksOptionsValidator(entorno);

        validator.Validate(null, new WebhooksOptions { PermitirDestinosLocales = permitirLocales })
            .Succeeded.ShouldBe(valido);
    }

    private static IOptions<WebhooksOptions> Opciones(bool permitirLocales) =>
        Options.Create(new WebhooksOptions { PermitirDestinosLocales = permitirLocales, Timeout = TimeSpan.FromSeconds(5) });

    /// <summary>A one-shot HTTP/1.1 server on a loopback port that answers every request with <c>respuesta</c>.</summary>
    private sealed class ReceptorCrudo : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _bucle;
        private int _solicitudes;

        public ReceptorCrudo(string respuesta)
        {
            _listener.Start();
            _bucle = AtenderAsync(respuesta);
        }

        public int Puerto => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public int Solicitudes => Volatile.Read(ref _solicitudes);

        public string UltimaSolicitud { get; private set; } = string.Empty;

        private async Task AtenderAsync(string respuesta)
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    using TcpClient cliente = await _listener.AcceptTcpClientAsync(_cts.Token);
                    NetworkStream stream = cliente.GetStream();
                    UltimaSolicitud = await LeerAsync(stream, _cts.Token);
                    Interlocked.Increment(ref _solicitudes);
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(respuesta), _cts.Token);
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static async Task<string> LeerAsync(NetworkStream stream, CancellationToken ct)
        {
            var leido = new StringBuilder();
            byte[] buffer = new byte[4096];
            int finCabeceras = -1;
            int largo = 0;

            while (true)
            {
                int n = await stream.ReadAsync(buffer, ct);
                if (n == 0)
                {
                    break;
                }

                leido.Append(Encoding.UTF8.GetString(buffer, 0, n));
                string texto = leido.ToString();

                if (finCabeceras < 0 && (finCabeceras = texto.IndexOf("\r\n\r\n", StringComparison.Ordinal)) >= 0)
                {
                    string? cabecera = texto[..finCabeceras].Split("\r\n")
                        .FirstOrDefault(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
                    largo = cabecera is null ? 0 : int.Parse(cabecera["Content-Length:".Length..].Trim(),
                        System.Globalization.CultureInfo.InvariantCulture);
                }

                if (finCabeceras >= 0 && Encoding.UTF8.GetByteCount(texto[(finCabeceras + 4)..]) >= largo)
                {
                    return texto;
                }
            }

            return leido.ToString();
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _bucle;
            _cts.Dispose();
        }
    }
}

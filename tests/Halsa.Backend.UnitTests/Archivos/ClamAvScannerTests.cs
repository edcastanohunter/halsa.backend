using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.Infrastructure.Archivos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.UnitTests.Archivos;

/// <summary>
/// SPEC 07, step 2: <see cref="ClamAvScanner"/> against a fake <c>clamd</c> on a loopback port, which answers
/// <c>OK</c>, <c>FOUND</c>, an error, or hangs. The scanner must fail closed in every case but <c>OK</c>.
/// </summary>
public sealed class ClamAvScannerTests
{
    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\n" + new string('x', 200_000));

    private static ClamAvScanner Scanner(int puerto, TimeSpan? timeout = null) =>
        new(Options.Create(new AntivirusOptions
        {
            Host = "127.0.0.1",
            Puerto = puerto,
            Timeout = timeout ?? TimeSpan.FromSeconds(5)
        }), NullLogger<ClamAvScanner>.Instance);

    [Fact]
    public async Task Ok_ShouldBeLimpio_AndSendTheWholeFileInChunks()
    {
        await using var clamd = FakeClamd.Iniciar("stream: OK");

        ResultadoAntivirus resultado = await Scanner(clamd.Puerto).AnalizarAsync(new MemoryStream(Pdf), CancellationToken.None);

        resultado.ShouldBe(ResultadoAntivirus.Limpio);
        (await clamd.ComandoAsync).ShouldBe("zINSTREAM");
        (await clamd.RecibidoAsync).ShouldBe(Pdf);
    }

    [Fact]
    public async Task Found_ShouldBeInfectado_WithTheSignature()
    {
        await using var clamd = FakeClamd.Iniciar("stream: Eicar-Test-Signature FOUND");

        ResultadoAntivirus resultado = await Scanner(clamd.Puerto).AnalizarAsync(new MemoryStream(Pdf), CancellationToken.None);

        resultado.ShouldBe(ResultadoAntivirus.Infectado("Eicar-Test-Signature"));
    }

    [Fact]
    public async Task AnError_ShouldBeNoDisponible()
    {
        await using var clamd = FakeClamd.Iniciar("INSTREAM size limit exceeded. ERROR");

        ResultadoAntivirus resultado = await Scanner(clamd.Puerto).AnalizarAsync(new MemoryStream(Pdf), CancellationToken.None);

        resultado.ShouldBeOfType<ResultadoAntivirus.NoDisponibleResultado>();
    }

    [Fact]
    public async Task AHangingClamd_ShouldBeNoDisponible_AfterTheTimeout()
    {
        await using var clamd = FakeClamd.Iniciar(respuesta: null);

        ResultadoAntivirus resultado = await Scanner(clamd.Puerto, TimeSpan.FromMilliseconds(500))
            .AnalizarAsync(new MemoryStream(Pdf), CancellationToken.None);

        resultado.ShouldBe(ResultadoAntivirus.NoDisponible("timeout"));
    }

    [Fact]
    public async Task NobodyListening_ShouldBeNoDisponible()
    {
        int puerto = FakeClamd.PuertoLibre();

        ResultadoAntivirus resultado = await Scanner(puerto).AnalizarAsync(new MemoryStream(Pdf), CancellationToken.None);

        resultado.ShouldBeOfType<ResultadoAntivirus.NoDisponibleResultado>();
    }

    [Fact]
    public async Task TheCallersCancellation_ShouldPropagate()
    {
        await using var clamd = FakeClamd.Iniciar(respuesta: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            Scanner(clamd.Puerto).AnalizarAsync(new MemoryStream(Pdf), cts.Token));
    }

    [Theory]
    [InlineData("stream: OK", "LIMPIO", null)]
    [InlineData("stream: Win.Test.EICAR_HDB-1 FOUND", "INFECTADO", "Win.Test.EICAR_HDB-1")]
    [InlineData("stream: Eicar-Test-Signature FOUND", "INFECTADO", "Eicar-Test-Signature")]
    [InlineData("stream: lstat() failed. ERROR", "NO_DISPONIBLE", null)]
    [InlineData("", "NO_DISPONIBLE", null)]
    public void Interpretar_ShouldReadTheClamdReply(string respuesta, string esperado, string? firma)
    {
        ResultadoAntivirus resultado = ClamAvScanner.Interpretar(respuesta);

        string obtenido = resultado switch
        {
            ResultadoAntivirus.LimpioResultado => "LIMPIO",
            ResultadoAntivirus.InfectadoResultado => "INFECTADO",
            _ => "NO_DISPONIBLE"
        };
        obtenido.ShouldBe(esperado);
        (resultado as ResultadoAntivirus.InfectadoResultado)?.Firma.ShouldBe(firma);
    }

    // ── Health check ──────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthCheck_ShouldBeHealthy_WhenClamdAnswersPong()
    {
        await using var clamd = FakeClamd.Iniciar("PONG");

        HealthCheckResult result = await HealthCheck(clamd.Puerto).CheckHealthAsync(Contexto());

        result.Status.ShouldBe(HealthStatus.Healthy);
        (await clamd.ComandoAsync).ShouldBe("zPING");
    }

    [Fact]
    public async Task HealthCheck_ShouldReportItsFailureStatus_WhenClamdIsDown()
    {
        HealthCheckResult result = await HealthCheck(FakeClamd.PuertoLibre()).CheckHealthAsync(Contexto());

        result.Status.ShouldBe(HealthStatus.Degraded);
    }

    private static AntivirusHealthCheck HealthCheck(int puerto) =>
        new(Options.Create(new AntivirusOptions { Host = "127.0.0.1", Puerto = puerto }));

    private static HealthCheckContext Contexto() => new()
    {
        Registration = new HealthCheckRegistration(
            AntivirusHealthCheck.Nombre, Substitute(), HealthStatus.Degraded, tags: null)
    };

    private static IHealthCheck Substitute() => NSubstitute.Substitute.For<IHealthCheck>();

    /// <summary>
    /// One connection of a fake <c>clamd</c>: reads the command up to <c>\0</c> and, for <c>INSTREAM</c>, the chunks up
    /// to the empty one; then answers <c>{respuesta}\0</c>, or never answers when <c>respuesta</c> is null.
    /// </summary>
    private sealed class FakeClamd : IAsyncDisposable
    {
        private readonly TcpListener _listener;
        private readonly TaskCompletionSource<string> _comando = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<byte[]> _recibido = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _servidor;

        private FakeClamd(string? respuesta)
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            _servidor = AtenderAsync(respuesta);
        }

        public int Puerto => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public Task<string> ComandoAsync => _comando.Task;

        public Task<byte[]> RecibidoAsync => _recibido.Task;

        public static FakeClamd Iniciar(string? respuesta) => new(respuesta);

        public static int PuertoLibre()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            int puerto = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return puerto;
        }

        private async Task AtenderAsync(string? respuesta)
        {
            try
            {
                using TcpClient cliente = await _listener.AcceptTcpClientAsync(_cts.Token);
                NetworkStream red = cliente.GetStream();

                string comando = await LeerComandoAsync(red);
                _comando.TrySetResult(comando);

                if (comando == "zINSTREAM")
                {
                    _recibido.TrySetResult(await LeerFragmentosAsync(red));
                }

                if (respuesta is null)
                {
                    await Task.Delay(Timeout.Infinite, _cts.Token);
                }

                await red.WriteAsync(Encoding.ASCII.GetBytes(respuesta + "\0"), _cts.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or SocketException)
            {
                // Disposed while waiting, or the client went away first.
            }
        }

        private async Task<string> LeerComandoAsync(NetworkStream red)
        {
            var comando = new StringBuilder();
            byte[] uno = new byte[1];
            while (await red.ReadAsync(uno, _cts.Token) == 1 && uno[0] != 0)
            {
                comando.Append((char)uno[0]);
            }

            return comando.ToString();
        }

        private async Task<byte[]> LeerFragmentosAsync(NetworkStream red)
        {
            using var recibido = new MemoryStream();
            byte[] longitud = new byte[4];

            while (true)
            {
                await red.ReadExactlyAsync(longitud, _cts.Token);
                int tamano = BinaryPrimitives.ReadInt32BigEndian(longitud);
                if (tamano == 0)
                {
                    return recibido.ToArray();
                }

                byte[] fragmento = new byte[tamano];
                await red.ReadExactlyAsync(fragmento, _cts.Token);
                recibido.Write(fragmento);
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _listener.Stop();
            await _servidor;
            _cts.Dispose();
        }
    }
}

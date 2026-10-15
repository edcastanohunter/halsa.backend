using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.IntegrationTests.Webhooks;

/// <summary>
/// A webhook receiver for the SPEC 10 tests: Kestrel on a loopback port, recording every request and answering with the
/// queued status codes (then <see cref="StatusPorDefecto"/>). Only reachable with <c>PermitirDestinosLocales</c>.
/// </summary>
internal sealed class ReceptorPrueba : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<int> _respuestas = new();

    private ReceptorPrueba(WebApplication app) => _app = app;

    public ConcurrentQueue<Recibido> Recibidos { get; } = new();

    public int StatusPorDefecto { get; set; } = 200;

    /// <summary>The receiver's URL for <paramref name="ruta"/>.</summary>
    public string Url(string ruta = "/hook") => $"{Base}{ruta}";

    private string Base { get; set; } = string.Empty;

    public static async Task<ReceptorPrueba> IniciarAsync()
    {
        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();

        WebApplication app = builder.Build();
        var receptor = new ReceptorPrueba(app);

        app.Run(async http =>
        {
            using var lector = new StreamReader(http.Request.Body, Encoding.UTF8);
            string cuerpo = await lector.ReadToEndAsync();

            receptor.Recibidos.Enqueue(new Recibido(
                http.Request.Path,
                http.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
                cuerpo));

            int status = receptor._respuestas.TryDequeue(out int siguiente) ? siguiente : receptor.StatusPorDefecto;
            http.Response.StatusCode = status;
            if (status is >= 300 and < 400)
            {
                http.Response.Headers.Location = "/otra";
            }
        });

        await app.StartAsync();
        receptor.Base = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();

        return receptor;
    }

    /// <summary>The next requests answer these codes, in order.</summary>
    public void Responder(params int[] status)
    {
        foreach (int s in status)
        {
            _respuestas.Enqueue(s);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    public sealed record Recibido(string Ruta, IReadOnlyDictionary<string, string> Cabeceras, string Cuerpo)
    {
        public string Cabecera(string nombre) => Cabeceras[nombre];

        /// <summary>The <c>v1</c> values of <c>Halsa-Signature</c>, in order.</summary>
        public IReadOnlyList<string> Firmas => [.. Cabecera("Halsa-Signature").Split(',')
            .Where(p => p.StartsWith("v1=", StringComparison.Ordinal)).Select(p => p[3..])];

        /// <summary>Verifies one <c>v1</c> as a receiver would: HMAC-SHA256(secreto, t + "." + cuerpo).</summary>
        public bool FirmaValida(byte[] secreto, int indice = 0)
        {
            string t = Cabecera("Halsa-Signature").Split(',').Single(p => p.StartsWith("t=", StringComparison.Ordinal))[2..];
            long.Parse(t, CultureInfo.InvariantCulture).ShouldBeGreaterThan(0);

            byte[] esperado = HMACSHA256.HashData(secreto, Encoding.UTF8.GetBytes($"{t}.{Cuerpo}"));
            return CryptographicOperations.FixedTimeEquals(esperado, Convert.FromHexString(Firmas[indice]));
        }
    }
}

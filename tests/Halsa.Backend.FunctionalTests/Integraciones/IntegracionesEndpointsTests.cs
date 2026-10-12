using System.Net;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.Infrastructure.Outbox;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Integraciones;

/// <summary>
/// SPEC 05, paso 7: <c>/v1/integraciones/{furat|furep}</c> de punta a punta. Cada test usa un tenant o un número
/// de reporte propio, así que no depende del orden de los demás.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class IntegracionesEndpointsTests(ApiFactory factory)
{
    private const string Furats = "v1/integraciones/furat";
    private const string Fureps = "v1/integraciones/furep";

    private static string Numero() => $"POS-{Guid.NewGuid():N}"[..30];

    // ── Radicación ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostFurat_ShouldAnswer202_WithLocationAndRadicado()
    {
        string tenant = TenantNuevo("ARL_F");
        HttpResponseMessage response = await PostAsync(IntegracionArl(factory, tenant), Furats, Furat(Numero()));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonObject body = await BodyAsync(response);

        body["estado"]!.GetValue<string>().ShouldBe("RADICADO");
        body["radicado"]!.GetValue<string>().ShouldBe($"ATEL-{tenant}-{AnioColombia()}-000001");
        body["plazos"]!.AsArray().ShouldBeEmpty();

        Guid solicitudId = body["solicitudId"]!.GetValue<Guid>();
        response.Headers.Location!.ToString().ShouldBe($"/v1/integraciones/furat/{solicitudId}");
    }

    [Fact]
    public async Task SameKey_SameBody_ShouldReplay202_WithTheSameRadicado()
    {
        HttpClient client = IntegracionArl(factory, "ARL_A");
        JsonObject cuerpo = Furat(Numero());
        Guid llave = Guid.NewGuid();

        JsonObject primera = await BodyAsync(await PostAsync(client, Furats, cuerpo, llave));
        HttpResponseMessage segunda = await PostAsync(client, Furats, cuerpo, llave);

        segunda.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonObject repetida = await BodyAsync(segunda);
        repetida["solicitudId"]!.GetValue<Guid>().ShouldBe(primera["solicitudId"]!.GetValue<Guid>());
        repetida["radicado"]!.GetValue<string>().ShouldBe(primera["radicado"]!.GetValue<string>());
    }

    [Fact]
    public async Task SameKey_AnotherBody_ShouldAnswer409()
    {
        HttpClient client = IntegracionArl(factory, "ARL_A");
        Guid llave = Guid.NewGuid();

        await PostAsync(client, Furats, Furat(Numero()), llave);
        HttpResponseMessage response = await PostAsync(client, Furats, Furat(Numero()), llave);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await BodyAsync(response))["errorCode"]!.GetValue<string>().ShouldBe("Integracion.IdempotenciaConflicto");
    }

    [Fact]
    public async Task ConcurrentPosts_WithTheSameKey_ShouldBothAnswer202_WithTheSameRadicado()
    {
        HttpClient client = IntegracionArl(factory, "ARL_A");
        JsonObject cuerpo = Furat(Numero());
        Guid llave = Guid.NewGuid();

        HttpResponseMessage[] respuestas = await Task.WhenAll(
            PostAsync(client, Furats, cuerpo, llave),
            PostAsync(client, Furats, cuerpo, llave));

        respuestas.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Accepted);
        string[] radicados = await Task.WhenAll(respuestas.Select(async r => (await BodyAsync(r))["radicado"]!.GetValue<string>()));
        radicados.Distinct().ShouldHaveSingleItem();
    }

    [Fact]
    public async Task AnotherKey_SameReportNumber_ShouldAnswer409_WithTheExistingRadicado()
    {
        HttpClient client = IntegracionArl(factory, "ARL_A");
        string numero = Numero();

        string radicado = (await BodyAsync(await PostAsync(client, Furats, Furat(numero))))["radicado"]!.GetValue<string>();
        HttpResponseMessage response = await PostAsync(client, Furats, Furat(numero, trabajadorNumero: "555666777"));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        JsonObject body = await BodyAsync(response);
        body["errorCode"]!.GetValue<string>().ShouldBe("Integracion.ReporteYaRadicado");
        body["radicado"]!.GetValue<string>().ShouldBe(radicado);
    }

    [Fact]
    public async Task WithoutIdempotencyKey_ShouldAnswer400()
    {
        HttpResponseMessage response = await PostAsync(IntegracionArl(factory, "ARL_A"), Furats, Furat(Numero()), sinLlave: true);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["errorCode"]!.GetValue<string>().ShouldBe("Integracion.IdempotencyKeyInvalida");
    }

    [Fact]
    public async Task FutureEvent_ShouldAnswer400_WithTheErrorOnEventoFechaHora()
    {
        HttpResponseMessage response = await PostAsync(
            IntegracionArl(factory, "ARL_A"), Furats, Furat(Numero(), fechaHora: DateTimeOffset.UtcNow.AddDays(2)));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        JsonObject errors = (await BodyAsync(response))["errors"]!.AsObject();
        errors.Select(e => e.Key).ShouldBe(["evento.fechaHora"]);
        errors["evento.fechaHora"]![0]!.GetValue<string>().ShouldBe("La fecha del evento no puede ser futura.");
    }

    [Fact]
    public async Task MalformedJson_ShouldAnswer400_OnTheRoot()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Furats)
        {
            Content = new StringContent("{ esto no es json", System.Text.Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());

        HttpResponseMessage response = await IntegracionArl(factory, "ARL_A").SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["errors"]!.AsObject().ContainsKey("$").ShouldBeTrue();
    }

    // ── Autorización ──────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalistaArl_ShouldAnswer403()
    {
        HttpResponseMessage response = await PostAsync(AnalistaArlA(factory), Furats, Furat(Numero()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task IpsIntegration_ShouldAnswer403()
    {
        HttpResponseMessage response = await PostAsync(IntegracionIps(factory), Furats, Furat(Numero()));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── Consulta de estado ────────────────────────────────────────────────────

    [Fact]
    public async Task GetFurat_ShouldReturnRadicado_UntilTheOutboxRuns()
    {
        HttpClient client = IntegracionArl(factory, "ARL_A");
        JsonObject radicacion = await BodyAsync(await PostAsync(client, Furats, Furat(Numero())));
        Guid solicitudId = radicacion["solicitudId"]!.GetValue<Guid>();

        HttpResponseMessage response = await client.GetAsync($"{Furats}/{solicitudId}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject estado = await BodyAsync(response);
        estado["estado"]!.GetValue<string>().ShouldBe("RADICADO");
        estado["radicado"]!.GetValue<string>().ShouldBe(radicacion["radicado"]!.GetValue<string>());
        estado["casoId"]!.GetValue<Guid>().ShouldBe(radicacion["casoId"]!.GetValue<Guid>());
    }

    [Fact]
    public async Task GetFurat_OfAnotherTenant_ShouldAnswer404()
    {
        JsonObject deB = await BodyAsync(await PostAsync(IntegracionArl(factory, "ARL_B"), Furats, Furat(Numero())));

        HttpResponseMessage response = await IntegracionArl(factory, "ARL_A")
            .GetAsync($"{Furats}/{deB["solicitudId"]!.GetValue<Guid>()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetFurep_OfAFuratRequest_ShouldAnswer404()
    {
        HttpClient client = IntegracionArl(factory, "ARL_A");
        JsonObject furat = await BodyAsync(await PostAsync(client, Furats, Furat(Numero())));

        HttpResponseMessage response = await client.GetAsync($"{Fureps}/{furat["solicitudId"]!.GetValue<Guid>()}");

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    // ── Validación asíncrona ──────────────────────────────────────────────────

    [Fact]
    public async Task GetFurat_AfterTheOutbox_ShouldReturnValidado()
    {
        HttpClient client = IntegracionArl(factory, TenantNuevo("ARL_V"));
        Guid solicitudId = (await BodyAsync(await PostAsync(client, Furats, Furat(Numero()))))["solicitudId"]!.GetValue<Guid>();

        await ProcesarOutboxAsync();

        JsonObject estado = await BodyAsync(await client.GetAsync($"{Furats}/{solicitudId}"));
        estado["estado"]!.GetValue<string>().ShouldBe("VALIDADO");
    }

    [Fact]
    public async Task SecondFurat_OfTheSameWorker_ThreeHoursLater_ShouldReturnDuplicado()
    {
        HttpClient client = IntegracionArl(factory, TenantNuevo("ARL_D"));
        var evento = new DateTimeOffset(2026, 10, 7, 14, 30, 0, TimeSpan.FromHours(-5));

        await PostAsync(client, Furats, Furat(Numero(), fechaHora: evento));
        Guid segunda = (await BodyAsync(await PostAsync(client, Furats, Furat(Numero(), fechaHora: evento.AddHours(3)))))
            ["solicitudId"]!.GetValue<Guid>();

        await ProcesarOutboxAsync();

        JsonObject estado = await BodyAsync(await client.GetAsync($"{Furats}/{segunda}"));
        estado["estado"]!.GetValue<string>().ShouldBe("DUPLICADO");
    }

    [Fact]
    public async Task WorkerEndingIn000_ShouldReturnRechazado_WithItsReason()
    {
        HttpClient client = IntegracionArl(factory, TenantNuevo("ARL_R"));
        Guid solicitudId = (await BodyAsync(await PostAsync(client, Furats, Furat(Numero(), trabajadorNumero: "52000000"))))
            ["solicitudId"]!.GetValue<Guid>();

        await ProcesarOutboxAsync();

        JsonObject estado = await BodyAsync(await client.GetAsync($"{Furats}/{solicitudId}"));
        estado["estado"]!.GetValue<string>().ShouldBe("RECHAZADO");
        estado["motivoRechazo"]!["code"]!.GetValue<string>().ShouldBe("Integracion.NoAfiliado");
    }

    // ── FUREP ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostFurep_ShouldAnswer202()
    {
        HttpClient client = IntegracionArl(factory, "ARL_A");

        HttpResponseMessage response = await PostAsync(client, Fureps, Furep(Numero()));

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        JsonObject body = await BodyAsync(response);
        response.Headers.Location!.ToString().ShouldBe($"/v1/integraciones/furep/{body["solicitudId"]!.GetValue<Guid>()}");
        (await client.GetAsync($"{Fureps}/{body["solicitudId"]!.GetValue<Guid>()}")).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    /// <summary>The hosted outbox dispatcher is removed in tests; drain it the way it would run.</summary>
    private async Task ProcesarOutboxAsync()
    {
        var processor = new OutboxProcessor(
            factory.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new OutboxOptions { BatchSize = 100, MaxAttempts = 5 }),
            NullLogger<OutboxProcessor>.Instance);

        while (await processor.ProcessBatchAsync(CancellationToken.None) > 0)
        {
        }
    }

    internal static int AnioColombia() => DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5)).Year;
}

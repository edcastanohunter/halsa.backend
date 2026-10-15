using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using static Halsa.Backend.FunctionalTests.Integraciones.Webhooks.WebhooksClientes;

namespace Halsa.Backend.FunctionalTests.Integraciones.Webhooks;

/// <summary>
/// SPEC 10, paso 4: la API de suscripciones, de punta a punta. Cada test usa un tenant o una IPS propios, así que nunca
/// ve las suscripciones de otro.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class SuscripcionesWebhookEndpointsTests(ApiFactory factory)
{
    private const string UrlValida = "https://receptor.example.com/halsa";

    // ── Alta ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Crear_ShouldAnswer201_WithTheSecretOnlyOnce_AndStoreItWrapped()
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_ALTA"));

        HttpResponseMessage response = await CrearAsync(arl, UrlValida, "plazo.vencido", "caso.validado");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonObject body = await BodyAsync(response);
        Guid id = body["id"]!.GetValue<Guid>();
        response.Headers.Location!.ToString().ShouldBe($"/v1/integraciones/suscripciones-webhook/{id}");
        body["estado"]!.GetValue<string>().ShouldBe("ACTIVA");
        body["eventos"]!.AsArray().Select(e => e!.GetValue<string>()).ShouldBe(["caso.validado", "plazo.vencido"]);

        byte[] secreto = Base64Url.DecodeFromChars(body["secreto"]!.GetValue<string>());
        secreto.Length.ShouldBe(32);

        // In no other answer.
        (await arl.GetStringAsync(Ruta)).ShouldNotContain("secreto\"", Case.Insensitive);
        string detalle = await arl.GetStringAsync($"{Ruta}/{id}");
        detalle.ShouldNotContain("\"secreto\"");
        detalle.ShouldNotContain(body["secreto"]!.GetValue<string>());

        // And never in clear in the table.
        byte[] ct = (await EscalarAsync<byte[]>(factory,
            "SELECT secreto_ct FROM integracion.suscripciones_webhook WHERE id = @id", ("id", id)))!;
        ct.AsSpan().IndexOf(secreto).ShouldBe(-1);
        (await EscalarAsync<string>(factory,
            "SELECT emisor FROM integracion.suscripciones_webhook WHERE id = @id", ("id", id))).ShouldStartWith("tenant:WH_ALTA");
    }

    [Theory]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("http://ejemplo.com/hook")]
    [InlineData("https://usuario:clave@ejemplo.com/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("no es una url")]
    public async Task Crear_WithAForbiddenUrl_ShouldAnswer400(string url)
    {
        HttpResponseMessage response = await CrearAsync(IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_URL")), url, "plazo.vencido");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["detail"]!.GetValue<string>().ShouldContain("https://");
    }

    [Fact]
    public async Task AnIps_SubscribingToPlazoVencido_ShouldAnswer400()
    {
        HttpResponseMessage response = await CrearAsync(IntegracionIps(factory, IpsNueva()), UrlValida, "plazo.vencido");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnArl_SubscribingToCasoRemitido_ShouldAnswer400()
    {
        HttpResponseMessage response = await CrearAsync(
            IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_REMITIDO")), UrlValida, "plazo.vencido", "caso.remitido");

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AnIps_ShouldSubscribeToItsOwnEvents()
    {
        string reps = IpsNueva();

        HttpResponseMessage response = await CrearAsync(IntegracionIps(factory, reps), UrlValida, "caso.remitido", "remision.revocada");

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        Guid id = (await BodyAsync(response))["id"]!.GetValue<Guid>();
        (await EscalarAsync<string>(factory,
            "SELECT emisor FROM integracion.suscripciones_webhook WHERE id = @id", ("id", id))).ShouldBe($"ips:{reps}");
    }

    [Fact]
    public async Task Crear_WithoutEventsOrWithAnUnknownOne_ShouldAnswer400()
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_EVENTOS"));

        (await CrearAsync(arl, UrlValida)).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await CrearAsync(arl, UrlValida, "caso.inventado")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Actor ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task APerson_EvenWithWebhooksManage_ShouldAnswer403()
    {
        HttpResponseMessage response = await CrearAsync(AnalistaConPermiso(factory, "ARL_A"), UrlValida, "plazo.vencido");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WithoutWebhooksManage_ShouldAnswer403()
    {
        HttpClient sinPermiso = ConClaims(factory, new Dictionary<string, object>
        {
            ["sub"] = "int-arl-sin-permiso",
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["actor_tipo"] = "integracion",
            ["tenant_id"] = "ARL_A",
            ["permissions"] = new[] { "integracion:furat:write" }
        });

        (await sinPermiso.GetAsync(Ruta)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnotherEmisor_ShouldNotSeeTheSubscription()
    {
        HttpClient duena = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_DUENA"));
        HttpClient otra = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_OTRA"));
        Guid id = await CrearIdAsync(duena);

        (await otra.GetAsync($"{Ruta}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otra.PatchAsJsonAsync($"{Ruta}/{id}", new { estado = "PAUSADA" })).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otra.DeleteAsync($"{Ruta}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otra.PostAsync($"{Ruta}/{id}/rotar-secreto", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otra.GetAsync($"{Ruta}/{id}/entregas")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await otra.GetFromJsonAsync<JsonArray>(Ruta))!.ShouldBeEmpty();
    }

    // ── Cambio ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Patch_ShouldChangeOnlyWhatArrives()
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_PATCH"));
        Guid id = await CrearIdAsync(arl);

        HttpResponseMessage response = await arl.PatchAsJsonAsync($"{Ruta}/{id}", new { estado = "PAUSADA" });

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject body = await BodyAsync(response);
        body["estado"]!.GetValue<string>().ShouldBe("PAUSADA");
        body["url"]!.GetValue<string>().ShouldBe(UrlValida);

        response = await arl.PatchAsJsonAsync($"{Ruta}/{id}", new { url = "https://otro.example.com/h", eventos = new[] { "caso.rechazado" } });
        body = await BodyAsync(response);
        body["url"]!.GetValue<string>().ShouldBe("https://otro.example.com/h");
        body["eventos"]!.AsArray().Select(e => e!.GetValue<string>()).ShouldBe(["caso.rechazado"]);
        body["estado"]!.GetValue<string>().ShouldBe("PAUSADA");
    }

    [Theory]
    [InlineData("SUSPENDIDA")]
    [InlineData("ELIMINADA")]
    [InlineData("activa")]
    public async Task Patch_ToAStateTheEmisorCannotSet_ShouldAnswer400(string estado)
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_ESTADO"));
        Guid id = await CrearIdAsync(arl);

        (await arl.PatchAsJsonAsync($"{Ruta}/{id}", new { estado })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Patch_WithAPrivateUrl_ShouldAnswer400()
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_PATCH_URL"));
        Guid id = await CrearIdAsync(arl);

        (await arl.PatchAsJsonAsync($"{Ruta}/{id}", new { url = "https://192.168.0.10/h" })).StatusCode
            .ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ReactivatingASuspendedSubscription_ShouldResetItsFailures()
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_REACTIVAR"));
        Guid id = await CrearIdAsync(arl);
        await EjecutarAsync(factory, """
            UPDATE integracion.suscripciones_webhook SET estado = 'SUSPENDIDA', fallos_consecutivos = 50 WHERE id = @id
            """, ("id", id));

        JsonObject body = await BodyAsync(await arl.PatchAsJsonAsync($"{Ruta}/{id}", new { estado = "ACTIVA" }));

        body["estado"]!.GetValue<string>().ShouldBe("ACTIVA");
        body["fallosConsecutivos"]!.GetValue<int>().ShouldBe(0);
    }

    // ── Baja ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Delete_ShouldAnswer204_AndDiscardThePendingDeliveriesOnly()
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_BAJA"));
        Guid id = await CrearIdAsync(arl);
        Guid pendiente = await InsertEntregaAsync(factory, id);
        Guid entregada = await InsertEntregaAsync(factory, id, "ENTREGADA");

        (await arl.DeleteAsync($"{Ruta}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NoContent);

        (await EscalarAsync<string>(factory, "SELECT estado FROM integracion.suscripciones_webhook WHERE id = @id", ("id", id)))
            .ShouldBe("ELIMINADA");
        (await EscalarAsync<string>(factory, "SELECT estado FROM integracion.entregas_webhook WHERE id = @id", ("id", pendiente)))
            .ShouldBe("DESCARTADA");
        (await EscalarAsync<string>(factory, "SELECT estado FROM integracion.entregas_webhook WHERE id = @id", ("id", entregada)))
            .ShouldBe("ENTREGADA");
        (await arl.GetAsync($"{Ruta}/{id}")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await arl.GetFromJsonAsync<JsonArray>(Ruta))!.ShouldBeEmpty();
    }

    // ── Rotación ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task RotarSecreto_ShouldReturnANewSecret_AndKeepThePreviousFor24Hours()
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_ROTAR"));
        HttpResponseMessage creada = await CrearAsync(arl, UrlValida, "plazo.vencido");
        JsonObject alta = await BodyAsync(creada);
        Guid id = alta["id"]!.GetValue<Guid>();

        HttpResponseMessage response = await arl.PostAsync($"{Ruta}/{id}/rotar-secreto", null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        JsonObject body = await BodyAsync(response);
        string nuevo = body["secreto"]!.GetValue<string>();
        nuevo.ShouldNotBe(alta["secreto"]!.GetValue<string>());
        Base64Url.DecodeFromChars(nuevo).Length.ShouldBe(32);
        body["secretoAnteriorExpiraEn"]!.GetValue<DateTimeOffset>()
            .ShouldBe(DateTimeOffset.UtcNow.AddHours(24), TimeSpan.FromMinutes(1));

        (await EscalarAsync<bool>(factory, """
            SELECT secreto_anterior_ct IS NOT NULL AND secreto_anterior_ct <> secreto_ct
            FROM integracion.suscripciones_webhook WHERE id = @id
            """, ("id", id))).ShouldBeTrue();
    }

    // ── Entregas ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task Entregas_ShouldBePagedAndFiltered_WithoutThePayload()
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_ENTREGAS"));
        Guid id = await CrearIdAsync(arl);
        await InsertEntregaAsync(factory, id);
        await InsertEntregaAsync(factory, id);
        Guid deadLetter = await InsertEntregaAsync(factory, id, "DEAD_LETTER");

        JsonObject pagina = (await arl.GetFromJsonAsync<JsonObject>($"{Ruta}/{id}/entregas?pageSize=2", IngestaClientes.JsonOptions))!;
        pagina["totalCount"]!.GetValue<int>().ShouldBe(3);
        pagina["items"]!.AsArray().Count.ShouldBe(2);
        pagina["items"]![0]!.AsObject().ContainsKey("payload").ShouldBeFalse();

        JsonObject filtrada = (await arl.GetFromJsonAsync<JsonObject>($"{Ruta}/{id}/entregas?estado=DEAD_LETTER", IngestaClientes.JsonOptions))!;
        filtrada["items"]!.AsArray().Single()!["id"]!.GetValue<Guid>().ShouldBe(deadLetter);

        (await arl.GetAsync($"{Ruta}/{id}/entregas?estado=PERDIDA")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    // ── Reintento ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("DEAD_LETTER")]
    [InlineData("DESCARTADA")]
    public async Task Reintentar_FromDeadLetterOrDescartada_ShouldAnswer202_AndRequeueItAsNew(string estado)
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_REINTENTAR"));
        Guid id = await CrearIdAsync(arl);
        Guid entrega = await InsertEntregaAsync(factory, id, estado);

        HttpResponseMessage response = await arl.PostAsync($"{Ruta}/{id}/entregas/{entrega}/reintentar", null);

        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await EscalarAsync<string>(factory, "SELECT estado FROM integracion.entregas_webhook WHERE id = @id", ("id", entrega)))
            .ShouldBe("PENDIENTE");
        (await EscalarAsync<int>(factory, "SELECT intentos FROM integracion.entregas_webhook WHERE id = @id", ("id", entrega)))
            .ShouldBe(0);
        (await EscalarAsync<bool>(factory, """
            SELECT primer_intento_en > now() - interval '1 minute' AND proximo_intento_en = primer_intento_en
            FROM integracion.entregas_webhook WHERE id = @id
            """, ("id", entrega))).ShouldBeTrue();
    }

    [Theory]
    [InlineData("PENDIENTE")]
    [InlineData("ENTREGADA")]
    public async Task Reintentar_FromAnotherState_ShouldAnswer409(string estado)
    {
        HttpClient arl = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_REINT_409"));
        Guid id = await CrearIdAsync(arl);
        Guid entrega = await InsertEntregaAsync(factory, id, estado);

        (await arl.PostAsync($"{Ruta}/{id}/entregas/{entrega}/reintentar", null)).StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Reintentar_ADeliveryOfAnotherSubscriptionOrEmisor_ShouldAnswer404()
    {
        HttpClient duena = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_REINT_A"));
        HttpClient otra = IntegracionArl(factory, IngestaClientes.TenantNuevo("WH_REINT_B"));
        Guid id = await CrearIdAsync(duena);
        Guid otraSuscripcion = await CrearIdAsync(duena);
        Guid entrega = await InsertEntregaAsync(factory, id, "DEAD_LETTER");

        (await otra.PostAsync($"{Ruta}/{id}/entregas/{entrega}/reintentar", null)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await duena.PostAsync($"{Ruta}/{otraSuscripcion}/entregas/{entrega}/reintentar", null)).StatusCode
            .ShouldBe(HttpStatusCode.NotFound);
    }

    private static async Task<Guid> CrearIdAsync(HttpClient client)
    {
        HttpResponseMessage response = await CrearAsync(client, UrlValida, "plazo.vencido");
        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        return (await BodyAsync(response))["id"]!.GetValue<Guid>();
    }
}

using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Net.Http.Headers;

namespace Halsa.Bff.Paciente.Tests;

/// <summary>
/// SPEC 12, step 5: the BFF of the patient portal. The anti-CSRF header, the session cookie and its attributes, the
/// allowlist of the proxy and the token handling, all against stub downstreams.
/// </summary>
public sealed partial class BffTests : IClassFixture<BffFactory>
{
    private readonly BffFactory _factory;

    public BffTests(BffFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        _factory = factory;
        while (_factory.Api.Recibidos.TryDequeue(out _)) { }
        while (_factory.Keycloak.Recibidos.TryDequeue(out _)) { }
    }

    // ── Anti-CSRF and no session ──────────────────────────────────────────────

    [Fact]
    public async Task WithoutTheCsrfHeader_TheApiAndTheLogoutShouldAnswer403()
    {
        HttpClient cliente = _factory.Cliente();
        string cookie = await SesionAsync(cliente);

        (await cliente.SendAsync(Request(HttpMethod.Get, "/paciente/api/mis-casos", cookie, csrf: false)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cliente.SendAsync(Request(HttpMethod.Get, "/paciente/api/mis-casos", cookie: null, csrf: false)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cliente.SendAsync(Request(HttpMethod.Get, "/paciente/api/mis-casos", cookie, csrf: false, valorCsrf: "0")))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await cliente.SendAsync(Request(HttpMethod.Post, "/paciente/bff/logout", cookie, csrf: false)))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        _factory.Api.Recibidos.ShouldBeEmpty();
    }

    [Fact]
    public async Task WithTheHeaderButNoSession_TheApiShouldAnswer401_NeverARedirect()
    {
        HttpResponseMessage response = await _factory.Cliente()
            .SendAsync(Request(HttpMethod.Get, "/paciente/api/mis-casos", cookie: null));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.Location.ShouldBeNull();
        _factory.Api.Recibidos.ShouldBeEmpty();
    }

    [Fact]
    public async Task Usuario_WithoutASession_ShouldAnswer401()
    {
        (await _factory.Cliente().GetAsync("/paciente/bff/usuario")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    // ── The allowlist ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/paciente/api/%2e%2e/casos")]
    [InlineData("/paciente/api/%2E%2E/%2E%2E/v1/casos")]
    [InlineData("/paciente/api/../casos")]
    [InlineData("/paciente/api/mis-casos/%2e%2e/%2e%2e/%2e%2e/casos")]
    [InlineData("/paciente/api/..%2f..%2fcasos")]
    [InlineData("/paciente/api/..%5c..%5ccasos")]
    [InlineData("/paciente/apix/casos")]
    [InlineData("/v1/casos")]
    [InlineData("/v1/paciente/mis-casos")]
    [InlineData("/casos")]
    public async Task APathOutOfThePrefix_ShouldAnswer404_AndNeverBeForwarded(string ruta)
    {
        HttpClient cliente = _factory.Cliente();
        string cookie = await SesionAsync(cliente);

        HttpResponseMessage response = await cliente.SendAsync(Request(HttpMethod.Get, ruta, cookie));

        response.StatusCode.ShouldBe(
            HttpStatusCode.NotFound, string.Join(", ", _factory.Api.Recibidos.Select(r => r.Uri.OriginalString)));
        _factory.Api.Recibidos.ShouldBeEmpty();
    }

    // ── The session cookie ────────────────────────────────────────────────────

    [Fact]
    public async Task TheSessionCookie_ShouldBeHostPrefixed_HttpOnlySecureStrict_AndCarryNoJwt()
    {
        HttpResponseMessage response = await _factory.Cliente().PostAsync("/pruebas/sesion", content: null);

        SetCookieHeaderValue cookie = SetCookieHeaderValue
            .ParseList([.. response.Headers.GetValues("Set-Cookie")])
            .ShouldHaveSingleItem();

        cookie.Name.Value.ShouldBe("__Host-halsa-paciente");
        cookie.HttpOnly.ShouldBeTrue();
        cookie.Secure.ShouldBeTrue();
        cookie.SameSite.ShouldBe(Microsoft.Net.Http.Headers.SameSiteMode.Strict);
        cookie.Path.Value.ShouldBe("/");
        cookie.Domain.HasValue.ShouldBeFalse();
        cookie.Expires.ShouldBeNull();   // A session cookie: the Redis entry carries the expiry.

        string valor = cookie.Value.Value!;
        Jwt().IsMatch(valor).ShouldBeFalse(valor);
        valor.ShouldNotContain("eyJ");
        valor.ShouldNotContain("firma-del");
    }

    // ── The proxy ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task WithASession_ShouldForwardToV1Paciente_WithTheBearer_AndWithoutCookiesNorCsrf()
    {
        HttpClient cliente = _factory.Cliente();
        string cookie = await SesionAsync(cliente);

        HttpRequestMessage request = Request(HttpMethod.Get, "/paciente/api/casos/abc/linea-tiempo?x=1", cookie);
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer token-del-navegador");
        HttpResponseMessage response = await cliente.SendAsync(request);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        response.Headers.Contains("Set-Cookie").ShouldBeFalse();

        Recibido recibido = _factory.Api.Recibidos.ShouldHaveSingleItem();
        recibido.Uri.AbsolutePath.ShouldBe("/v1/paciente/casos/abc/linea-tiempo");
        recibido.Uri.Query.ShouldBe("?x=1");
        recibido.Uri.Host.ShouldBe("api.test");
        recibido.Authorization.ShouldBe($"Bearer {BffFactory.AccessToken}");
        recibido.ConCookie.ShouldBeFalse();
        recibido.ConCsrf.ShouldBeFalse();
    }

    [Fact]
    public async Task AnAccessTokenAboutToExpire_ShouldBeRefreshedInTheServer_BeforeForwarding()
    {
        HttpClient cliente = _factory.Cliente();
        string cookie = await SesionAsync(cliente, expiraEnSegundos: 30);

        HttpResponseMessage response = await cliente.SendAsync(Request(HttpMethod.Get, "/paciente/api/mis-casos", cookie));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Recibido refresco = _factory.Keycloak.Recibidos.ShouldHaveSingleItem();
        refresco.Uri.ToString().ShouldBe(BffFactory.KeycloakToken);
        refresco.Contenido!.ShouldContain("grant_type=refresh_token");
        refresco.Contenido.ShouldContain("client_id=halsa-bff-paciente");
        _factory.Api.Recibidos.ShouldHaveSingleItem().Authorization.ShouldBe("Bearer nuevo-access-token");

        // No token reaches the browser, not even the refreshed one.
        (await response.Content.ReadAsStringAsync()).ShouldNotContain("nuevo-access-token");
        foreach (string valor in response.Headers.TryGetValues("Set-Cookie", out IEnumerable<string>? valores) ? valores : [])
        {
            valor.ShouldNotContain("nuevo-access-token");
        }
    }

    [Fact]
    public async Task ASessionOlderThanTwelveHours_ShouldBeOver_EvenWithActivity()
    {
        HttpClient cliente = _factory.Cliente();
        string cookie = await SesionAsync(cliente, iniciadaHaceHoras: 13);

        (await cliente.SendAsync(Request(HttpMethod.Get, "/paciente/api/mis-casos", cookie)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        _factory.Api.Recibidos.ShouldBeEmpty();
    }

    // ── bff/* ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Usuario_ShouldAnswerTheNameTheEmailAndVinculado_AndNoToken()
    {
        HttpClient cliente = _factory.Cliente();
        string cookie = await SesionAsync(cliente);

        HttpResponseMessage response = await cliente.SendAsync(Request(HttpMethod.Get, "/paciente/bff/usuario", cookie, csrf: false));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string json = await response.Content.ReadAsStringAsync();
        JsonObject usuario = JsonNode.Parse(json)!.AsObject();
        usuario["nombre"]!.GetValue<string>().ShouldBe("Paciente Dev");
        usuario["correo"]!.GetValue<string>().ShouldBe("paciente.dev@halsa.dev");
        usuario["vinculado"]!.GetValue<bool>().ShouldBeTrue();
        json.ShouldNotContain("eyJ");

        _factory.Api.Recibidos.ShouldHaveSingleItem().Uri.AbsolutePath.ShouldBe("/v1/paciente/mis-casos");
    }

    [Fact]
    public async Task Logout_ShouldEndTheKeycloakSessionFromTheServer_AndTheLocalOne()
    {
        HttpClient cliente = _factory.Cliente();
        string cookie = await SesionAsync(cliente);

        HttpResponseMessage response = await cliente.SendAsync(Request(HttpMethod.Post, "/paciente/bff/logout", cookie));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        Recibido logout = _factory.Keycloak.Recibidos.ShouldHaveSingleItem();
        logout.Uri.ToString().ShouldBe(BffFactory.KeycloakLogout);
        logout.Contenido!.ShouldContain("refresh_token=");
        logout.Contenido.ShouldContain("client_secret=");

        // The Redis entry is gone: the old cookie no longer opens anything.
        (await cliente.SendAsync(Request(HttpMethod.Get, "/paciente/api/mis-casos", cookie)))
            .StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Theory]
    [InlineData("https://evil.example.com/")]
    [InlineData("//evil.example.com/")]
    [InlineData("/\\evil.example.com/")]
    public async Task Login_WithAnAbsoluteReturnUrl_ShouldAnswer400(string returnUrl)
    {
        (await _factory.Cliente().GetAsync($"/paciente/bff/login?returnUrl={Uri.EscapeDataString(returnUrl)}"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Login_ShouldRedirectToKeycloak_WithCodeAndPkce()
    {
        HttpResponseMessage response = await _factory.Cliente().GetAsync("/paciente/bff/login?returnUrl=%2Fmis-casos");

        response.StatusCode.ShouldBe(HttpStatusCode.Redirect);
        string destino = response.Headers.Location!.ToString();
        destino.ShouldStartWith(BffFactory.KeycloakAuth);
        destino.ShouldContain("client_id=halsa-bff-paciente");
        destino.ShouldContain("response_type=code");
        destino.ShouldContain("code_challenge_method=S256");
        destino.ShouldContain("scope=openid");
        destino.ShouldContain(Uri.EscapeDataString("https://localhost/paciente/bff/signin-oidc"));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static async Task<string> SesionAsync(HttpClient cliente, int expiraEnSegundos = 3600, int iniciadaHaceHoras = 0)
    {
        HttpResponseMessage response = await cliente.PostAsync(
            $"/pruebas/sesion?expiraEnSegundos={expiraEnSegundos}&iniciadaHaceHoras={iniciadaHaceHoras}", content: null);
        SetCookieHeaderValue cookie = SetCookieHeaderValue.ParseList([.. response.Headers.GetValues("Set-Cookie")]).Single();
        return $"{cookie.Name}={cookie.Value}";
    }

    private static HttpRequestMessage Request(HttpMethod metodo, string ruta, string? cookie, bool csrf = true, string valorCsrf = "1")
    {
        var request = new HttpRequestMessage(metodo, ruta);
        if (cookie is not null)
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookie);
        }

        if (csrf || valorCsrf != "1")
        {
            request.Headers.TryAddWithoutValidation(Bff.CsrfHeader, valorCsrf);
        }

        return request;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+$")]
    private static partial Regex Jwt();
}

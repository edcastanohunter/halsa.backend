using System.Net;
using Halsa.Backend.FunctionalTests.Abstractions;

namespace Halsa.Backend.FunctionalTests.Identidad;

[Collection(nameof(ApiCollection))]
public sealed class CorsTests(ApiFactory factory)
{
    private const string Backoffice = "https://halsa-backoffice.alcast.dev";

    private static HttpRequestMessage Preflight(string origen) =>
        new(HttpMethod.Options, "v1/identidad/yo")
        {
            Headers =
            {
                { "Origin", origen },
                { "Access-Control-Request-Method", "GET" },
                { "Access-Control-Request-Headers", "authorization" }
            }
        };

    [Fact]
    public async Task Preflight_ShouldBeAllowed_ForTheBackofficeOrigin_WithoutToken()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.SendAsync(Preflight(Backoffice));

        response.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([Backoffice]);
        response.Headers.Contains("Access-Control-Allow-Credentials").ShouldBeFalse();
    }

    [Fact]
    public async Task Preflight_ShouldNotBeAllowed_ForAnotherOrigin()
    {
        HttpClient client = factory.CreateClient();

        HttpResponseMessage response = await client.SendAsync(Preflight("https://otro.example.com"));

        response.Headers.Contains("Access-Control-Allow-Origin").ShouldBeFalse();
    }

    [Fact]
    public async Task Response_ShouldExposeWwwAuthenticate_ToTheBackoffice()
    {
        HttpClient client = factory.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Get, "v1/identidad/yo") { Headers = { { "Origin", Backoffice } } };

        HttpResponseMessage response = await client.SendAsync(request);

        response.Headers.GetValues("Access-Control-Allow-Origin").ShouldBe([Backoffice]);
        response.Headers.GetValues("Access-Control-Expose-Headers").Single().ShouldContain("WWW-Authenticate");
    }
}

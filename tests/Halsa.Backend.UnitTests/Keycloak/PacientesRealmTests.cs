using System.Text.Json.Nodes;

namespace Halsa.Backend.UnitTests.Keycloak;

/// <summary>
/// SPEC 12, step 6: the <c>halsa-pacientes</c> realm has the confidential client of the BFF and self-registration, and
/// the <c>halsa</c> realm gives the portal's two permissions to the roles the spec names.
/// </summary>
public sealed class PacientesRealmTests
{
    private static JsonObject Realm(string archivo) => JsonNode.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docker", "keycloak", archivo))))!.AsObject();

    [Fact]
    public void TheBffClient_ShouldBeConfidential_CodeWithPkce_ToTheBffCallback_WithTheApiAudience()
    {
        JsonObject realm = Realm("halsa-pacientes-realm.json");

        JsonObject client = realm["clients"]!.AsArray()
            .Single(c => c!["clientId"]!.GetValue<string>() == "halsa-bff-paciente")!.AsObject();

        client["publicClient"]!.GetValue<bool>().ShouldBeFalse();
        client["standardFlowEnabled"]!.GetValue<bool>().ShouldBeTrue();
        client["implicitFlowEnabled"]!.GetValue<bool>().ShouldBeFalse();
        client["directAccessGrantsEnabled"]!.GetValue<bool>().ShouldBeFalse();
        client["redirectUris"]!.AsArray().Select(u => u!.GetValue<string>())
            .ShouldBe(["https://localhost:5610/paciente/bff/signin-oidc"]);
        client["attributes"]!["pkce.code.challenge.method"]!.GetValue<string>().ShouldBe("S256");
        client["protocolMappers"]!.AsArray().ShouldContain(m =>
            m!["protocolMapper"]!.GetValue<string>() == "oidc-audience-mapper" &&
            m["config"]!["included.client.audience"]!.GetValue<string>() == "halsa-api" &&
            m["config"]!["access.token.claim"]!.GetValue<string>() == "true");
    }

    [Fact]
    public void TheRealm_ShouldAllowSelfRegistration()
    {
        JsonObject realm = Realm("halsa-pacientes-realm.json");

        realm["registrationAllowed"]!.GetValue<bool>().ShouldBeTrue();
        // Development has no SMTP: the production realm turns email verification on.
        realm["verifyEmail"]!.GetValue<bool>().ShouldBeFalse();
    }

    [Theory]
    [InlineData("analista-arl", "pacientes:activar")]
    [InlineData("integracion-arl", "pacientes:activar")]
    [InlineData("oficial-privacidad", "solicitudes-hc:gestionar")]
    public void ThePortalPermissions_ShouldBeInTheRolesOfTheSpec(string rol, string permiso)
    {
        JsonObject realm = Realm("halsa-realm.json");
        JsonArray roles = realm["roles"]!["client"]!["halsa-api"]!.AsArray();

        roles.ShouldContain(r => r!["name"]!.GetValue<string>() == permiso);
        roles.Single(r => r!["name"]!.GetValue<string>() == rol)!["composites"]!["client"]!["halsa-api"]!.AsArray()
            .Select(p => p!.GetValue<string>())
            .ShouldContain(permiso);
    }
}

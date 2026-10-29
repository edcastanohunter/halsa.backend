using System.Text.Json.Nodes;

namespace Halsa.Backend.UnitTests.Keycloak;

/// <summary>
/// SPEC 13: los realms de producción (<c>deploy/keycloak/*.prod.json</c>) no traen nada de desarrollo: ni usuarios, ni
/// <c>halsa-dev-cli</c>, ni clients de integración precargados (se dan de alta por el runbook 06), ni secretos en claro.
/// </summary>
public sealed class ProdRealmsTests
{
    public static TheoryData<string> Realms => ["halsa.prod.json", "halsa-pacientes.prod.json"];

    [Theory]
    [MemberData(nameof(Realms))]
    public void ProdRealm_ShouldHaveNoUsers(string archivo) =>
        Cargar(archivo)["users"]!.AsArray().ShouldBeEmpty();

    [Theory]
    [MemberData(nameof(Realms))]
    public void ProdRealm_ShouldHaveNeitherTheDevCliNorIntegrationClients(string archivo) =>
        ClientIds(Cargar(archivo)).ShouldAllBe(id => id != "halsa-dev-cli" && !id.StartsWith("int-", StringComparison.Ordinal));

    [Theory]
    [MemberData(nameof(Realms))]
    public void ProdRealm_ShouldRequireTlsAndProtectAgainstBruteForce(string archivo)
    {
        JsonObject realm = Cargar(archivo);

        realm["sslRequired"]!.GetValue<string>().ShouldBe("all");
        realm["bruteForceProtected"]!.GetValue<bool>().ShouldBeTrue();
    }

    [Theory]
    [MemberData(nameof(Realms))]
    public void ProdRealm_ShouldNotPointToLocalhost(string archivo) =>
        Cargar(archivo).ToJsonString().ShouldNotContain("localhost");

    [Fact]
    public void TheBffSecret_ShouldComeFromTheEnvironment()
    {
        JsonObject bff = Cargar("halsa-pacientes.prod.json")["clients"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(c => c["clientId"]!.GetValue<string>() == "halsa-bff-paciente");

        bff["secret"]!.GetValue<string>().ShouldBe("${HALSA_KC_BFF_SECRET}");
        bff["redirectUris"]!.AsArray().Select(u => u!.GetValue<string>())
            .ShouldBe(["https://${HALSA_HOST_PACIENTES}/paciente/bff/signin-oidc"]);
    }

    [Fact]
    public void ThePatientsRealm_ShouldVerifyEmail_WithTheSmtpLeftToTheSharedKeycloak()
    {
        JsonObject realm = Cargar("halsa-pacientes.prod.json");

        realm["verifyEmail"]!.GetValue<bool>().ShouldBeTrue();
        // SPEC 13 (amendment): the SMTP is configured later in the shared Keycloak's realm, not shipped from here.
        realm.ContainsKey("smtpServer").ShouldBeFalse();
    }

    [Fact]
    public void ThePersonalRealm_ShouldIssueSubAndAuthTime()
    {
        JsonObject realm = Cargar("halsa.prod.json");

        JsonObject basic = realm["clientScopes"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(s => s["name"]!.GetValue<string>() == "basic");
        basic["protocolMappers"]!.AsArray().Select(m => m!["protocolMapper"]!.GetValue<string>())
            .ShouldBe(["oidc-sub-mapper", "oidc-usersessionmodel-note-mapper"], ignoreOrder: true);

        realm["defaultDefaultClientScopes"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldContain("basic");
    }

    [Fact]
    public void TheAdministradorRole_ShouldHaveNeitherClinicalNorIntegrationPermissions()
    {
        JsonObject rol = Cargar("halsa.prod.json")["roles"]!["client"]!["halsa-api"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(r => r["name"]!.GetValue<string>() == "administrador");

        string[] permisos = [.. rol["composites"]!["client"]!["halsa-api"]!.AsArray().Select(n => n!.GetValue<string>())];

        permisos.ShouldContain("auditoria:read");
        permisos.ShouldContain("casos:read");
        permisos.Where(p => p.StartsWith("clinico:", StringComparison.Ordinal) ||
                            p.StartsWith("pcl:", StringComparison.Ordinal) ||
                            p.StartsWith("integracion:", StringComparison.Ordinal))
            .ShouldBeEmpty();
    }

    [Fact]
    public void TheBackofficeClient_ShouldRequirePkce()
    {
        JsonObject backoffice = Cargar("halsa.prod.json")["clients"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(c => c["clientId"]!.GetValue<string>() == "halsa-backoffice");

        backoffice["attributes"]!["pkce.code.challenge.method"]!.GetValue<string>().ShouldBe("S256");
    }

    /// <summary>
    /// importar-realms.sh fills only these with envsubst; ${client_id} is Keycloak's own mapper syntax and must stay
    /// literal. Anything else would reach the shared Keycloak unrendered.
    /// </summary>
    [Theory]
    [MemberData(nameof(Realms))]
    public void ProdRealm_ShouldOnlyUseThePlaceholdersTheImportFills(string archivo)
    {
        string[] permitidos = ["HALSA_HOST_PACIENTES", "HALSA_BACKOFFICE_URL", "HALSA_KC_BFF_SECRET", "client_id"];

        System.Text.RegularExpressions.Regex.Matches(Cargar(archivo).ToJsonString(), @"\$\{([^}]+)\}")
            .Select(m => m.Groups[1].Value)
            .ShouldAllBe(nombre => permitidos.Contains(nombre));
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static JsonObject Cargar(string archivo) => JsonNode.Parse(File.ReadAllText(Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..", "deploy", "keycloak", archivo))))!.AsObject();

    private static IEnumerable<string> ClientIds(JsonObject realm) =>
        realm["clients"]!.AsArray().Select(c => c!["clientId"]!.GetValue<string>());
}

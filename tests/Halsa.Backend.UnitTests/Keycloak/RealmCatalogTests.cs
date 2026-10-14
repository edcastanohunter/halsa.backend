using System.Text.Json;
using System.Text.Json.Nodes;

namespace Halsa.Backend.UnitTests.Keycloak;

/// <summary>
/// Protege el secreto profesional (Ley 23/1981, art. 37) verificando que el rol compuesto
/// <c>analista-arl</c> no incluya permisos clínicos, de PCL ni de auditoría,
/// ni siquiera de forma transitiva dentro del mismo cliente.
/// </summary>
public sealed class RealmCatalogTests
{
    private static readonly string RealmJsonPath = Path.GetFullPath(
        Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", "..",   // hasta la raíz del repo
            "docker", "keycloak", "halsa-realm.json"));

    private static JsonObject LoadHalsaRealm()
    {
        string json = File.ReadAllText(RealmJsonPath);
        return JsonNode.Parse(json)!.AsObject();
    }

    /// <summary>
    /// Construye el conjunto completo de permisos atómicos que un rol posee,
    /// expandiendo composites en el mismo cliente de forma recursiva.
    /// </summary>
    private static HashSet<string> ExpandRolePermissions(
        string roleName,
        Dictionary<string, JsonObject> clientRolesByName,
        HashSet<string>? visited = null)
    {
        visited ??= [];

        if (!visited.Add(roleName))
            return [];

        if (!clientRolesByName.TryGetValue(roleName, out JsonObject? role))
            return [];

        var permissions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool isComposite = role["composite"]?.GetValue<bool>() ?? false;

        if (!isComposite)
        {
            // Permiso atómico: el nombre IS el permiso
            permissions.Add(roleName);
            return permissions;
        }

        // Rol compuesto: expandir sus hijos
        JsonNode? composites = role["composites"];
        if (composites is null)
        {
            permissions.Add(roleName);
            return permissions;
        }

        JsonNode? clientComposites = composites["client"]?["halsa-api"];
        if (clientComposites is JsonArray childArray)
        {
            foreach (JsonNode? child in childArray)
            {
                string? childName = child?.GetValue<string>();
                if (childName is null) continue;

                permissions.UnionWith(
                    ExpandRolePermissions(childName, clientRolesByName, visited));
            }
        }

        return permissions;
    }

    [Fact]
    public void AnalistaArl_ShouldNotHaveClinico_PclOrAuditoria_Permissions()
    {
        JsonObject realm = LoadHalsaRealm();

        // Construir mapa de roles del cliente halsa-api
        JsonArray? halsaApiRoles = realm["roles"]?["client"]?["halsa-api"]?.AsArray();
        halsaApiRoles.ShouldNotBeNull("El realm debe tener roles para el cliente halsa-api");

        Dictionary<string, JsonObject> clientRoles = halsaApiRoles
            .Select(n => n!.AsObject())
            .ToDictionary(o => o["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);

        clientRoles.ShouldContainKey("analista-arl",
            "El rol compuesto analista-arl debe existir en halsa-api");

        HashSet<string> permissions = ExpandRolePermissions("analista-arl", clientRoles);

        string[] forbidden = [.. permissions
            .Where(p =>
                p.StartsWith("clinico:", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("pcl:", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("auditoria:", StringComparison.OrdinalIgnoreCase))];

        forbidden.ShouldBeEmpty(
            $"analista-arl no debe tener permisos clínicos, de PCL ni de auditoría. " +
            $"Encontrados: {string.Join(", ", forbidden)}");
    }

    [Fact]
    public void MedicoIps_ShouldHaveCasosRead_ButNotPclOrAuditoria()
    {
        JsonObject realm = LoadHalsaRealm();

        JsonArray? halsaApiRoles = realm["roles"]?["client"]?["halsa-api"]?.AsArray();
        halsaApiRoles.ShouldNotBeNull();

        Dictionary<string, JsonObject> clientRoles = halsaApiRoles
            .Select(n => n!.AsObject())
            .ToDictionary(o => o["name"]!.GetValue<string>(), StringComparer.OrdinalIgnoreCase);

        HashSet<string> permissions = ExpandRolePermissions("medico-ips", clientRoles);

        permissions.ShouldContain("casos:read",
            "medico-ips necesita casos:read para listar los casos que le remitieron (SPEC 02)");
        permissions.ShouldContain("incapacidades:read",
            "medico-ips consulta las incapacidades de los casos que le remitieron (SPEC 07)");

        string[] forbidden = [.. permissions
            .Where(p =>
                p.StartsWith("pcl:", StringComparison.OrdinalIgnoreCase) ||
                p.StartsWith("auditoria:", StringComparison.OrdinalIgnoreCase))];

        forbidden.ShouldBeEmpty(
            $"medico-ips no debe tener permisos de PCL ni de auditoría. " +
            $"Encontrados: {string.Join(", ", forbidden)}");
    }

    [Theory]
    [InlineData("rethus")]
    [InlineData("tipo_documento")]
    [InlineData("numero_documento")]
    public void HalsaClaims_ShouldMapTheProfessionalAttribute_AsAFlatAccessTokenClaim(string atributo)
    {
        // SPEC 06: IProfesionalActual reads these claims; without the mapper every backoffice write answers 403.
        JsonObject realm = LoadHalsaRealm();

        JsonObject scope = realm["clientScopes"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(s => s["name"]!.GetValue<string>() == "halsa-claims");

        JsonObject config = scope["protocolMappers"]!.AsArray()
            .Select(n => n!.AsObject())
            .Where(m => m["protocolMapper"]!.GetValue<string>() == "oidc-usermodel-attribute-mapper")
            .Select(m => m["config"]!.AsObject())
            .Single(c => c["user.attribute"]!.GetValue<string>() == atributo);

        config["claim.name"]!.GetValue<string>().ShouldBe(atributo);
        config["access.token.claim"]!.GetValue<string>().ShouldBe("true");
        config["jsonType.label"]!.GetValue<string>().ShouldBe("String");
    }

    [Theory]
    [InlineData("calificador.arla", "CC", "52000111", "RM-DEV-0001")]
    [InlineData("medico.ips", "CC", "80000222", "RM-DEV-0002")]
    public void SeedDoctors_ShouldCarryTheirProfessionalIdentity(
        string username, string tipoDocumento, string numeroDocumento, string rethus)
    {
        JsonObject realm = LoadHalsaRealm();

        JsonObject attributes = realm["users"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(u => u["username"]!.GetValue<string>() == username)["attributes"]!.AsObject();

        attributes["tipo_documento"]!.AsArray().Single()!.GetValue<string>().ShouldBe(tipoDocumento);
        attributes["numero_documento"]!.AsArray().Single()!.GetValue<string>().ShouldBe(numeroDocumento);
        attributes["rethus"]!.AsArray().Single()!.GetValue<string>().ShouldBe(rethus);
    }

    // ── Step-up (SPEC 07) ─────────────────────────────────────────────────────

    [Fact]
    public void BrowserFlow_ShouldMapAcr1ToThePassword_AndAcr2ToTheOtp()
    {
        JsonObject realm = LoadHalsaRealm();

        realm["attributes"]!["acr.loa.map"]!.GetValue<string>().ShouldBe("{\"1\":1,\"2\":2}");

        string browserFlow = realm["browserFlow"]!.GetValue<string>();
        JsonObject[] flows = [.. realm["authenticationFlows"]!.AsArray().Select(n => n!.AsObject())];
        flows.ShouldContain(f => f["alias"]!.GetValue<string>() == browserFlow && f["topLevel"]!.GetValue<bool>());

        Dictionary<string, string> niveles = realm["authenticatorConfig"]!.AsArray()
            .Select(n => n!["config"]!.AsObject())
            .ToDictionary(c => c["loa-condition-level"]!.GetValue<string>(), c => c["loa-max-age"]!.GetValue<string>());
        niveles.Keys.ShouldBe(["1", "2"], ignoreOrder: true);

        // Each level's sub-flow is the condition plus one authenticator: the password at 1, the OTP at 2.
        string[] autenticadores = [.. flows
            .SelectMany(f => f["authenticationExecutions"]!.AsArray())
            .Select(e => e!["authenticator"]?.GetValue<string>())
            .Where(a => a is not null && a != "conditional-level-of-authentication")
            .Select(a => a!)];
        autenticadores.ShouldBe(["auth-username-password-form", "auth-otp-form"], ignoreOrder: true);
    }

    [Fact]
    public void TheTokens_ShouldCarryTheAcrClaim()
    {
        JsonObject realm = LoadHalsaRealm();

        // The realm declares its own client scopes, so Keycloak does not create its built-in "acr" scope on import.
        JsonObject acr = realm["clientScopes"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(s => s["name"]!.GetValue<string>() == "acr");
        JsonObject mapper = acr["protocolMappers"]!.AsArray().Single()!.AsObject();
        mapper["protocolMapper"]!.GetValue<string>().ShouldBe("oidc-acr-mapper");
        mapper["config"]!["access.token.claim"]!.GetValue<string>().ShouldBe("true");

        realm["defaultDefaultClientScopes"]!.AsArray().Select(n => n!.GetValue<string>()).ShouldContain("acr");
    }

    // ── Import (docker compose down -v && up) ─────────────────────────────────

    private static string KeycloakDir => Path.GetDirectoryName(RealmJsonPath)!;

    [Fact]
    public void EveryImportedFile_ShouldBeNamedAfterItsRealm()
    {
        // Keycloak refuses to start when a file's name does not match the realm inside it.
        string[] archivos = Directory.GetFiles(KeycloakDir, "*.json");

        archivos.ShouldNotBeEmpty();
        foreach (string archivo in archivos)
        {
            string realm = JsonNode.Parse(File.ReadAllText(archivo))!["realm"]!.GetValue<string>();
            Path.GetFileName(archivo).ShouldBe($"{realm}-realm.json");
        }
    }

    [Fact]
    public void EveryDefaultScope_ShouldBeDeclared()
    {
        JsonObject realm = LoadHalsaRealm();

        // With its own clientScopes, Keycloak creates none of its built-in scopes but offline_access, and silently
        // drops a default that names a missing one.
        HashSet<string> declarados = [.. realm["clientScopes"]!.AsArray().Select(s => s!["name"]!.GetValue<string>()), "offline_access"];

        string[] defaults = [.. realm["defaultDefaultClientScopes"]!.AsArray()
            .Concat(realm["defaultOptionalClientScopes"]!.AsArray())
            .Select(n => n!.GetValue<string>())];

        defaults.Where(d => !declarados.Contains(d)).ShouldBeEmpty();
        defaults.ShouldContain("roles");
        defaults.ShouldContain("profile");
    }

    [Fact]
    public void TheRolesScope_ShouldIssueTheClientRoles_TheApiReadsAsPermissions()
    {
        JsonObject realm = LoadHalsaRealm();

        JsonObject roles = realm["clientScopes"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(s => s["name"]!.GetValue<string>() == "roles");

        JsonObject[] mappers = [.. roles["protocolMappers"]!.AsArray().Select(m => m!.AsObject())];
        JsonObject clientRoles = mappers.Single(m => m["protocolMapper"]!.GetValue<string>() == "oidc-usermodel-client-role-mapper");
        clientRoles["config"]!["claim.name"]!.GetValue<string>().ShouldBe("resource_access.${client_id}.roles");
        clientRoles["config"]!["access.token.claim"]!.GetValue<string>().ShouldBe("true");
    }

    [Theory]
    [InlineData("int-arl-ARL_A", "integracion-arl")]
    [InlineData("int-ips-110011234501", "integracion-ips")]
    public void EveryIntegrationClient_ShouldHaveItsRoleOnItsServiceAccount(string clientId, string rol)
    {
        JsonObject realm = LoadHalsaRealm();

        // A client-credentials token carries only the roles of the client's service-account user.
        JsonObject cuenta = realm["users"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(u => u["serviceAccountClientId"]?.GetValue<string>() == clientId);

        cuenta["clientRoles"]!["halsa-api"]!.AsArray().Select(r => r!.GetValue<string>()).ShouldBe([rol]);
    }

    [Fact]
    public void ClientScopeMappings_ShouldUseTheImportFormat()
    {
        JsonObject realm = LoadHalsaRealm();

        // { "<client that owns the roles>": [ { "client": "<client that receives them>", "roles": [...] } ] }
        foreach ((string _, JsonNode? mapeos) in realm["clientScopeMappings"]!.AsObject())
        {
            foreach (JsonNode? mapeo in mapeos.ShouldBeOfType<JsonArray>())
            {
                mapeo!["client"]!.GetValue<string>().ShouldNotBeNullOrWhiteSpace();
                mapeo["roles"].ShouldBeOfType<JsonArray>().ShouldNotBeEmpty();
            }
        }
    }

    [Theory]
    [InlineData("calificador.arla", true)]
    [InlineData("medico.ips", true)]
    [InlineData("analista.arla", false)]
    public void OnlyTheSeedDoctors_ShouldHaveAnOtpCredential(string username, bool tieneOtp)
    {
        JsonObject realm = LoadHalsaRealm();

        JsonObject usuario = realm["users"]!.AsArray()
            .Select(n => n!.AsObject())
            .Single(u => u["username"]!.GetValue<string>() == username);

        usuario["credentials"]!.AsArray()
            .Any(c => c!["type"]!.GetValue<string>() == "otp")
            .ShouldBe(tieneOtp);
    }

    [Fact]
    public void ClinicoEmergencia_ShouldNotBelongToAnyCompositeRole()
    {
        JsonObject realm = LoadHalsaRealm();

        JsonArray? halsaApiRoles = realm["roles"]?["client"]?["halsa-api"]?.AsArray();
        halsaApiRoles.ShouldNotBeNull();

        // clinico:emergencia no debe aparecer en los composites de ningún rol
        List<string> compositesThatIncludeEmergencia = [];

        foreach (JsonNode? roleNode in halsaApiRoles)
        {
            JsonObject role = roleNode!.AsObject();
            string roleName = role["name"]!.GetValue<string>();

            bool isComposite = role["composite"]?.GetValue<bool>() ?? false;
            if (!isComposite) continue;

            JsonNode? clientComposites = role["composites"]?["client"]?["halsa-api"];
            if (clientComposites is not JsonArray childArray) continue;

            bool includesEmergencia = childArray
                .Any(n => n?.GetValue<string>() == "clinico:emergencia");

            if (includesEmergencia)
                compositesThatIncludeEmergencia.Add(roleName);
        }

        compositesThatIncludeEmergencia.ShouldBeEmpty(
            "clinico:emergencia debe otorgarse individualmente, nunca a través de un rol compuesto. " +
            $"Roles que lo incluyen: {string.Join(", ", compositesThatIncludeEmergencia)}");
    }
}

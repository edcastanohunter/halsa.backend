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

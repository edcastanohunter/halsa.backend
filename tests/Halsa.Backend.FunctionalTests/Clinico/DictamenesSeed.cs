using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>Bodies, actors and SQL helpers of the SPEC 08 dictamen tests.</summary>
internal static class DictamenesSeed
{
    private static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    public static string RutaDictamen(Guid caso) => $"v1/casos/{caso}/dictamen-pcl";

    public static string RutaNotificar(Guid caso) => $"v1/casos/{caso}/dictamen-pcl/notificar";

    public static DateOnly Hoy => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(ZonaColombia).DateTime);

    public static string Fecha(DateOnly fecha) => fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary><c>calificador.arla</c> with MFA that many minutes ago and its name claim.</summary>
    public static HttpClient Calificador(ApiFactory factory, int autenticadoHaceMinutos = 5)
    {
        Dictionary<string, object> claims = ConStepUp(CalificadorArlA(), "2", autenticadoHaceMinutos);
        claims["name"] = "Calificador ARL-A";
        return Client(factory, claims);
    }

    public const string Fundamentos =
        "Secuelas de fractura del quinto dedo de la mano derecha con limitación funcional, valoradas según el manual vigente.";

    /// <summary>The spec's example: 32,50 % = 20,00 + 12,50, dated yesterday.</summary>
    public static JsonObject Dictamen(
        decimal porcentaje = 32.50m,
        decimal? deficiencias = 20.00m,
        decimal? rol = 12.50m,
        Guid? corrigeA = null,
        string fundamentos = Fundamentos) => new()
    {
        ["porcentajePcl"] = porcentaje,
        ["origen"] = "LABORAL",
        ["fechaEstructuracion"] = Fecha(Hoy.AddDays(-30)),
        ["fechaDictamen"] = Fecha(Hoy.AddDays(-1)),
        ["componentes"] = deficiencias is null ? null : new JsonObject { ["deficiencias"] = deficiencias, ["rolLaboralYOtrasAreas"] = rol },
        ["fundamentos"] = fundamentos,
        ["diagnosticos"] = new JsonArray(new JsonObject { ["sistema"] = "CIE-10", ["codigo"] = "S62.6" }),
        ["corrigeA"] = corrigeA?.ToString()
    };

    /// <summary>A case of ARL_A with <c>calificador.arla</c> assigned.</summary>
    public static async Task<Guid> CasoAsignadoAsync(ApiFactory factory)
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);
        return caso;
    }

    public static async Task<JsonObject> RegistradoAsync(ApiFactory factory, Guid caso, JsonObject? cuerpo = null)
    {
        HttpResponseMessage response = await Calificador(factory).PostAsJsonAsync(RutaDictamen(caso), cuerpo ?? Dictamen(), JsonOptions);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await BodyAsync(response);
    }

    public static async Task<T?> EscalarAsync<T>(ApiFactory factory, string sql, Guid id)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);
        object? valor = await cmd.ExecuteScalarAsync();
        return valor is null or DBNull ? default : (T)valor;
    }
}

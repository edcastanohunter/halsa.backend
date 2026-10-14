using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>
/// SPEC 08, step 5: <c>POST /v1/casos/{casoId}/dictamen-pcl</c>, behind <c>pcl:dictamen:write</c>, a recent step-up
/// and the assignment. Each test seeds its own case, with <c>calificador.arla</c> assigned unless it says otherwise.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class RegistrarDictamenPclEndpointTests(ApiFactory factory)
{
    private static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    private static string Ruta(Guid caso) => $"v1/casos/{caso}/dictamen-pcl";

    private static DateOnly Hoy => DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(ZonaColombia).DateTime);

    private static string Fecha(DateOnly fecha) => fecha.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary><c>calificador.arla</c> with MFA 5 minutes ago and its name claim.</summary>
    private HttpClient Calificador(int autenticadoHaceMinutos = 5)
    {
        Dictionary<string, object> claims = ConStepUp(CalificadorArlA(), "2", autenticadoHaceMinutos);
        claims["name"] = "Calificador ARL-A";
        return Client(factory, claims);
    }

    /// <summary>The spec's example: 32,50 % = 20,00 + 12,50.</summary>
    private static JsonObject Dictamen(
        decimal porcentaje = 32.50m,
        decimal? deficiencias = 20.00m,
        decimal? rol = 12.50m,
        Guid? corrigeA = null,
        string fundamentos = "Secuelas de fractura del quinto dedo de la mano derecha con limitación funcional, valoradas según el manual vigente.") => new()
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

    private async Task<Guid> CasoAsignadoAsync()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);
        return caso;
    }

    private async Task<JsonObject> RegistradoAsync(Guid caso, JsonObject cuerpo)
    {
        HttpResponseMessage response = await Calificador().PostAsJsonAsync(Ruta(caso), cuerpo, JsonOptions);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await BodyAsync(response);
    }

    private async Task<T?> EscalarAsync<T>(string sql, Guid id)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("id", id);
        object? valor = await cmd.ExecuteScalarAsync();
        return valor is null or DBNull ? default : (T)valor;
    }

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADictamenWhoseComponentsAdd_ShouldAnswer201_AndCalificarTheCase()
    {
        Guid caso = await CasoAsignadoAsync();

        HttpResponseMessage response = await Calificador().PostAsJsonAsync(Ruta(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        response.Headers.Location!.ToString().ShouldBe($"/v1/casos/{caso}/dictamen-pcl");
        JsonObject body = await BodyAsync(response);
        Guid id = body["id"]!.GetValue<Guid>();
        body["porcentajePcl"]!.GetValue<decimal>().ShouldBe(32.50m);
        body["origen"]!.GetValue<string>().ShouldBe("LABORAL");
        body["manual"]!.GetValue<string>().ShouldBe("Decreto 1507/2014");
        body["componentes"]!["deficiencias"]!.GetValue<decimal>().ShouldBe(20.00m);
        body["estadoNotificacion"]!.GetValue<string>().ShouldBe("PENDIENTE");
        body["calificador"]!["registroRethus"]!.GetValue<string>().ShouldBe("RM-DEV-0001");
        body["calificador"]!["nombreCompleto"]!.GetValue<string>().ShouldBe("Calificador ARL-A");
        body["calificador"]!["identificacion"]!["numero"]!.GetValue<string>().ShouldBe("52000111");
        body["venceControversiaEn"].ShouldBeNull();

        // The case: EnCalificacionPcl, with the administrative result the ARL settles with.
        (await EscalarAsync<string>("SELECT estado FROM atel.casos_medicos WHERE id = @id", caso)).ShouldBe("EnCalificacionPcl");
        (await EscalarAsync<decimal>("SELECT calificacion_pcl FROM atel.casos_medicos WHERE id = @id", caso)).ShouldBe(32.50m);
        (await EscalarAsync<string>("SELECT calificacion_origen FROM atel.casos_medicos WHERE id = @id", caso)).ShouldBe("LABORAL");

        // The clinical part is a DICTAMEN_PCL record, encrypted; the audit says CREAR on it.
        Guid registro = (await EscalarAsync<Guid>("SELECT registro_clinico_id FROM atel.dictamenes_pcl WHERE id = @id", id));
        RegistroFila fila = (await RegistrosAsync(factory, caso)).ShouldHaveSingleItem();
        fila.Id.ShouldBe(registro);
        fila.Tipo.ShouldBe("DictamenPcl");
        fila.ProfesionalSub.ShouldBe(CalificadorArlASub);
        (await AuditoriaAsync(factory, caso)).ShouldContain(
            new AuditoriaFila("CREAR", "REGISTRO_CLINICO", registro.ToString(), "EXITO", ArlA, "halsa-backoffice"));
    }

    [Fact]
    public async Task TheCaseResponse_ShouldShowThePercentage_ToTheAnalyst_ButNeverTheFundamentos()
    {
        Guid caso = await CasoAsignadoAsync();
        await RegistradoAsync(caso, Dictamen());

        HttpResponseMessage response = await Client(factory, AnalistaArlA()).GetAsync($"v1/casos/{caso}");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        string cuerpo = await response.Content.ReadAsStringAsync();
        cuerpo.ShouldContain("32.5");
        cuerpo.ShouldNotContain("fundamentos", Case.Insensitive);
        cuerpo.ShouldNotContain("Secuelas");
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComponentsThatDoNotAdd_ShouldAnswer400()
    {
        Guid caso = await CasoAsignadoAsync();

        HttpResponseMessage response = await Calificador().PostAsJsonAsync(Ruta(caso), Dictamen(rol: 10.00m), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Dictamen.ComponentesNoSuman");
        (await RegistrosAsync(factory, caso)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(100.01, "Dictamen.PorcentajeFueraDeRango")]
    [InlineData(32.505, "Dictamen.PorcentajeFueraDeRango")]
    public async Task AnInvalidPercentage_ShouldAnswer400(double porcentaje, string codigo)
    {
        Guid caso = await CasoAsignadoAsync();

        HttpResponseMessage response = await Calificador()
            .PostAsJsonAsync(Ruta(caso), Dictamen((decimal)porcentaje, deficiencias: null), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(codigo);
    }

    [Fact]
    public async Task ShortFundamentos_AFutureDictamen_AndNoDiagnosis_ShouldAnswer400()
    {
        Guid caso = await CasoAsignadoAsync();
        JsonObject cuerpo = Dictamen(fundamentos: "Muy corto");
        cuerpo["fechaDictamen"] = Fecha(Hoy.AddDays(1));
        cuerpo["diagnosticos"] = new JsonArray();

        HttpResponseMessage response = await Calificador().PostAsJsonAsync(Ruta(caso), cuerpo, JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        string problema = await response.Content.ReadAsStringAsync();
        problema.ShouldContain("Dictamen.FundamentosLongitud");
        problema.ShouldContain("Dictamen.FechasInvalidas");
        problema.ShouldContain("Dictamen.DiagnosticoRequerido");
    }

    // ── One live dictamen ─────────────────────────────────────────────────────

    [Fact]
    public async Task ASecondPost_WithoutCorrigeA_ShouldAnswer409YaExiste()
    {
        Guid caso = await CasoAsignadoAsync();
        await RegistradoAsync(caso, Dictamen());

        HttpResponseMessage response = await Calificador().PostAsJsonAsync(Ruta(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("Dictamen.YaExiste");
    }

    [Fact]
    public async Task ASecondPost_WithCorrigeA_WhilePendiente_ShouldAnswer201_AndMarkThePrevious()
    {
        Guid caso = await CasoAsignadoAsync();
        Guid anterior = (await RegistradoAsync(caso, Dictamen()))["id"]!.GetValue<Guid>();

        JsonObject nuevo = await RegistradoAsync(caso, Dictamen(40.00m, 25.00m, 15.00m, corrigeA: anterior));

        Guid id = nuevo["id"]!.GetValue<Guid>();
        nuevo["corrigeA"]!.GetValue<Guid>().ShouldBe(anterior);
        (await EscalarAsync<Guid>("SELECT corregido_por FROM atel.dictamenes_pcl WHERE id = @id", anterior)).ShouldBe(id);
        (await EscalarAsync<decimal>("SELECT calificacion_pcl FROM atel.casos_medicos WHERE id = @id", caso)).ShouldBe(40.00m);

        // The clinical record of the correction points to the original's, and the audit says CORREGIR.
        List<RegistroFila> registros = await RegistrosAsync(factory, caso);
        registros.Count.ShouldBe(2);
        registros[1].CorrigeA.ShouldBe(registros[0].Id);
        (await AuditoriaAsync(factory, caso)).ShouldContain(
            new AuditoriaFila("CORREGIR", "REGISTRO_CLINICO", registros[1].Id.ToString(), "EXITO", ArlA, "halsa-backoffice"));
    }

    [Fact]
    public async Task CorrigeA_OfANonLiveDictamen_ShouldAnswer409_AndWithoutAnyDictamen404()
    {
        Guid caso = await CasoAsignadoAsync();

        HttpResponseMessage sinNinguno = await Calificador()
            .PostAsJsonAsync(Ruta(caso), Dictamen(corrigeA: Guid.CreateVersion7()), JsonOptions);
        Guid primero = (await RegistradoAsync(caso, Dictamen()))["id"]!.GetValue<Guid>();
        await RegistradoAsync(caso, Dictamen(corrigeA: primero));
        HttpResponseMessage alViejo = await Calificador()
            .PostAsJsonAsync(Ruta(caso), Dictamen(corrigeA: primero), JsonOptions);

        sinNinguno.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(sinNinguno)).ShouldBe("Dictamen.NoEncontrado");
        alViejo.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(alViejo)).ShouldBe("Dictamen.CorrigeANoVigente");
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WithAnAuthTimeOf20MinutesAgo_ShouldAnswer401_WithMaxAge900()
    {
        Guid caso = await CasoAsignadoAsync();

        HttpResponseMessage response = await Calificador(autenticadoHaceMinutos: 20)
            .PostAsJsonAsync(Ruta(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("max_age=\"900\"");
    }

    [Fact]
    public async Task CalificadorNotAssigned_ShouldAnswer403NoAsignado_AndWriteNothing()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await Calificador().PostAsJsonAsync(Ruta(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.NoAsignado");
        (await EscalarAsync<long>("SELECT count(*) FROM atel.dictamenes_pcl WHERE caso_medico_id = @id", caso)).ShouldBe(0);
    }

    [Fact]
    public async Task AnalistaArl_ShouldAnswer403()
    {
        Guid caso = await CasoAsignadoAsync();

        HttpResponseMessage response = await Client(factory, ConStepUp(AnalistaArlA(), "2", 1))
            .PostAsJsonAsync(Ruta(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

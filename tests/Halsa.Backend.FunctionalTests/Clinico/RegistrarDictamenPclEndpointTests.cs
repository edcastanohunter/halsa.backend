using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Clinico.DictamenesSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>
/// SPEC 08, step 5: <c>POST /v1/casos/{casoId}/dictamen-pcl</c>, behind <c>pcl:dictamen:write</c>, a recent step-up
/// and the assignment. Each test seeds its own case, with <c>calificador.arla</c> assigned unless it says otherwise.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class RegistrarDictamenPclEndpointTests(ApiFactory factory)
{
    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task ADictamenWhoseComponentsAdd_ShouldAnswer201_AndCalificarTheCase()
    {
        Guid caso = await CasoAsignadoAsync(factory);

        HttpResponseMessage response = await Calificador(factory).PostAsJsonAsync(RutaDictamen(caso), Dictamen(), JsonOptions);

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
        (await EscalarAsync<string>(factory, "SELECT estado FROM atel.casos_medicos WHERE id = @id", caso)).ShouldBe("EnCalificacionPcl");
        (await EscalarAsync<decimal>(factory, "SELECT calificacion_pcl FROM atel.casos_medicos WHERE id = @id", caso)).ShouldBe(32.50m);
        (await EscalarAsync<string>(factory, "SELECT calificacion_origen FROM atel.casos_medicos WHERE id = @id", caso)).ShouldBe("LABORAL");

        // The clinical part is a DICTAMEN_PCL record, encrypted; the audit says CREAR on it.
        Guid registro = (await EscalarAsync<Guid>(factory, "SELECT registro_clinico_id FROM atel.dictamenes_pcl WHERE id = @id", id));
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
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso, Dictamen());

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
        Guid caso = await CasoAsignadoAsync(factory);

        HttpResponseMessage response = await Calificador(factory).PostAsJsonAsync(RutaDictamen(caso), Dictamen(rol: 10.00m), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain("Dictamen.ComponentesNoSuman");
        (await RegistrosAsync(factory, caso)).ShouldBeEmpty();
    }

    [Theory]
    [InlineData(100.01, "Dictamen.PorcentajeFueraDeRango")]
    [InlineData(32.505, "Dictamen.PorcentajeFueraDeRango")]
    public async Task AnInvalidPercentage_ShouldAnswer400(double porcentaje, string codigo)
    {
        Guid caso = await CasoAsignadoAsync(factory);

        HttpResponseMessage response = await Calificador(factory)
            .PostAsJsonAsync(RutaDictamen(caso), Dictamen((decimal)porcentaje, deficiencias: null), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).ShouldContain(codigo);
    }

    [Fact]
    public async Task ShortFundamentos_AFutureDictamen_AndNoDiagnosis_ShouldAnswer400()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        JsonObject cuerpo = Dictamen(fundamentos: "Muy corto");
        cuerpo["fechaDictamen"] = Fecha(Hoy.AddDays(1));
        cuerpo["diagnosticos"] = new JsonArray();

        HttpResponseMessage response = await Calificador(factory).PostAsJsonAsync(RutaDictamen(caso), cuerpo, JsonOptions);

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
        Guid caso = await CasoAsignadoAsync(factory);
        await RegistradoAsync(factory, caso, Dictamen());

        HttpResponseMessage response = await Calificador(factory).PostAsJsonAsync(RutaDictamen(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("Dictamen.YaExiste");
    }

    [Fact]
    public async Task ASecondPost_WithCorrigeA_WhilePendiente_ShouldAnswer201_AndMarkThePrevious()
    {
        Guid caso = await CasoAsignadoAsync(factory);
        Guid anterior = (await RegistradoAsync(factory, caso, Dictamen()))["id"]!.GetValue<Guid>();

        JsonObject nuevo = await RegistradoAsync(factory, caso, Dictamen(40.00m, 25.00m, 15.00m, corrigeA: anterior));

        Guid id = nuevo["id"]!.GetValue<Guid>();
        nuevo["corrigeA"]!.GetValue<Guid>().ShouldBe(anterior);
        (await EscalarAsync<Guid>(factory, "SELECT corregido_por FROM atel.dictamenes_pcl WHERE id = @id", anterior)).ShouldBe(id);
        (await EscalarAsync<decimal>(factory, "SELECT calificacion_pcl FROM atel.casos_medicos WHERE id = @id", caso)).ShouldBe(40.00m);

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
        Guid caso = await CasoAsignadoAsync(factory);

        HttpResponseMessage sinNinguno = await Calificador(factory)
            .PostAsJsonAsync(RutaDictamen(caso), Dictamen(corrigeA: Guid.CreateVersion7()), JsonOptions);
        Guid primero = (await RegistradoAsync(factory, caso, Dictamen()))["id"]!.GetValue<Guid>();
        await RegistradoAsync(factory, caso, Dictamen(corrigeA: primero));
        HttpResponseMessage alViejo = await Calificador(factory)
            .PostAsJsonAsync(RutaDictamen(caso), Dictamen(corrigeA: primero), JsonOptions);

        sinNinguno.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(sinNinguno)).ShouldBe("Dictamen.NoEncontrado");
        alViejo.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(alViejo)).ShouldBe("Dictamen.CorrigeANoVigente");
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task WithAnAuthTimeOf20MinutesAgo_ShouldAnswer401_WithMaxAge900()
    {
        Guid caso = await CasoAsignadoAsync(factory);

        HttpResponseMessage response = await Calificador(factory, autenticadoHaceMinutos: 20)
            .PostAsJsonAsync(RutaDictamen(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.ToString().ShouldContain("max_age=\"900\"");
    }

    [Fact]
    public async Task CalificadorNotAssigned_ShouldAnswer403NoAsignado_AndWriteNothing()
    {
        Guid caso = await SeedCasoAsync(factory, ArlA);

        HttpResponseMessage response = await Calificador(factory).PostAsJsonAsync(RutaDictamen(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.NoAsignado");
        (await EscalarAsync<long>(factory, "SELECT count(*) FROM atel.dictamenes_pcl WHERE caso_medico_id = @id", caso)).ShouldBe(0);
    }

    [Fact]
    public async Task AnalistaArl_ShouldAnswer403()
    {
        Guid caso = await CasoAsignadoAsync(factory);

        HttpResponseMessage response = await Client(factory, ConStepUp(AnalistaArlA(), "2", 1))
            .PostAsJsonAsync(RutaDictamen(caso), Dictamen(), JsonOptions);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

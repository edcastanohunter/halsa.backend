using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Clinico;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;
using static Halsa.Backend.FunctionalTests.Pacientes.PacientesSeed;

namespace Halsa.Backend.FunctionalTests.Pacientes;

/// <summary>
/// SPEC 12, step 2: the ARL issues activation codes and the patient redeems them, with the limits per code and per
/// patient. Every test uses its own tenant, document and patient.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed partial class CodigosActivacionEndpointsTests(ApiFactory factory)
{
    // ── Emisión ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Emitir_ForAValidadoCase_ShouldAnswer201_AndStoreOnlyTheHashAndTheSalt()
    {
        string tenant = TenantNuevo("ARL_PAC_EMITIR");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());

        HttpResponseMessage response = await Analista(factory, tenant).PostAsync(RutaCodigos(caso), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonObject body = await BodyAsync(response);
        string codigo = body["codigo"]!.GetValue<string>();
        FormatoCodigo().IsMatch(codigo).ShouldBeTrue(codigo);
        body["expiraEn"]!.GetValue<DateTimeOffset>().ShouldBeInRange(
            DateTimeOffset.UtcNow.AddDays(30).AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(30).AddMinutes(5));

        string id = codigo[..6];
        string secreto = codigo[7..];
        JsonObject fila = await CodigoAsync(factory, id);
        fila["estado"]!.GetValue<string>().ShouldBe("ACTIVO");
        fila["caso_medico_id"]!.GetValue<Guid>().ShouldBe(caso);
        fila["hash_len"]!.GetValue<int>().ShouldBe(32);
        fila["sal_len"]!.GetValue<int>().ShouldBe(16);
        (await TodoElCodigoAsync(factory, id)).ShouldNotContain(secreto);

        (await AuditoriaAsync(factory, caso)).ShouldContain(
            new AuditoriaFila("CREAR", "CODIGO_ACTIVACION", id, "EXITO", tenant, "halsa-backoffice"));
    }

    [Theory]
    [InlineData("Radicado")]
    [InlineData("Rechazado")]
    public async Task Emitir_ForACaseNotYetValidado_ShouldAnswer409(string estado)
    {
        string tenant = TenantNuevo("ARL_PAC_RADICADO");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento(), estado);

        HttpResponseMessage response = await Analista(factory, tenant).PostAsync(RutaCodigos(caso), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("Paciente.CasoNoValidado");
    }

    [Fact]
    public async Task Emitir_Again_ShouldExpireThePreviousCode()
    {
        string tenant = TenantNuevo("ARL_PAC_REEMITIR");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());

        string primero = await EmitirAsync(factory, tenant, caso);
        string segundo = await EmitirAsync(factory, tenant, caso);

        (await CodigoAsync(factory, primero[..6]))["estado"]!.GetValue<string>().ShouldBe("EXPIRADO");
        (await CodigoAsync(factory, segundo[..6]))["estado"]!.GetValue<string>().ShouldBe("ACTIVO");

        HttpResponseMessage viejo = await CanjearAsync(Paciente(factory, NuevoSub()), primero);
        viejo.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(viejo)).ShouldBe("Paciente.CodigoInvalido");
    }

    [Fact]
    public async Task Emitir_ForACaseOfAnotherTenant_ShouldAnswer404()
    {
        Guid caso = await CasoDelTrabajadorAsync(factory, TenantNuevo("ARL_PAC_AJENO"), NuevoDocumento());

        (await Analista(factory, TenantNuevo("ARL_PAC_OTRO")).PostAsync(RutaCodigos(caso), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Emitir_WithoutPacientesActivar_ShouldAnswer403()
    {
        string tenant = TenantNuevo("ARL_PAC_SIN_PERMISO");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());

        (await Analista(factory, tenant, activa: false).PostAsync(RutaCodigos(caso), content: null))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    // ── Canje ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Canjear_ShouldAnswer201_WithTheMaskedDocument_AndAuditIt()
    {
        string tenant = TenantNuevo("ARL_PAC_CANJE");
        string documento = NuevoDocumento();
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, documento);
        string codigo = await EmitirAsync(factory, tenant, caso);
        string sub = NuevoSub();

        HttpResponseMessage response = await CanjearAsync(Paciente(factory, sub), codigo.ToLowerInvariant());

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        JsonObject body = await BodyAsync(response);
        JsonObject trabajador = body["trabajador"]!.AsObject();
        trabajador["tipo"]!.GetValue<string>().ShouldBe("CC");
        trabajador["numeroEnmascarado"]!.GetValue<string>().ShouldBe($"{new string('*', documento.Length - 4)}{documento[^4..]}");
        body.ToJsonString().ShouldNotContain(documento);

        JsonObject fila = await CodigoAsync(factory, codigo[..6]);
        fila["estado"]!.GetValue<string>().ShouldBe("USADO");
        fila["usado_por"]!.GetValue<string>().ShouldBe(sub);

        (await AuditoriaDeActorAsync(factory, sub, "VINCULO")).ShouldHaveSingleItem()
            .ShouldBe(new AuditoriaFila("CREAR", "VINCULO", codigo[..6], "EXITO", tenant, "halsa-bff-paciente"));
    }

    [Fact]
    public async Task Canjear_AUsedCode_ShouldAnswer400_WithTheSameErrorAsAnAbsentOne()
    {
        string tenant = TenantNuevo("ARL_PAC_USADO");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());
        string codigo = await EmitirAsync(factory, tenant, caso);
        (await CanjearAsync(Paciente(factory, NuevoSub()), codigo)).StatusCode.ShouldBe(HttpStatusCode.Created);

        HttpResponseMessage usado = await CanjearAsync(Paciente(factory, NuevoSub()), codigo);
        HttpResponseMessage inexistente = await CanjearAsync(Paciente(factory, NuevoSub()), "ZZZZZZ-ZZZZZZZZZZ");
        HttpResponseMessage malTecleado = await CanjearAsync(Paciente(factory, NuevoSub()), "no-es-un-codigo");

        foreach (HttpResponseMessage response in new[] { usado, inexistente, malTecleado })
        {
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            JsonObject body = await BodyAsync(response);
            body["errorCode"]!.GetValue<string>().ShouldBe("Paciente.CodigoInvalido");
            body["detail"]!.GetValue<string>().ShouldBe("El código de activación no es válido.");
        }
    }

    [Fact]
    public async Task FiveWrongSecrets_ShouldBlockTheCode_AndTheRightOneAfterwardsAnswers423()
    {
        string tenant = TenantNuevo("ARL_PAC_BLOQUEO");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());
        string codigo = await EmitirAsync(factory, tenant, caso);

        // Five patients, one wrong guess each: the per-patient limit never kicks in.
        for (int i = 0; i < 5; i++)
        {
            (await CanjearAsync(Paciente(factory, NuevoSub()), ConOtroSecreto(codigo)))
                .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        (await CodigoAsync(factory, codigo[..6]))["estado"]!.GetValue<string>().ShouldBe("BLOQUEADO");

        HttpResponseMessage response = await CanjearAsync(Paciente(factory, NuevoSub()), codigo);
        response.StatusCode.ShouldBe((HttpStatusCode)423);
        (await ErrorCodeAsync(response)).ShouldBe("Paciente.CodigoBloqueado");
    }

    [Fact]
    public async Task FiveFailuresOfAPatient_ShouldAnswer429ToTheSixth_EvenWithAValidCode()
    {
        string tenant = TenantNuevo("ARL_PAC_LIMITE");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());
        string codigo = await EmitirAsync(factory, tenant, caso);
        HttpClient paciente = Paciente(factory, NuevoSub());

        for (int i = 0; i < 5; i++)
        {
            (await CanjearAsync(paciente, $"ZZZZZ{i}-ZZZZZZZZZZ")).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        }

        HttpResponseMessage response = await CanjearAsync(paciente, codigo);
        response.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        (await ErrorCodeAsync(response)).ShouldBe("Paciente.DemasiadosIntentos");
        (await CodigoAsync(factory, codigo[..6]))["estado"]!.GetValue<string>().ShouldBe("ACTIVO");
    }

    [Fact]
    public async Task Canjear_ForAnotherDocument_ShouldAnswer409()
    {
        string tenant = TenantNuevo("ARL_PAC_DISTINTO");
        HttpClient paciente = await VinculadoAsync(factory, tenant, await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento()));
        string otro = await EmitirAsync(factory, tenant, await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento()));

        HttpResponseMessage response = await CanjearAsync(paciente, otro);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("Paciente.VinculoDistinto");
        (await CodigoAsync(factory, otro[..6]))["estado"]!.GetValue<string>().ShouldBe("ACTIVO");
    }

    [Fact]
    public async Task Canjear_WithAStaffToken_ShouldAnswer403()
    {
        string tenant = TenantNuevo("ARL_PAC_PERSONAL");
        Guid caso = await CasoDelTrabajadorAsync(factory, tenant, NuevoDocumento());
        string codigo = await EmitirAsync(factory, tenant, caso);

        (await Analista(factory, tenant).PostAsJsonAsync("v1/paciente/vinculos", new { codigo }, JsonOptions))
            .StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await CodigoAsync(factory, codigo[..6]))["estado"]!.GetValue<string>().ShouldBe("ACTIVO");
    }

    [GeneratedRegex("^[0-9A-HJKMNP-TV-Z]{6}-[0-9A-HJKMNP-TV-Z]{10}$")]
    private static partial Regex FormatoCodigo();
}

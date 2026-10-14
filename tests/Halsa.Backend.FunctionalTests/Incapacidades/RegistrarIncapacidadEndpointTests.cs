using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.FunctionalTests.Abstractions;
using Halsa.Backend.FunctionalTests.Clinico;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Incapacidades.IncapacidadesSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Incapacidades;

/// <summary>
/// SPEC 07, step 7: <c>POST /v1/casos/{casoId}/incapacidades</c> through the whole pipeline, with the fake antivirus
/// of <see cref="ApiFactory"/>. Each test seeds its own case.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class RegistrarIncapacidadEndpointTests(ApiFactory factory)
{
    private HttpClient Ips() => IntegracionIps(factory);

    private HttpClient MedicoIpsConAcr(string acr) => Client(factory, ConAcr(MedicoIps(), acr));

    private async Task<JsonObject> CargadaAsync(HttpClient client, Guid caso, JsonObject datos)
    {
        HttpResponseMessage response = await CargarAsync(client, caso, datos);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await BodyAsync(response);
    }

    private Task<long> FilasAsync(string tabla, Guid caso) =>
        ContarAsync(factory, $"SELECT count(*) FROM {tabla} WHERE caso_medico_id = @caso", ("caso", caso));

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task IpsIntegration_Inicial_ShouldAnswer201_WithTheServerComputedDates()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        DateOnly inicio = Hoy.AddDays(-20);

        HttpResponseMessage response = await CargarAsync(Ips(), caso, Datos(inicio, dias: 10));

        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        JsonObject body = await BodyAsync(response);
        Guid id = body["id"]!.GetValue<Guid>();
        body["casoId"]!.GetValue<Guid>().ShouldBe(caso);
        body["fechaInicio"]!.GetValue<string>().ShouldBe(inicio.ToString("yyyy-MM-dd"));
        body["fechaFin"]!.GetValue<string>().ShouldBe(inicio.AddDays(9).ToString("yyyy-MM-dd"));
        body["diasAcumulados"]!.GetValue<int>().ShouldBe(10);
        body["estado"]!.GetValue<string>().ShouldBe("REGISTRADA");
        response.Headers.Location!.ToString().ShouldBe($"/v1/casos/{caso}/incapacidades/{id}");

        // One event per successful upload.
        (await ContarAsync(factory, """
            SELECT count(*) FROM outbox.outbox_messages
            WHERE type LIKE '%IncapacidadRegistradaIntegrationEvent' AND content::text LIKE '%' || @id || '%'
            """, ("id", id.ToString()))).ShouldBe(1);
    }

    [Fact]
    public async Task TheStoredFile_ShouldBeEncrypted_AndItsSha256TheOriginals()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        JsonObject body = await CargadaAsync(Ips(), caso, Datos());

        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ruta, sha256_claro FROM clinico.soportes WHERE id = @id";
        cmd.Parameters.AddWithValue("id", body["soporteId"]!.GetValue<Guid>());
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        reader.GetFieldValue<byte[]>(1).ShouldBe(SHA256.HashData(Pdf));
        byte[] enDisco = await File.ReadAllBytesAsync(Path.Combine(factory.ArchivosRaiz, reader.GetString(0)));
        Encoding.ASCII.GetString(enDisco).ShouldNotStartWith("%PDF-");
        Encoding.ASCII.GetString(enDisco).ShouldNotContain(TextoDelPdf);
    }

    [Fact]
    public async Task TheResponse_AndTheAtelRow_ShouldNotCarryTheDiagnosisNorTheObservations()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        HttpResponseMessage response = await CargarAsync(Ips(), caso, Datos(codigo: "S62.6"));

        string cuerpo = await response.Content.ReadAsStringAsync();
        response.StatusCode.ShouldBe(HttpStatusCode.Created, cuerpo);
        cuerpo.ShouldNotContain("S62");
        cuerpo.ShouldNotContain("confidencial");
        cuerpo.ShouldNotContain("profesional");

        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT row_to_json(i)::text FROM atel.incapacidades i WHERE caso_medico_id = @caso";
        cmd.Parameters.AddWithValue("caso", caso);
        string fila = (string)(await cmd.ExecuteScalarAsync())!;
        fila.ShouldNotContain("S62");
        fila.ShouldNotContain("confidencial");
    }

    // ── Step-up ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task MedicoIps_WithAcr1_ShouldAnswer401_WithStepUp_AndAuditTheDenial()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        HttpResponseMessage response = await CargarAsync(MedicoIpsConAcr("1"), caso);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        string wwwAuthenticate = response.Headers.WwwAuthenticate.ToString();
        wwwAuthenticate.ShouldContain("error=\"insufficient_user_authentication\"");
        wwwAuthenticate.ShouldContain("acr_values=\"2\"");
        (await ErrorCodeAsync(response)).ShouldBe("Auth.StepUpRequerido");

        (await ContarAsync(factory, """
            SELECT count(*) FROM auditoria.accesos WHERE caso_medico_id = @caso
              AND resultado = 'DENEGADO' AND motivo = 'step-up' AND recurso_tipo = 'INCAPACIDAD'
            """, ("caso", caso))).ShouldBe(1);
        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(0);
    }

    [Fact]
    public async Task MedicoIps_WithAcr2_ShouldAnswer201_AndSignWithTheRethusOfTheClaims()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        await CargadaAsync(MedicoIpsConAcr("2"), caso, Datos(conProfesional: false));

        RegistroFila registro = (await RegistrosAsync(factory, caso)).ShouldHaveSingleItem();
        registro.Tipo.ShouldBe("CertificadoIncapacidad");
        registro.Rethus.ShouldBe("RM-DEV-0002");
        registro.ProfesionalSub.ShouldBe(MedicoIpsSub);
    }

    // ── Parts, sizes, signature ───────────────────────────────────────────────

    [Fact]
    public async Task ASupportThatIsNotAPdf_ShouldAnswer415()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        HttpResponseMessage response = await CargarAsync(Ips(), caso, soporte: Encoding.ASCII.GetBytes("GIF89a..."));

        response.StatusCode.ShouldBe(HttpStatusCode.UnsupportedMediaType);
        (await ErrorCodeAsync(response)).ShouldBe("Archivos.NoEsPdf");
    }

    [Fact]
    public async Task ASupportOf11MB_ShouldAnswer413()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        byte[] grande = new byte[11 * 1024 * 1024];
        Encoding.ASCII.GetBytes("%PDF-1.7\n").CopyTo(grande, 0);

        HttpResponseMessage response = await CargarAsync(Ips(), caso, soporte: grande);

        response.StatusCode.ShouldBe(HttpStatusCode.RequestEntityTooLarge);
        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(0);
    }

    [Fact]
    public async Task WithoutTheSupportPart_ShouldAnswer400()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        HttpResponseMessage response = await CargarAsync(Ips(), caso, sinSoporte: true);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe("Incapacidades.PartesInvalidas");
    }

    [Fact]
    public async Task AThirdPart_ShouldAnswer400()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        HttpResponseMessage response = await CargarAsync(Ips(), caso,
            partesExtra: cuerpo => cuerpo.Add(new StringContent("x"), "otra"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WithoutIdempotencyKey_ShouldAnswer400()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        HttpResponseMessage response = await CargarAsync(Ips(), caso, sinLlave: true);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).ShouldBe("Integracion.IdempotencyKeyInvalida");
    }

    [Fact]
    public async Task AnInvalidDatos_ShouldAnswer400_WithTheJsonPathOfTheField()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        JsonObject datos = Datos();
        datos["dias"] = 31;

        HttpResponseMessage response = await CargarAsync(Ips(), caso, datos);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await BodyAsync(response))["errors"]!["datos.dias"].ShouldNotBeNull();
    }

    // ── Antivirus ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnEicarSupport_ShouldAnswer422_AndLeaveNoFileNorRows()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        int antes = ArchivosEnDisco(factory, ArlA);
        factory.Antivirus.Resultado = ResultadoAntivirus.Infectado("Eicar-Test-Signature");

        try
        {
            HttpResponseMessage response = await CargarAsync(Ips(), caso);

            response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await ErrorCodeAsync(response)).ShouldBe("Archivos.SoporteInfectado");
        }
        finally
        {
            factory.Antivirus.Resultado = ResultadoAntivirus.Limpio;
        }

        ArchivosEnDisco(factory, ArlA).ShouldBe(antes);
        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(0);
        (await FilasAsync("clinico.soportes", caso)).ShouldBe(0);
        (await FilasAsync("clinico.historia_clinica_detalle", caso)).ShouldBe(0);
    }

    [Fact]
    public async Task WithTheAntivirusDown_ShouldAnswer503_AndHealthShouldReportItDegraded()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        factory.Antivirus.Resultado = ResultadoAntivirus.NoDisponible("timeout");

        try
        {
            HttpResponseMessage response = await CargarAsync(Ips(), caso);

            response.StatusCode.ShouldBe(HttpStatusCode.ServiceUnavailable);
            (await ErrorCodeAsync(response)).ShouldBe("Archivos.AntivirusNoDisponible");
        }
        finally
        {
            factory.Antivirus.Resultado = ResultadoAntivirus.Limpio;
        }

        // The health check pings the configured clamd, where nothing listens in the tests.
        var health = new HttpRequestMessage(HttpMethod.Get, "health");
        health.Headers.Add("X-Unauthenticated", "true");
        JsonObject estado = await BodyAsync(await factory.CreateClient().SendAsync(health));
        estado["entries"]!["antivirus"]!["status"]!.GetValue<string>().ShouldBe("Degraded");
    }

    // ── Rules ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnOverlappingIncapacidad_ShouldAnswer422()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        await CargadaAsync(Ips(), caso, Datos(Hoy.AddDays(-20), dias: 10));

        HttpResponseMessage response = await CargarAsync(Ips(), caso, Datos(Hoy.AddDays(-15), dias: 10));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(response)).ShouldBe("INCAPACIDAD_SOLAPADA");
    }

    [Fact]
    public async Task AProrrogaWithoutProrrogaDe_ShouldAnswer422()
    {
        Guid caso = await CasoRemitidoAsync(factory);

        HttpResponseMessage response = await CargarAsync(Ips(), caso, Datos(tipo: "PRORROGA"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await ErrorCodeAsync(response)).ShouldBe("PRORROGA_SIN_INICIAL");
    }

    [Theory]
    [InlineData(31, HttpStatusCode.UnprocessableEntity)]
    [InlineData(30, HttpStatusCode.Created)]
    public async Task AProrroga_StartingNDaysAfterTheEnd(int diasDespues, HttpStatusCode esperado)
    {
        Guid caso = await CasoRemitidoAsync(factory);
        DateOnly inicio = Hoy.AddDays(-100);
        Guid inicial = (await CargadaAsync(Ips(), caso, Datos(inicio, dias: 10)))["id"]!.GetValue<Guid>();
        DateOnly finInicial = inicio.AddDays(9);

        HttpResponseMessage response = await CargarAsync(Ips(), caso,
            Datos(finInicial.AddDays(diasDespues), dias: 5, tipo: "PRORROGA", prorrogaDe: inicial));

        response.StatusCode.ShouldBe(esperado);
        if (esperado != HttpStatusCode.Created)
        {
            (await ErrorCodeAsync(response)).ShouldBe("PRORROGA_DISCONTINUA");
        }
    }

    [Theory]
    [InlineData("S62.0", HttpStatusCode.Created)]
    [InlineData("M54.5", HttpStatusCode.UnprocessableEntity)]
    public async Task AProrroga_ShouldKeepTheCategoryOfItsInitial(string codigo, HttpStatusCode esperado)
    {
        Guid caso = await CasoRemitidoAsync(factory);
        DateOnly inicio = Hoy.AddDays(-30);
        Guid inicial = (await CargadaAsync(Ips(), caso, Datos(inicio, dias: 10, codigo: "S62.6")))["id"]!.GetValue<Guid>();

        HttpResponseMessage response = await CargarAsync(Ips(), caso,
            Datos(inicio.AddDays(10), dias: 5, tipo: "PRORROGA", prorrogaDe: inicial, codigo: codigo));

        response.StatusCode.ShouldBe(esperado);
        if (esperado != HttpStatusCode.Created)
        {
            (await ErrorCodeAsync(response)).ShouldBe("PRORROGA_DIAGNOSTICO_NO_RELACIONADO");
        }
    }

    [Fact]
    public async Task AChainReaching150And180Days_ShouldAlert()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        DateOnly inicio = Hoy.AddDays(-200);

        // 5 × 30 = 150 days, then a 6th link: 180.
        JsonObject? body = null;
        for (int i = 0; i < 6; i++)
        {
            HttpResponseMessage response = await CargarAsync(Ips(), caso, Datos(
                inicio.AddDays(30 * i), dias: 30, tipo: i == 0 ? "INICIAL" : "PRORROGA",
                prorrogaDe: body?["id"]!.GetValue<Guid>()));
            body = await BodyAsync(response);
            response.StatusCode.ShouldBe(HttpStatusCode.Created, body.ToJsonString());

            if (i == 4)
            {
                body["diasAcumulados"]!.GetValue<int>().ShouldBe(150);
                Alertas(body).ShouldContain("ACUMULADO_150_DIAS");
                Alertas(body).ShouldNotContain("ACUMULADO_180_DIAS");
            }
        }

        body!["diasAcumulados"]!.GetValue<int>().ShouldBe(180);
        Alertas(body).ShouldContain("ACUMULADO_150_DIAS");
        Alertas(body).ShouldContain("ACUMULADO_180_DIAS");
    }

    private static string[] Alertas(JsonObject body) =>
        [.. body["alertas"]!.AsArray().Select(a => a!["code"]!.GetValue<string>())];

    // ── Idempotency and uniqueness ────────────────────────────────────────────

    [Fact]
    public async Task TheSameKeyAndBody_ShouldAnswer201WithTheSameId_WithoutASecondFileOrRow()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        JsonObject datos = Datos();
        Guid llave = Guid.NewGuid();

        HttpResponseMessage primera = await CargarAsync(Ips(), caso, datos, llave: llave);
        int archivos = ArchivosEnDisco(factory, ArlA);
        HttpResponseMessage segunda = await CargarAsync(Ips(), caso, datos, llave: llave);

        primera.StatusCode.ShouldBe(HttpStatusCode.Created);
        segunda.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await BodyAsync(segunda))["id"]!.GetValue<Guid>().ShouldBe((await BodyAsync(primera))["id"]!.GetValue<Guid>());
        ArchivosEnDisco(factory, ArlA).ShouldBe(archivos);
        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(1);
        (await FilasAsync("clinico.soportes", caso)).ShouldBe(1);
    }

    [Fact]
    public async Task TheSameCertificate_OfTheSameIps_WithAnotherKey_ShouldAnswer409()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        string certificado = $"CERT-{Guid.NewGuid():N}"[..30];
        await CargadaAsync(Ips(), caso, Datos(Hoy.AddDays(-40), numeroCertificado: certificado));

        HttpResponseMessage response = await CargarAsync(Ips(), caso, Datos(Hoy.AddDays(-20), numeroCertificado: certificado));

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).ShouldBe("Incapacidades.CertificadoDuplicado");
    }

    // ── Isolation ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnArlBPerson_OnAnArlACase_ShouldAnswer404()
    {
        Guid caso = await CasoRemitidoAsync(factory, ArlA);
        Dictionary<string, object> medicoArlB = CalificadorArlA();
        medicoArlB["tenant_id"] = ArlB;

        HttpResponseMessage response = await CargarAsync(Client(factory, ConAcr(medicoArlB, "2")), caso);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AnArlIntegration_ShouldAnswer403()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        Dictionary<string, object> integracionArl = new()
        {
            ["sub"] = "01999000-0000-7000-8000-00000000c001",
            ["iss"] = TestAuthenticationHandler.DefaultIssuer,
            ["azp"] = "int-arl-ARL_A",
            ["actor_tipo"] = "integracion",
            ["tenant_id"] = ArlA,
            ["permissions"] = new[] { "incapacidades:write" }
        };

        HttpResponseMessage response = await CargarAsync(Client(factory, integracionArl), caso);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }
}

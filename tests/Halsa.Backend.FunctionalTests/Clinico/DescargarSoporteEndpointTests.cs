using System.Net;
using System.Text.Json.Nodes;
using Halsa.Backend.FunctionalTests.Abstractions;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Incapacidades.IncapacidadesSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Clinico;

/// <summary>
/// SPEC 08, step 4: <c>GET /v1/casos/{casoId}/incapacidades/{incapacidadId}/soporte</c>, the decrypted PDF, behind the
/// clinical policy and the SHA-256 check. Each test uploads its own incapacidad (SPEC 07) on its own case.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class DescargarSoporteEndpointTests(ApiFactory factory)
{
    private static string RutaSoporte(Guid caso, Guid incapacidad) => $"v1/casos/{caso}/incapacidades/{incapacidad}/soporte";

    private HttpClient CalificadorConMfa() => Client(factory, ConStepUp(CalificadorArlA(), "2"));

    /// <summary>A case of ARL_A remitted to the seed IPS, with one incapacidad uploaded by the IPS integration.</summary>
    private async Task<(Guid Caso, Guid Incapacidad, Guid Soporte, string Certificado)> IncapacidadCargadaAsync()
    {
        Guid caso = await CasoRemitidoAsync(factory);
        string certificado = $"CERT-{Guid.NewGuid():N}"[..20];

        HttpResponseMessage response = await CargarAsync(IntegracionIps(factory), caso, Datos(numeroCertificado: certificado));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        JsonObject body = await BodyAsync(response);

        return (caso, body["id"]!.GetValue<Guid>(), body["soporteId"]!.GetValue<Guid>(), certificado);
    }

    private async Task<string> RutaEnDiscoAsync(Guid soporte)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ruta FROM clinico.soportes WHERE id = @id";
        cmd.Parameters.AddWithValue("id", soporte);
        return Path.Combine(factory.ArchivosRaiz, (string)(await cmd.ExecuteScalarAsync())!);
    }

    private async Task<List<AuditoriaFila>> LecturasDeSoporteAsync(Guid caso) =>
        [.. (await AuditoriaAsync(factory, caso)).Where(f => f.Accion == "LEER" && f.Recurso == "SOPORTE")];

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task CalificadorAssigned_ShouldGetExactlyTheOriginalPdf_NotCached_AndLeaveALeerSoporteRow()
    {
        (Guid caso, Guid incapacidad, _, string certificado) = await IncapacidadCargadaAsync();
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(RutaSoporte(caso, incapacidad));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Pdf);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/pdf");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
        response.Content.Headers.ContentDisposition!.ToString()
            .ShouldBe($"attachment; filename=\"incapacidad-{certificado}.pdf\"");

        (await LecturasDeSoporteAsync(caso)).ShouldBe([
            new AuditoriaFila("LEER", "SOPORTE", incapacidad.ToString(), "EXITO", ArlA, "halsa-backoffice")]);
    }

    [Fact]
    public async Task MedicoIps_AssignedByItsIps_ShouldDownloadIt()
    {
        (Guid caso, Guid incapacidad, _, _) = await IncapacidadCargadaAsync();
        await AsignarAsync(factory, caso, ArlA, MedicoIpsSub, porIps: Ips);

        HttpResponseMessage response = await Client(factory, ConStepUp(MedicoIps(), "2")).GetAsync(RutaSoporte(caso, incapacidad));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.Content.ReadAsByteArrayAsync()).ShouldBe(Pdf);
    }

    // ── Integrity ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AFileWithOneAlteredByte_ShouldAnswer500Corrupto_WithoutThePdf()
    {
        (Guid caso, Guid incapacidad, Guid soporte, _) = await IncapacidadCargadaAsync();
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);
        string ruta = await RutaEnDiscoAsync(soporte);
        byte[] cifrado = await File.ReadAllBytesAsync(ruta);
        cifrado[cifrado.Length / 2] ^= 0x01;
        await File.WriteAllBytesAsync(ruta, cifrado);

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(RutaSoporte(caso, incapacidad));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await ErrorCodeAsync(response)).ShouldBe("Archivos.Corrupto");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(TextoDelPdf);
    }

    [Fact]
    public async Task AFileWhoseHashIsNotTheOriginals_ShouldAnswer500Corrupto()
    {
        (Guid caso, Guid incapacidad, Guid soporte, _) = await IncapacidadCargadaAsync();
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);

        // The file still decrypts; only the recorded hash disagrees. The table is immutable by trigger.
        await using (var conn = new NpgsqlConnection(factory.SuperuserConnectionString))
        {
            await conn.OpenAsync();
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = """
                SET session_replication_role = replica;
                UPDATE clinico.soportes SET sha256_claro = sha256('otro'::bytea) WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("id", soporte);
            (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(RutaSoporte(caso, incapacidad));

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await ErrorCodeAsync(response)).ShouldBe("Archivos.Corrupto");
        (await response.Content.ReadAsStringAsync()).ShouldNotContain(TextoDelPdf);
    }

    // ── Authorization ─────────────────────────────────────────────────────────

    [Fact]
    public async Task AnalistaArl_ShouldGet403_AndADenegadoRow()
    {
        (Guid caso, Guid incapacidad, _, _) = await IncapacidadCargadaAsync();

        HttpResponseMessage response = await Client(factory, ConStepUp(AnalistaArlA(), "2")).GetAsync(RutaSoporte(caso, incapacidad));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await LecturasDeSoporteAsync(caso)).ShouldHaveSingleItem().Resultado.ShouldBe("DENEGADO");
    }

    [Fact]
    public async Task CalificadorNotAssigned_ShouldGet403NoAsignado()
    {
        (Guid caso, Guid incapacidad, _, _) = await IncapacidadCargadaAsync();

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(RutaSoporte(caso, incapacidad));

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.NoAsignado");
        (await LecturasDeSoporteAsync(caso)).ShouldHaveSingleItem().Resultado.ShouldBe("DENEGADO");
    }

    [Fact]
    public async Task AnIncapacidadOfAnotherCase_ShouldAnswer404()
    {
        (_, Guid incapacidadAjena, _, _) = await IncapacidadCargadaAsync();
        Guid caso = await CasoRemitidoAsync(factory);
        await AsignarAsync(factory, caso, ArlA, CalificadorArlASub);

        HttpResponseMessage response = await CalificadorConMfa().GetAsync(RutaSoporte(caso, incapacidadAjena));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).ShouldBe("Clinico.SoporteNoEncontrado");
    }
}

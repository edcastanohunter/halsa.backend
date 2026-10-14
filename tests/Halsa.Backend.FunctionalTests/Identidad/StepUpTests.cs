using System.Net;
using Halsa.Backend.FunctionalTests.Abstractions;
using Npgsql;
using static Halsa.Backend.FunctionalTests.Clinico.ConceptosSeed;
using static Halsa.Backend.FunctionalTests.Integraciones.IngestaClientes;

namespace Halsa.Backend.FunctionalTests.Identidad;

/// <summary>
/// SPEC 07, step 1: the step-up policy through the whole pipeline, on <see cref="StepUpProbeEndpoint"/>. A person
/// without <c>acr=2</c> gets a <c>401</c> that tells the client what to ask Keycloak for, and an audited denial.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class StepUpTests(ApiFactory factory)
{
    private static Dictionary<string, object> ConAcr(Dictionary<string, object> actor, string acr)
    {
        actor["acr"] = acr;
        return actor;
    }

    [Fact]
    public async Task Person_WithAcr1_ShouldAnswer401_WithWwwAuthenticate_AndAuditTheDenial()
    {
        Guid caso = Guid.CreateVersion7();

        HttpResponseMessage response = await Client(factory, ConAcr(MedicoIps(), "1"))
            .PostAsync(StepUpProbeEndpoint.Ruta(caso), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        string wwwAuthenticate = response.Headers.WwwAuthenticate.ToString();
        wwwAuthenticate.ShouldContain("error=\"insufficient_user_authentication\"");
        wwwAuthenticate.ShouldContain("acr_values=\"2\"");
        (await ErrorCodeAsync(response)).ShouldBe("Auth.StepUpRequerido");

        (await DenegadosAsync(caso)).ShouldBe([("CREAR", "INCAPACIDAD", "DENEGADO", "step-up")]);
    }

    [Fact]
    public async Task Person_WithAcr2_ShouldPass()
    {
        HttpResponseMessage response = await Client(factory, ConAcr(MedicoIps(), "2"))
            .PostAsync(StepUpProbeEndpoint.Ruta(Guid.CreateVersion7()), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Person_WithoutThePermission_ShouldAnswer403_EvenWithAcr1()
    {
        Guid caso = Guid.CreateVersion7();

        // analista.arla has incapacidades:read only: raising the acr would not let it in.
        HttpResponseMessage response = await Client(factory, ConAcr(AnalistaArlA(), "1"))
            .PostAsync(StepUpProbeEndpoint.Ruta(caso), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        response.Headers.WwwAuthenticate.ShouldBeEmpty();
        (await DenegadosAsync(caso)).ShouldBe([("CREAR", "INCAPACIDAD", "DENEGADO", (string?)null)]);
    }

    [Fact]
    public async Task Integracion_WithoutAcr_ShouldPass()
    {
        HttpResponseMessage response = await IntegracionIps(factory)
            .PostAsync(StepUpProbeEndpoint.Ruta(Guid.CreateVersion7()), content: null);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    private async Task<List<(string Accion, string Recurso, string Resultado, string? Motivo)>> DenegadosAsync(Guid caso)
    {
        await using var conn = new NpgsqlConnection(factory.SuperuserConnectionString);
        await conn.OpenAsync();
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT accion, recurso_tipo, resultado, motivo FROM auditoria.accesos
            WHERE caso_medico_id = @caso ORDER BY ocurrido_en
            """;
        cmd.Parameters.AddWithValue("caso", caso);

        var filas = new List<(string, string, string, string?)>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            filas.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return filas;
    }
}

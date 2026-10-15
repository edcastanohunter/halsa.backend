using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Terminos.PlazosSql;

namespace Halsa.Backend.IntegrationTests.Terminos;

/// <summary>
/// SPEC 09, paso 3: el esquema <c>sst</c>, sus reglas semilla y las barreras de <c>halsa_app</c> y <c>halsa_worker</c>.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class SstTerminosTests(DatabaseFixture db)
{
    private static readonly DateTimeOffset Vence = new(2026, 10, 20, 23, 59, 59, Bogota);

    [Fact]
    public async Task SeedRules_ShouldBeTheSixOfArq_AndTheCopiaHcOfSpec12()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT codigo || ':' || evento_disparador || ':' || cantidad || unidad || ':' || alerta_cantidad || alerta_unidad
            FROM sst.reglas_termino ORDER BY codigo
            """;

        var reglas = new List<string>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            reglas.Add(reader.GetString(0));
        }

        reglas.ShouldBe(
        [
            "CONTROVERSIA_DICTAMEN:DictamenPclNotificado:10HABILES:3HABILES",
            "INVESTIGACION_AT:CasoRadicado:15CALENDARIO:3CALENDARIO",
            "REMISION_INVESTIGACION_GRAVE:CasoRadicado:15CALENDARIO:3CALENDARIO",
            "REPORTE_FURAT:CasoRadicado:2HABILES:1HABILES",
            "REPORTE_FUREP:CasoRadicado:2HABILES:1HABILES",
            "SOLICITUD_COPIA_HC:SolicitudCopiaHcCreada:10HABILES:3HABILES",
            "TOPE_IT_180:IncapacidadRegistrada:180CALENDARIO:30CALENDARIO"
        ]);
    }

    [Fact]
    public async Task HalsaApp_OfArlA_ShouldNotSeeThePlazosOfArlB()
    {
        const string arlA = "ARL_SST_RLS_A";
        const string arlB = "ARL_SST_RLS_B";
        Guid plazoA = await PlazoNuevoAsync(arlA);
        Guid plazoB = await PlazoNuevoAsync(arlB);

        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: arlA);

        (await ScalarAsync(conn, "SELECT count(*) FROM sst.plazos WHERE id = @id", ("id", plazoA))).ShouldBe(1);
        (await ScalarAsync(conn, "SELECT count(*) FROM sst.plazos WHERE id = @id", ("id", plazoB))).ShouldBe(0);
        (await ScalarAsync(conn, "SELECT count(*) FROM sst.plazos WHERE tenant_id = @t", ("t", arlB))).ShouldBe(0);
    }

    [Fact]
    public async Task HalsaApp_ShouldNotInsertAPlazoForAnotherTenant()
    {
        const string arlA = "ARL_SST_INS_A";
        const string arlB = "ARL_SST_INS_B";
        (Guid casoB, _) = await InsertCasoAsync(db, arlB, Vence.AddDays(-5), Vence.AddDays(-5));

        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: arlA);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO sst.plazos (id, tenant_id, caso_medico_id, regla_id, regla_codigo, radicado, inicia_en, vence_en,
                                    alerta_en, estado, extemporaneo)
            SELECT gen_random_uuid(), @t, @caso, id, codigo, 'X', now(), now(), now(), 'ABIERTO', false
            FROM sst.reglas_termino WHERE codigo = 'INVESTIGACION_AT'
            """;
        cmd.Parameters.AddWithValue("t", arlB);
        cmd.Parameters.AddWithValue("caso", casoB);

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task HalsaWorker_ShouldSeeThePlazosOfEveryTenant()
    {
        Guid plazoA = await PlazoNuevoAsync("ARL_SST_WRK_A");
        Guid plazoB = await PlazoNuevoAsync("ARL_SST_WRK_B");

        await using NpgsqlConnection conn = await OpenAsync(db.WorkerConnectionString);

        (await ScalarAsync(conn, "SELECT count(*) FROM sst.plazos WHERE id IN (@a, @b)", ("a", plazoA), ("b", plazoB)))
            .ShouldBe(2);
    }

    [Fact]
    public async Task HalsaWorker_ShouldNotInsertPlazos()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.WorkerConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sst.plazos (id) VALUES (gen_random_uuid())";

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Theory]
    [InlineData("SELECT count(*) FROM atel.casos_medicos")]
    [InlineData("SELECT count(*) FROM atel.incapacidades")]
    [InlineData("SELECT count(*) FROM clinico.historia_clinica_detalle")]
    [InlineData("SELECT count(*) FROM auditoria.accesos")]
    [InlineData("SELECT count(*) FROM integracion.mensajes_entrantes")]
    [InlineData("SELECT tenant_id FROM clinico.soportes")]
    public async Task HalsaWorker_ShouldBeDeniedOutsideItsTables(string sql)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.WorkerConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteScalarAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
        error.MessageText.ShouldContain("permission denied");
    }

    [Fact]
    public async Task HalsaWorker_ShouldReadOnlyTheIdAndPathOfTheSupports()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.WorkerConnectionString);

        (await ScalarAsync(conn, "SELECT count(*) FROM (SELECT id, ruta FROM clinico.soportes) s")).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task HalsaWorker_ShouldRunThePartitionsFunction()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.WorkerConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT auditoria.asegurar_particiones(3)";

        await cmd.ExecuteNonQueryAsync();
    }

    [Fact]
    public async Task HalsaApp_ShouldNotWriteTheRulesNorTheHolidays()
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: "ARL_SST_GLOBAL");
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO sst.festivos (fecha, descripcion, origen) VALUES (DATE '2099-01-02', 'x', 'MANUAL')";

        PostgresException error = await Should.ThrowAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task UniqueIndex_ShouldTreatANullReferenceAsEqual()
    {
        const string tenant = "ARL_SST_UNICO";
        (Guid caso, _) = await InsertCasoAsync(db, tenant, Vence.AddDays(-5), Vence.AddDays(-5));
        await InsertPlazoAsync(db, tenant, caso, "INVESTIGACION_AT", Vence, Vence.AddDays(-3));

        PostgresException error = await Should.ThrowAsync<PostgresException>(() =>
            InsertPlazoAsync(db, tenant, caso, "INVESTIGACION_AT", Vence, Vence.AddDays(-3)));
        error.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
    }

    private async Task<Guid> PlazoNuevoAsync(string tenant)
    {
        (Guid caso, _) = await InsertCasoAsync(db, tenant, Vence.AddDays(-5), Vence.AddDays(-5));
        return await InsertPlazoAsync(db, tenant, caso, "INVESTIGACION_AT", Vence, Vence.AddDays(-3));
    }
}

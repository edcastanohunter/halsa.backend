using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Dictamenes;

/// <summary>
/// SPEC 08, step 5: the grants and the RLS of <c>atel.dictamenes_pcl</c>, and the column-limited <c>UPDATE</c> of
/// <c>halsa_ingesta</c> on <c>atel.casos_medicos</c>. Raw connections, to prove the database enforces them on its own.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class DictamenesBarrierTests(DatabaseFixture db)
{
    private static async Task<int> ExecuteAsync(NpgsqlConnection conn, string sql, params (string Name, object Value)[] parameters)
    {
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteNonQueryAsync();
    }

    /// <summary>A live dictamen of the case, inserted as the owner (RLS forced, so with the tenant set).</summary>
    private async Task<Guid> InsertDictamenAsync(Guid casoId, string tenantId)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: tenantId);

        Guid id = Guid.CreateVersion7();
        await ExecuteAsync(conn, """
            INSERT INTO atel.dictamenes_pcl
              (id, tenant_id, caso_medico_id, registro_clinico_id, porcentaje_pcl, origen, fecha_estructuracion,
               fecha_dictamen, calificador_sub, calificador_registro_rethus, estado_notificacion)
            VALUES (@id, @tid, @caso, @registro, 32.50, 'LABORAL', current_date - 30, current_date - 1,
                    'sub-test', 'RM-12345', 'PENDIENTE')
            """, ("id", id), ("tid", tenantId), ("caso", casoId), ("registro", Guid.CreateVersion7()));

        return id;
    }

    // ── halsa_ingesta on casos_medicos ────────────────────────────────────────

    [Fact]
    public async Task Ingesta_ShouldUpdateTheStateAndTheCalificacion_OfItsTenantsCase()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        await using NpgsqlConnection conn = await OpenAsync(db.IngestaConnectionString, tenantId: ArlA);

        int filas = await ExecuteAsync(conn, """
            UPDATE atel.casos_medicos
            SET estado = 'EnCalificacionPcl', calificacion_pcl = 32.50,
                calificacion_fecha_estructuracion = current_date, calificacion_origen = 'LABORAL'
            WHERE id = @id
            """, ("id", caso));

        filas.ShouldBe(1);
    }

    [Fact]
    public async Task Ingesta_ShouldNotUpdateAnotherTenantsCase()
    {
        Guid casoB = await InsertCasoAsync(db, ArlB);
        await using NpgsqlConnection conn = await OpenAsync(db.IngestaConnectionString, tenantId: ArlA);

        (await ExecuteAsync(conn, "UPDATE atel.casos_medicos SET calificacion_pcl = 99 WHERE id = @id", ("id", casoB)))
            .ShouldBe(0);
    }

    [Theory]
    [InlineData("UPDATE atel.casos_medicos SET radicado = radicado")]
    [InlineData("UPDATE atel.casos_medicos SET tenant_id = tenant_id")]
    [InlineData("UPDATE atel.casos_medicos SET trabajador_numero = trabajador_numero")]
    [InlineData("UPDATE atel.dictamenes_pcl SET porcentaje_pcl = porcentaje_pcl")]
    [InlineData("UPDATE atel.dictamenes_pcl SET estado_notificacion = estado_notificacion")]
    [InlineData("DELETE FROM atel.dictamenes_pcl")]
    public async Task Ingesta_ShouldNotUpdateBeyondItsColumns(string sql)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.IngestaConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(conn, sql));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    // ── halsa_app on dictamenes_pcl ───────────────────────────────────────────

    [Fact]
    public async Task App_ShouldRecordTheNotification_OfItsTenantsDictamen()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid dictamen = await InsertDictamenAsync(caso, ArlA);
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        int filas = await ExecuteAsync(conn, """
            UPDATE atel.dictamenes_pcl
            SET estado_notificacion = 'NOTIFICADO', notificado_en = now(), medio_notificacion = 'PERSONAL'
            WHERE id = @id
            """, ("id", dictamen));

        filas.ShouldBe(1);
    }

    [Theory]
    [InlineData("UPDATE atel.dictamenes_pcl SET porcentaje_pcl = porcentaje_pcl")]
    [InlineData("UPDATE atel.dictamenes_pcl SET corregido_por = corregido_por")]
    [InlineData("DELETE FROM atel.dictamenes_pcl")]
    [InlineData("""
        INSERT INTO atel.dictamenes_pcl
          (id, tenant_id, caso_medico_id, registro_clinico_id, porcentaje_pcl, origen, fecha_estructuracion,
           fecha_dictamen, calificador_sub, calificador_registro_rethus, estado_notificacion)
        VALUES (gen_random_uuid(), 'ARL_A', gen_random_uuid(), gen_random_uuid(), 1, 'LABORAL', current_date,
                current_date, 's', 'r', 'PENDIENTE')
        """)]
    public async Task App_ShouldNotWriteTheDictamenBeyondItsNotification(string sql)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => ExecuteAsync(conn, sql));

        ex.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task App_ShouldNotSeeAnotherTenantsDictamen()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid dictamen = await InsertDictamenAsync(caso, ArlA);
        await using NpgsqlConnection conn = await OpenAsync(db.AppConnectionString, tenantId: ArlB);

        (await ScalarAsync(conn, "SELECT count(*) FROM atel.dictamenes_pcl WHERE id = @id", ("id", dictamen))).ShouldBe(0);
    }

    // ── One live dictamen ─────────────────────────────────────────────────────

    [Fact]
    public async Task TwoLiveDictamenesOfTheSameCase_ShouldViolateTheUniqueIndex()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        await InsertDictamenAsync(caso, ArlA);

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => InsertDictamenAsync(caso, ArlA));

        ex.SqlState.ShouldBe(PostgresErrorCodes.UniqueViolation);
        ex.ConstraintName.ShouldBe("ux_dictamenes_pcl_caso_vigente");
    }

    [Fact]
    public async Task CorregidoPor_ShouldBeCheckedAtCommit_SoTheOldOneCanStopBeingLiveFirst()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid anterior = await InsertDictamenAsync(caso, ArlA);
        Guid nuevo = Guid.CreateVersion7();

        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();

        // The new id does not exist yet: a non-deferred FK would refuse this UPDATE.
        await ExecuteAsync(conn, "UPDATE atel.dictamenes_pcl SET corregido_por = @nuevo WHERE id = @id", ("nuevo", nuevo), ("id", anterior));
        await ExecuteAsync(conn, """
            INSERT INTO atel.dictamenes_pcl
              (id, tenant_id, caso_medico_id, registro_clinico_id, porcentaje_pcl, origen, fecha_estructuracion,
               fecha_dictamen, calificador_sub, calificador_registro_rethus, estado_notificacion, corrige_a)
            VALUES (@id, 'ARL_A', @caso, gen_random_uuid(), 40, 'LABORAL', current_date - 30, current_date - 1,
                    'sub-test', 'RM-12345', 'PENDIENTE', @anterior)
            """, ("id", nuevo), ("caso", caso), ("anterior", anterior));
        await tx.CommitAsync();

        (await ScalarAsync(conn, "SELECT count(*) FROM atel.dictamenes_pcl WHERE caso_medico_id = @caso AND corregido_por IS NULL", ("caso", caso)))
            .ShouldBe(1);
    }

    [Fact]
    public async Task ACorregidoPorThatNeverArrives_ShouldFailAtCommit()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid anterior = await InsertDictamenAsync(caso, ArlA);

        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA);
        await using NpgsqlTransaction tx = await conn.BeginTransactionAsync();
        await ExecuteAsync(conn, "UPDATE atel.dictamenes_pcl SET corregido_por = gen_random_uuid() WHERE id = @id", ("id", anterior));

        PostgresException ex = await Should.ThrowAsync<PostgresException>(() => tx.CommitAsync());

        ex.SqlState.ShouldBe(PostgresErrorCodes.ForeignKeyViolation);
    }
}

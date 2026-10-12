using System.Globalization;
using Halsa.Backend.Infrastructure.Database;
using Halsa.Backend.IntegrationTests.Abstractions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Auditoria.AuditoriaSql;

namespace Halsa.Backend.IntegrationTests.Auditoria;

/// <summary>
/// SPEC 04, the table itself: nobody modifies it (not even its owner), <c>halsa_clinico</c> cannot read
/// it, and the monthly partitions plus <c>DEFAULT</c> always have somewhere to put a row.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AuditoriaTablaTests(DatabaseFixture db)
{
    public static TheoryData<string, string> RolesYSentencias()
    {
        var data = new TheoryData<string, string>();
        foreach (string rol in new[] { "halsa_app", "halsa_clinico", "halsa_owner" })
        {
            data.Add(rol, "UPDATE auditoria.accesos SET motivo = 'alterado' WHERE id = @id");
            data.Add(rol, "DELETE FROM auditoria.accesos WHERE id = @id");
            data.Add(rol, "TRUNCATE auditoria.accesos");
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(RolesYSentencias))]
    public async Task Modificar_ShouldFail_ForEveryRole(string rol, string sql)
    {
        Guid id = await InsertAsOwnerAsync(db, DateTimeOffset.UtcNow);

        // halsa_app and halsa_clinico lack the privilege; the owner hits the triggers.
        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => ExecuteAsync(ConnectionStringFor(rol), sql, id));

        error.SqlState.ShouldBeOneOf(PostgresErrorCodes.InsufficientPrivilege, PostgresErrorCodes.RaiseException);

        // The row is still there, untouched.
        (await ScalarAsSuperuserAsync(db, "SELECT motivo IS NULL FROM auditoria.accesos WHERE id = @id", id))
            .ShouldBe(true);
    }

    [Fact]
    public async Task Modificar_AsOwner_ShouldBeRejectedByTheTriggers_EvenWhenRlsHidesEveryRow()
    {
        Guid id = await InsertAsOwnerAsync(db, DateTimeOffset.UtcNow);

        // No app.tenant_id and no UPDATE policy: the statement sees zero rows, so only the statement trigger can refuse it.
        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => ExecuteAsync(db.OwnerConnectionString, "UPDATE auditoria.accesos SET motivo = 'x' WHERE id = @id", id));

        error.SqlState.ShouldBe(PostgresErrorCodes.RaiseException);
        error.MessageText.ShouldContain("inmutable");
    }

    [Fact]
    public async Task Select_AsClinico_ShouldBePermissionDenied()
    {
        PostgresException error = await Should.ThrowAsync<PostgresException>(
            () => ExecuteAsync(db.ClinicalConnectionString, "SELECT 1 FROM auditoria.accesos"));

        error.SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }

    [Fact]
    public async Task Partitions_ShouldExistForTheCurrentMonthAndTheNextThree()
    {
        DateTime mes = new(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] esperadas = [.. Enumerable.Range(0, 4)
            .Select(i => "accesos_" + mes.AddMonths(i).ToString("yyyy_MM", CultureInfo.InvariantCulture))];

        List<string> particiones = await ParticionesAsync();

        particiones.ShouldContain("accesos_default");
        foreach (string esperada in esperadas)
        {
            particiones.ShouldContain(esperada);
        }
    }

    [Fact]
    public async Task AsegurarParticiones_ShouldBeIdempotent()
    {
        List<string> antes = await ParticionesAsync();

        await DatabaseMigrator.AsegurarParticionesAsync(db.OwnerConnectionString);
        await DatabaseMigrator.AsegurarParticionesAsync(db.OwnerConnectionString);

        (await ParticionesAsync()).ShouldBe(antes, ignoreOrder: true);
    }

    [Fact]
    public async Task Insert_OutsideTheMonthlyPartitions_ShouldLandInDefault()
    {
        Guid id = await InsertAsOwnerAsync(db, new DateTimeOffset(2001, 1, 15, 12, 0, 0, TimeSpan.Zero));

        (await ScalarAsSuperuserAsync(db, "SELECT tableoid::regclass::text FROM auditoria.accesos WHERE id = @id", id))
            .ShouldBe("auditoria.accesos_default");
    }

    [Fact]
    public async Task Insert_InTheCurrentMonth_ShouldLandInItsPartition()
    {
        Guid id = await InsertAsOwnerAsync(db, DateTimeOffset.UtcNow);
        string esperada = "auditoria.accesos_" + DateTime.UtcNow.ToString("yyyy_MM", CultureInfo.InvariantCulture);

        (await ScalarAsSuperuserAsync(db, "SELECT tableoid::regclass::text FROM auditoria.accesos WHERE id = @id", id))
            .ShouldBe(esperada);
    }

    private string ConnectionStringFor(string rol) => rol switch
    {
        "halsa_app" => db.AppConnectionString,
        "halsa_clinico" => db.ClinicalConnectionString,
        "halsa_owner" => db.OwnerConnectionString,
        _ => throw new ArgumentOutOfRangeException(nameof(rol), rol, null)
    };

    private async Task<List<string>> ParticionesAsync()
    {
        await using var conn = new NpgsqlConnection(db.SuperuserConnectionString);
        await conn.OpenAsync();

        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT c.relname FROM pg_inherits i JOIN pg_class c ON c.oid = i.inhrelid
            WHERE i.inhparent = 'auditoria.accesos'::regclass
            """;

        var particiones = new List<string>();
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            particiones.Add(reader.GetString(0));
        }

        return particiones;
    }
}

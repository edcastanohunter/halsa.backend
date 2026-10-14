using Npgsql;

namespace Halsa.Backend.Infrastructure.Worker;

/// <summary>
/// La conexión <c>halsa_worker</c> (cadena <c>Worker</c>, SPEC 09): solo para los barridos entre tenants. Sus políticas
/// de RLS son <c>TO halsa_worker</c> y solo en las tablas que se barren; no tiene <c>USAGE</c> sobre <c>atel</c>. Los
/// handlers de eventos, que traen el tenant, siguen en <c>halsa_app</c> con <c>ISystemTenantScope</c>.
///
/// SQL directo, sin EF: los barridos son actualizaciones por lotes con <c>FOR UPDATE SKIP LOCKED</c>.
/// </summary>
internal sealed class BaseDatosWorker(string connectionString) : IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource = NpgsqlDataSource.Create(connectionString);

    public ValueTask<NpgsqlConnection> AbrirAsync(CancellationToken ct) => _dataSource.OpenConnectionAsync(ct);

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

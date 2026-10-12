using System.Net;
using Halsa.Backend.Infrastructure.Auditoria;
using Halsa.Backend.Infrastructure.Database.Auditoria;

namespace Halsa.Backend.UnitTests.Auditoria;

/// <summary>
/// <c>hash_registro</c> must be reproducible by anyone holding the row (SPEC 11 seals these hashes),
/// so the canonical form is pinned with a golden value computed outside .NET.
/// </summary>
public sealed class CanonicalAuditHasherTests
{
    // sha256 of the JCS form below, computed independently (Python json.dumps, sort_keys, compact).
    private const string GoldenHash = "76246eab99170281ffd99f6bc8816716b37c1a69cdcd391e415b5b6a98c25813";

    private const string GoldenCanonical =
        "{\"accion\":\"LEER\",\"acr\":\"1\",\"actor_id\":\"01999000-0000-7000-8000-000000000c01\"," +
        "\"actor_ips_id\":\"110011234501\",\"actor_roles\":[\"casos:read\",\"clinico:read\"]," +
        "\"actor_tenant_id\":null,\"actor_tipo\":\"ips\",\"caso_medico_id\":\"01999000-0000-7000-8000-0000000ca501\"," +
        "\"client_id\":\"halsa-web\",\"correlation_id\":\"corr-1\",\"id\":\"01999000-0000-7000-8000-0000000a0d01\"," +
        "\"ip\":\"10.0.0.7\",\"motivo\":null,\"ocurrido_en\":\"2026-10-12T15:30:45.123456Z\",\"recurso_id\":null," +
        "\"recurso_tipo\":\"REGISTRO_CLINICO\",\"resultado\":\"EXITO\",\"tenant_id\":\"ARL_A\"," +
        "\"user_agent\":\"Mozilla/5.0 \\\"Halsa\\\"\\t\u00f1\\u0001\"}";

    private static AccesoAuditado Golden() => new()
    {
        Id = Guid.Parse("01999000-0000-7000-8000-0000000a0d01"),
        OcurridoEn = new DateTimeOffset(2026, 10, 12, 10, 30, 45, TimeSpan.FromHours(-5)).AddTicks(1_234_560),
        TenantId = "ARL_A",
        ActorId = "01999000-0000-7000-8000-000000000c01",
        ActorTipo = "ips",
        ActorTenantId = null,
        ActorIpsId = "110011234501",
        ActorRoles = ["casos:read", "clinico:read"],
        ClientId = "halsa-web",
        Ip = IPAddress.Parse("10.0.0.7"),
        UserAgent = "Mozilla/5.0 \"Halsa\"\t\u00f1\u0001",
        CorrelationId = "corr-1",
        Accion = "LEER",
        Resultado = "EXITO",
        RecursoTipo = "REGISTRO_CLINICO",
        RecursoId = null,
        CasoMedicoId = Guid.Parse("01999000-0000-7000-8000-0000000ca501"),
        Motivo = null,
        Acr = "1"
    };

    [Fact]
    public void Canonicalizar_ShouldProduceTheGoldenForm()
    {
        CanonicalAuditHasher.Canonicalizar(CanonicalAuditHasher.Campos(Golden())).ShouldBe(GoldenCanonical);
    }

    [Fact]
    public void Hash_ShouldMatchTheGoldenValue()
    {
        Convert.ToHexStringLower(CanonicalAuditHasher.Hash(Golden())).ShouldBe(GoldenHash);
    }

    [Fact]
    public void CalcularHash_ShouldStoreTheSameHash()
    {
        AccesoAuditado acceso = Golden();

        acceso.CalcularHash();

        Convert.ToHexStringLower(acceso.HashRegistro).ShouldBe(GoldenHash);
    }

    [Fact]
    public void Hash_ShouldNotDependOnThePropertyOrder()
    {
        KeyValuePair<string, object?>[] campos = [.. CanonicalAuditHasher.Campos(Golden())];

        byte[] enOrden = CanonicalAuditHasher.Hash(campos);
        byte[] alReves = CanonicalAuditHasher.Hash(campos.Reverse());
        byte[] barajado = CanonicalAuditHasher.Hash(campos.OrderBy(c => c.Key.GetHashCode(StringComparison.Ordinal)));

        alReves.ShouldBe(enOrden);
        barajado.ShouldBe(enOrden);
    }

    [Fact]
    public void Hash_ShouldChange_WhenAnyColumnChanges()
    {
        AccesoAuditado original = Golden();
        AccesoAuditado alterado = Golden();
        typeof(AccesoAuditado).GetProperty(nameof(AccesoAuditado.Resultado))!.SetValue(alterado, "DENEGADO");

        CanonicalAuditHasher.Hash(alterado).ShouldNotBe(CanonicalAuditHasher.Hash(original));
    }

    [Fact]
    public void Canonicalizar_ShouldWriteIntegersAndEmptyArrays()
    {
        string json = CanonicalAuditHasher.Canonicalizar(new Dictionary<string, object?>
        {
            ["b"] = 42,
            ["a"] = Array.Empty<string>(),
            ["c"] = -7L
        });

        json.ShouldBe("{\"a\":[],\"b\":42,\"c\":-7}");
    }

    [Fact]
    public void Canonicalizar_ShouldSortKeysByUtf16CodeUnits()
    {
        // Ordinal, not culture: "_" (0x5F) sorts after "Z" (0x5A) and before "a" (0x61).
        string json = CanonicalAuditHasher.Canonicalizar(new Dictionary<string, object?>
        {
            ["a"] = "1",
            ["_"] = "2",
            ["Z"] = "3"
        });

        json.ShouldBe("{\"Z\":\"3\",\"_\":\"2\",\"a\":\"1\"}");
    }

    [Fact]
    public void Canonicalizar_ShouldRejectUnsupportedValues()
    {
        Should.Throw<ArgumentException>(() => CanonicalAuditHasher.Canonicalizar(
            new Dictionary<string, object?> { ["decimal"] = 1.5m }));
    }

    [Fact]
    public void Canonicalizar_ShouldRejectALoneSurrogate()
    {
        Should.Throw<ArgumentException>(() => CanonicalAuditHasher.Canonicalizar(
            new Dictionary<string, object?> { ["x"] = "\ud800" }));
    }
}

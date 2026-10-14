using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Incapacidades;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.Infrastructure.Archivos;
using Halsa.Backend.Infrastructure.Tenancy;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;
using static Halsa.Backend.IntegrationTests.Integraciones.IngestaSql;

namespace Halsa.Backend.IntegrationTests.Incapacidades;

/// <summary>
/// SPEC 07, step 6: <see cref="RegistrarIncapacidadCommand"/> through the mediator on <c>halsa_ingesta</c>, with a fake
/// antivirus and the real encrypted file store in a temporary folder. Each test seeds its own case.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class RegistrarIncapacidadTests : IAsyncDisposable
{
    private static readonly FakeTenantContext IntegracionIps = new(ActorTipo.Integracion, null, new IpsId(Ips));

    private static readonly byte[] Pdf = Encoding.ASCII.GetBytes("%PDF-1.7\nBT (Incapacidad de prueba) ET\n%%EOF\n");

    private readonly DatabaseFixture _db;
    private readonly string _raiz = Path.Combine(Path.GetTempPath(), $"halsa-incapacidades-{Guid.NewGuid():N}");
    private readonly FakeAntivirus _antivirus = new();
    private readonly ServiceProvider _services;

    public RegistrarIncapacidadTests(DatabaseFixture db)
    {
        _db = db;
        _services = db.CrearServicios(services =>
        {
            services.RemoveAll<IAntivirusScanner>();
            services.AddSingleton<IAntivirusScanner>(_antivirus);
            services.Configure<ArchivosOptions>(o => o.Raiz = _raiz);
        });
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        if (Directory.Exists(_raiz))
        {
            Directory.Delete(_raiz, recursive: true);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(-5)).DateTime);

    private static JsonObject Datos(
        DateOnly? fechaInicio = null,
        int dias = 10,
        string tipo = "INICIAL",
        Guid? prorrogaDe = null,
        string codigo = "S62.6",
        string? numeroCertificado = null,
        bool conProfesional = true) => new()
    {
        ["numeroCertificado"] = numeroCertificado ?? $"CERT-{Guid.NewGuid():N}"[..30],
        ["tipo"] = tipo,
        ["prorrogaDe"] = prorrogaDe?.ToString(),
        ["fechaInicio"] = (fechaInicio ?? Hoy.AddDays(-20)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ["dias"] = dias,
        ["diagnosticoPrincipal"] = new JsonObject { ["sistema"] = "CIE-10", ["codigo"] = codigo, ["descripcion"] = "Fractura" },
        ["diagnosticosRelacionados"] = new JsonArray(new JsonObject { ["sistema"] = "CIE-10", ["codigo"] = "W19" }),
        ["profesional"] = conProfesional
            ? new JsonObject
            {
                ["identificacion"] = new JsonObject { ["tipo"] = "CC", ["numero"] = "79111222" },
                ["nombreCompleto"] = "Ana Ruiz",
                ["registroRethus"] = "RM-12345"
            }
            : null,
        ["ipsEmisora"] = new JsonObject { ["codigoReps"] = Ips, ["nombre"] = "IPS Uno" },
        ["modalidad"] = "PRESENCIAL",
        ["observaciones"] = "Reposo absoluto, observación confidencial."
    };

    private async Task<Result<IncapacidadResponse>> CargarAsync(
        Guid caso,
        JsonObject datos,
        Guid? llave = null,
        ITenantContext? actor = null,
        ProfesionalDelToken? profesional = null,
        byte[]? soporte = null)
    {
        ITenantContext quien = actor ?? IntegracionIps;

        await using AsyncServiceScope scope = _services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TestActor>().Tenant = quien;
        scope.ServiceProvider.GetRequiredService<TenantContextHolder>()
            .Establecer(new TenantContextData(quien.Tipo, quien.Sub, quien.TenantId, quien.IpsId));

        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(new RegistrarIncapacidadCommand(
            new CasoMedicoId(caso),
            (llave ?? Guid.NewGuid()).ToString(),
            Encoding.UTF8.GetBytes(datos.ToJsonString()),
            soporte ?? Pdf,
            profesional,
            "int-ips-110011234501"));
    }

    private async Task<IncapacidadResponse> CargarValidaAsync(Guid caso, JsonObject datos, Guid? llave = null)
    {
        Result<IncapacidadResponse> result = await CargarAsync(caso, datos, llave);
        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        return result.Value;
    }

    private async Task<Guid> CasoRemitidoAsync(string tenant = ArlA, DateTimeOffset? fechaEvento = null)
    {
        Guid caso = await InsertCasoAsync(_db, tenant, fechaEvento ?? DateTimeOffset.UtcNow.AddDays(-60), "EnAtencion");
        await InsertRemisionAsync(_db, caso, tenant, Ips);
        return caso;
    }

    private Task<long> FilasAsync(string tabla, Guid caso) =>
        ContarAsync(_db, $"SELECT count(*) FROM {tabla} WHERE caso_medico_id = @caso", ("caso", caso));

    private int ArchivosEnDisco() =>
        Directory.Exists(_raiz) ? Directory.GetFiles(_raiz, "*.bin", SearchOption.AllDirectories).Length : 0;

    // ── Success ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Inicial_FromTheIpsIntegration_ShouldRegisterEverythingInOneTransaction()
    {
        Guid caso = await CasoRemitidoAsync();
        DateOnly inicio = Hoy.AddDays(-20);

        IncapacidadResponse respuesta = await CargarValidaAsync(caso, Datos(inicio, dias: 10));

        respuesta.CasoId.ShouldBe(caso);
        respuesta.FechaInicio.ShouldBe(inicio);
        respuesta.FechaFin.ShouldBe(inicio.AddDays(9));
        respuesta.Dias.ShouldBe(10);
        respuesta.DiasAcumulados.ShouldBe(10);
        respuesta.Estado.ShouldBe("REGISTRADA");
        respuesta.Tipo.ShouldBe("INICIAL");
        respuesta.Alertas.ShouldBeEmpty();

        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(1);
        (await FilasAsync("clinico.soportes", caso)).ShouldBe(1);
        (await ContarAsync(_db,
            "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE caso_medico_id = @caso AND tipo = 'CertificadoIncapacidad'",
            ("caso", caso))).ShouldBe(1);
        (await ContarAsync(_db, """
            SELECT count(*) FROM auditoria.accesos WHERE caso_medico_id = @caso AND accion = 'CREAR'
              AND resultado = 'EXITO' AND tenant_id = 'ARL_A' AND recurso_tipo IN ('INCAPACIDAD', 'REGISTRO_CLINICO')
            """, ("caso", caso))).ShouldBe(2);
        (await ContarAsync(_db, """
            SELECT count(*) FROM outbox.outbox_messages
            WHERE type LIKE '%IncapacidadRegistradaIntegrationEvent' AND content::text LIKE '%' || @caso || '%'
            """, ("caso", caso.ToString()))).ShouldBe(1);
        ArchivosEnDisco().ShouldBe(1);
    }

    [Fact]
    public async Task TheSupport_ShouldBeStoredEncrypted_WithTheOriginalsSha256()
    {
        Guid caso = await CasoRemitidoAsync();

        IncapacidadResponse respuesta = await CargarValidaAsync(caso, Datos());

        await using NpgsqlConnection conn = await OpenAsync(_db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ruta, sha256_claro, tamano, tenant_id, key_id FROM clinico.soportes WHERE id = @id";
        cmd.Parameters.AddWithValue("id", respuesta.SoporteId);
        await using NpgsqlDataReader reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue();

        reader.GetFieldValue<byte[]>(1).ShouldBe(SHA256.HashData(Pdf));
        reader.GetInt64(2).ShouldBe(Pdf.Length);
        reader.GetString(3).ShouldBe(ArlA);
        reader.GetString(4).ShouldBe("ARL_A:SOPORTES:v1");

        string enDisco = Encoding.ASCII.GetString(await File.ReadAllBytesAsync(Path.Combine(_raiz, reader.GetString(0))));
        enDisco.ShouldNotContain("%PDF-");
        enDisco.ShouldNotContain("Incapacidad de prueba");
    }

    [Fact]
    public async Task TheDiagnosis_ShouldNotBeInClear_InAtel()
    {
        Guid caso = await CasoRemitidoAsync();

        await CargarValidaAsync(caso, Datos(codigo: "S62.6"));

        // Every column of the row, as text: neither the code, its category, nor the observations.
        string fila = (string)(await ScalarTextoAsync(
            "SELECT row_to_json(i)::text FROM atel.incapacidades i WHERE caso_medico_id = @caso", caso))!;
        fila.ShouldNotContain("S62");
        fila.ShouldNotContain("W19");
        fila.ShouldNotContain("confidencial");
    }

    [Fact]
    public async Task Backoffice_ShouldSignTheClinicalRecordWithTheTokensProfessional()
    {
        Guid caso = await CasoRemitidoAsync();
        var medicoIps = new FakeTenantContext(ActorTipo.Ips, null, new IpsId(Ips));
        var token = new ProfesionalDelToken(new ProfesionalRef(medicoIps.Sub, "CC", "80000222", "RM-DEV-0002"), "Medico IPS");

        Result<IncapacidadResponse> result = await CargarAsync(
            caso, Datos(conProfesional: false), actor: medicoIps, profesional: token);

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
        (await ScalarTextoAsync("""
            SELECT profesional_registro_rethus FROM clinico.historia_clinica_detalle
            WHERE caso_medico_id = @caso AND tipo = 'CertificadoIncapacidad'
            """, caso)).ShouldBe("RM-DEV-0002");
        (await ScalarTextoAsync(
            "SELECT profesional_registro_rethus FROM atel.incapacidades WHERE caso_medico_id = @caso", caso))
            .ShouldBe("RM-DEV-0002");
    }

    // ── Idempotency ───────────────────────────────────────────────────────────

    [Fact]
    public async Task SameKeyAndBody_ShouldReplay_WithoutASecondFileOrRow()
    {
        Guid caso = await CasoRemitidoAsync();
        JsonObject datos = Datos();
        Guid llave = Guid.NewGuid();

        IncapacidadResponse primera = await CargarValidaAsync(caso, datos, llave);
        IncapacidadResponse segunda = await CargarValidaAsync(caso, datos, llave);

        segunda.Id.ShouldBe(primera.Id);
        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(1);
        (await FilasAsync("clinico.soportes", caso)).ShouldBe(1);
        ArchivosEnDisco().ShouldBe(1);
    }

    [Fact]
    public async Task SameKey_WithAnotherPdf_ShouldConflict()
    {
        Guid caso = await CasoRemitidoAsync();
        JsonObject datos = Datos();
        Guid llave = Guid.NewGuid();
        await CargarValidaAsync(caso, datos, llave);

        Result<IncapacidadResponse> result = await CargarAsync(
            caso, datos, llave, soporte: Encoding.ASCII.GetBytes("%PDF-1.7\notro\n"));

        result.Error.ShouldBe(IntegracionErrors.IdempotenciaConflicto);
    }

    [Fact]
    public async Task SameCertificate_FromTheSameIps_WithAnotherKey_ShouldConflict()
    {
        Guid caso = await CasoRemitidoAsync();
        await CargarValidaAsync(caso, Datos(Hoy.AddDays(-40), numeroCertificado: "CERT-DUP-1"));

        Result<IncapacidadResponse> result = await CargarAsync(caso, Datos(Hoy.AddDays(-20), numeroCertificado: "CERT-DUP-1"));

        result.Error.ShouldBe(IncapacidadCargaErrors.CertificadoDuplicado);
        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(1);
    }

    // ── Antivirus ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AnInfectedSupport_ShouldBeRefused_LeavingNoFileNorRows()
    {
        Guid caso = await CasoRemitidoAsync();
        _antivirus.Resultado = ResultadoAntivirus.Infectado("Eicar-Test-Signature");

        Result<IncapacidadResponse> result = await CargarAsync(caso, Datos());

        result.Error.ShouldBe(IncapacidadCargaErrors.SoporteInfectado);
        ArchivosEnDisco().ShouldBe(0);
        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(0);
        (await FilasAsync("clinico.soportes", caso)).ShouldBe(0);
        (await FilasAsync("clinico.historia_clinica_detalle", caso)).ShouldBe(0);
    }

    [Fact]
    public async Task WithoutAntivirus_ShouldFailClosed()
    {
        Guid caso = await CasoRemitidoAsync();
        _antivirus.Resultado = ResultadoAntivirus.NoDisponible("timeout");

        Result<IncapacidadResponse> result = await CargarAsync(caso, Datos());

        result.Error.ShouldBe(IncapacidadCargaErrors.AntivirusNoDisponible);
        ArchivosEnDisco().ShouldBe(0);
    }

    // ── Visibility, format and rules ──────────────────────────────────────────

    [Fact]
    public async Task ACaseNotRemittedToTheIps_ShouldNotBeFound()
    {
        Guid caso = await InsertCasoAsync(_db, ArlA, DateTimeOffset.UtcNow.AddDays(-60), "EnAtencion");

        (await CargarAsync(caso, Datos())).Error.ShouldBe(IncapacidadCargaErrors.CasoNoEncontrado);
        ArchivosEnDisco().ShouldBe(0);
    }

    [Fact]
    public async Task AnotherArl_ShouldNotFindTheCase()
    {
        Guid caso = await CasoRemitidoAsync(ArlA);
        var medicoArlB = new FakeTenantContext(ActorTipo.Arl, new TenantId(ArlB), null);
        var token = new ProfesionalDelToken(new ProfesionalRef(medicoArlB.Sub, "CC", "1", "RM-1"), null);

        Result<IncapacidadResponse> result = await CargarAsync(caso, Datos(), actor: medicoArlB, profesional: token);

        result.Error.ShouldBe(IncapacidadCargaErrors.CasoNoEncontrado);
    }

    [Fact]
    public async Task TheIntegration_WithoutProfesional_ShouldFailValidation_InTheDatosPart()
    {
        Guid caso = await CasoRemitidoAsync();

        Result<IncapacidadResponse> result = await CargarAsync(caso, Datos(conProfesional: false));

        ErrorDeCampo error = result.Error.ShouldBeOfType<ValidationError>().Errors.Cast<ErrorDeCampo>().ShouldHaveSingleItem();
        error.Ruta.ShouldBe("datos.profesional");
    }

    [Fact]
    public async Task AnArlIntegration_ShouldNotBeAllowed()
    {
        Guid caso = await CasoRemitidoAsync();

        Result<IncapacidadResponse> result = await CargarAsync(caso, Datos(), actor: Integracion(ArlA));

        result.Error.ShouldBe(IncapacidadCargaErrors.ActorNoAutorizado);
    }

    [Fact]
    public async Task AnOverlappingIncapacidad_ShouldBeRejected()
    {
        Guid caso = await CasoRemitidoAsync();
        await CargarValidaAsync(caso, Datos(Hoy.AddDays(-20), dias: 10));

        Result<IncapacidadResponse> result = await CargarAsync(caso, Datos(Hoy.AddDays(-15), dias: 10));

        result.Error.ShouldBe(IncapacidadErrors.Solapada);
        (await FilasAsync("atel.incapacidades", caso)).ShouldBe(1);
    }

    [Theory]
    [InlineData("S62.0", true)]
    [InlineData("M54.5", false)]
    public async Task AProrroga_ShouldKeepTheCie10CategoryOfItsInitial(string codigoProrroga, bool aceptada)
    {
        Guid caso = await CasoRemitidoAsync();
        IncapacidadResponse inicial = await CargarValidaAsync(caso, Datos(Hoy.AddDays(-30), dias: 10, codigo: "S62.6"));

        Result<IncapacidadResponse> result = await CargarAsync(caso, Datos(
            Hoy.AddDays(-20), dias: 5, tipo: "PRORROGA", prorrogaDe: inicial.Id, codigo: codigoProrroga));

        if (aceptada)
        {
            result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
            result.Value.DiasAcumulados.ShouldBe(15);
        }
        else
        {
            result.Error.ShouldBe(IncapacidadErrors.ProrrogaDiagnosticoNoRelacionado);
        }
    }

    [Fact]
    public async Task AClosedCase_ShouldBeRejected()
    {
        Guid caso = await InsertCasoAsync(_db, ArlA, DateTimeOffset.UtcNow.AddDays(-60), "Cerrado");
        await InsertRemisionAsync(_db, caso, ArlA, Ips);

        (await CargarAsync(caso, Datos())).Error.ShouldBe(IncapacidadErrors.CasoCerrado);
    }

    private async Task<object?> ScalarTextoAsync(string sql, Guid caso)
    {
        await using NpgsqlConnection conn = await OpenAsync(_db.SuperuserConnectionString);
        await using NpgsqlCommand cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("caso", caso);
        return await cmd.ExecuteScalarAsync();
    }

    /// <summary>An antivirus whose verdict the test decides. Clean by default.</summary>
    private sealed class FakeAntivirus : IAntivirusScanner
    {
        public ResultadoAntivirus Resultado { get; set; } = ResultadoAntivirus.Limpio;

        public Task<ResultadoAntivirus> AnalizarAsync(Stream contenido, CancellationToken ct) => Task.FromResult(Resultado);
    }
}

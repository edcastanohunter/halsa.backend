using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Clinico.Commands.Corregir;
using Halsa.Backend.Application.Clinico.Commands.Registrar;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Clinico;

/// <summary>
/// SPEC 06, step 2: the gate brings the event date and the state, and the SPEC 03 commands reject a care date
/// before the event and a case that no longer takes records. A rejected command leaves nothing behind.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ClinicalCaseRulesTests(DatabaseFixture db)
{
    private static readonly FakeTenantContext ActorArlA = FakeTenantContext.Arl(ArlA);
    private static readonly FakeTenantContext ActorIps = FakeTenantContext.Ips(Ips);

    private Task<Result<RegistroClinicoCreadoResponse>> RegistrarAsync(FakeTenantContext actor, Guid caso, DateTimeOffset atendidoEn) =>
        SendAsync(db, actor, new RegistrarRegistroClinicoCommand(
            new CasoMedicoId(caso), TipoRegistroClinico.ConceptoMedico, atendidoEn, Contenido("Control"), Medico));

    private async Task<long> RegistrosAsync(Guid caso)
    {
        await using NpgsqlConnection conn = await OpenAsync(db.SuperuserConnectionString);
        return await ScalarAsync(conn,
            "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE caso_medico_id = @caso", ("caso", caso));
    }

    // ── Gate ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Gate_ShouldReturnTheTenantTheEventDateAndTheState_ToTheIpsWithVigenteRemision()
    {
        var fechaEvento = new DateTimeOffset(2026, 9, 1, 13, 30, 0, TimeSpan.Zero);
        Guid caso = await InsertCasoAsync(db, ArlA, fechaEvento, estado: "EnAtencion");
        await InsertRemisionAsync(db, caso, ArlA, Ips);

        await using AsyncServiceScope scope = db.CreateScope(ActorIps);
        CasoVisible? visible = await scope.ServiceProvider.GetRequiredService<IClinicalCaseGate>()
            .CasoVisibleAsync(new CasoMedicoId(caso), CancellationToken.None);

        visible.ShouldBe(new CasoVisible(new TenantId(ArlA), fechaEvento, EstadoCaso.EnAtencion));
    }

    [Fact]
    public async Task Gate_ShouldReturnNull_ForAnotherTenantsCase()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);

        await using AsyncServiceScope scope = db.CreateScope(ActorArlA);
        CasoVisible? visible = await scope.ServiceProvider.GetRequiredService<IClinicalCaseGate>()
            .CasoVisibleAsync(new CasoMedicoId(caso), CancellationToken.None);

        visible.ShouldBeNull();
    }

    // ── Care date against the event ───────────────────────────────────────────

    [Fact]
    public async Task Registrar_TwoDaysBeforeTheEvent_ShouldFailWithFechaAtencionAnteriorAlEvento()
    {
        DateTimeOffset fechaEvento = DateTimeOffset.UtcNow.AddDays(-5);
        Guid caso = await InsertCasoAsync(db, ArlA, fechaEvento);

        Result<RegistroClinicoCreadoResponse> result = await RegistrarAsync(ActorArlA, caso, fechaEvento.AddDays(-2));

        result.Error.ShouldBe(ClinicoErrors.FechaAtencionAnteriorAlEvento);
        (await RegistrosAsync(caso)).ShouldBe(0);
    }

    [Fact]
    public async Task Registrar_WithinTheOneDayTolerance_ShouldSucceed()
    {
        DateTimeOffset fechaEvento = DateTimeOffset.UtcNow.AddDays(-5);
        Guid caso = await InsertCasoAsync(db, ArlA, fechaEvento);

        Result<RegistroClinicoCreadoResponse> result = await RegistrarAsync(ActorArlA, caso, fechaEvento.AddHours(-23));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
    }

    // ── Case state ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Rechazado")]
    [InlineData("Cerrado")]
    public async Task Registrar_OnARejectedOrClosedCase_ShouldFailWithCasoNoAdmiteRegistros(string estado)
    {
        Guid caso = await InsertCasoAsync(db, ArlA, estado: estado);

        Result<RegistroClinicoCreadoResponse> result = await RegistrarAsync(ActorArlA, caso, DateTimeOffset.UtcNow.AddMinutes(-5));

        result.Error.ShouldBe(ClinicoErrors.CasoNoAdmiteRegistros);
        (await RegistrosAsync(caso)).ShouldBe(0);
    }

    [Theory]
    [InlineData("Validado")]
    [InlineData("EnAtencion")]
    [InlineData("EnCalificacionOrigen")]
    [InlineData("EnCalificacionPcl")]
    public async Task Registrar_OnAnOpenCase_ShouldSucceed(string estado)
    {
        Guid caso = await InsertCasoAsync(db, ArlA, estado: estado);

        Result<RegistroClinicoCreadoResponse> result = await RegistrarAsync(ActorArlA, caso, DateTimeOffset.UtcNow.AddMinutes(-5));

        result.IsSuccess.ShouldBeTrue(result.IsFailure ? result.Error.ToString() : null);
    }

    [Fact]
    public async Task Corregir_OnARejectedCase_ShouldFailWithCasoNoAdmiteRegistros()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid original = await ClinicoSql.RegistrarAsync(db, ActorArlA, caso, Contenido("Original"));

        await using (NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString, tenantId: ArlA))
        {
            await using NpgsqlCommand cmd = conn.CreateCommand();
            cmd.CommandText = "UPDATE atel.casos_medicos SET estado = 'Rechazado' WHERE id = @id";
            cmd.Parameters.AddWithValue("id", caso);
            (await cmd.ExecuteNonQueryAsync()).ShouldBe(1);
        }

        Result<RegistroClinicoCreadoResponse> result = await SendAsync(db, ActorArlA, new CorregirRegistroClinicoCommand(
            new HistoriaClinicaDetalleId(original), new CasoMedicoId(caso), TipoRegistroClinico.ConceptoMedico,
            DateTimeOffset.UtcNow.AddMinutes(-5), Contenido("Corrección"), Medico));

        result.Error.ShouldBe(ClinicoErrors.CasoNoAdmiteRegistros);
        (await RegistrosAsync(caso)).ShouldBe(1);
    }
}

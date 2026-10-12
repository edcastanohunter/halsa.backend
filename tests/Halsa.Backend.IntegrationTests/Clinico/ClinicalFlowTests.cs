using Halsa.Backend.Application.Clinico.Commands.Corregir;
using Halsa.Backend.Application.Clinico.Commands.Registrar;
using Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Clinico;

/// <summary>
/// The full clinical flow through the mediator, as an ARL and as an IPS: gate on the administrative
/// side, encryption, the clinical context as <c>halsa_clinico</c>, and decryption on the way back.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class ClinicalFlowTests(DatabaseFixture db)
{
    private static readonly FakeTenantContext ActorArlA = FakeTenantContext.Arl(ArlA);
    private static readonly FakeTenantContext ActorArlB = FakeTenantContext.Arl(ArlB);
    private static readonly FakeTenantContext ActorIps = FakeTenantContext.Ips(Ips);

    private Task<Result<IReadOnlyList<RegistroClinicoResponse>>> LeerAsync(FakeTenantContext actor, Guid caso) =>
        SendAsync(db, actor, new GetRegistrosClinicosQuery(new CasoMedicoId(caso)));

    // ── ARL ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Arl_ShouldRegisterAndReadItsOwnHistory()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        var contenido = Contenido("Dolor en muñeca", new CodigoClinico("CIE-10", "S62.6"));

        Guid registro = await RegistrarAsync(db, ActorArlA, caso, contenido);

        Result<IReadOnlyList<RegistroClinicoResponse>> result = await LeerAsync(ActorArlA, caso);

        result.IsSuccess.ShouldBeTrue();
        RegistroClinicoResponse leido = result.Value.ShouldHaveSingleItem();
        leido.Id.ShouldBe(registro);
        leido.CasoId.ShouldBe(caso);
        leido.Contenido.Motivo.ShouldBe("Dolor en muñeca");
        leido.Contenido.Diagnosticos.ShouldBe([new CodigoClinico("CIE-10", "S62.6")]);
        leido.Profesional.Sub.ShouldBe(ActorArlA.Sub);
        leido.Profesional.RegistroRethus.ShouldBe(Medico.RegistroRethus);
        leido.RetenerHasta.ShouldBe(DateOnly.FromDateTime(leido.RegistradoEn.UtcDateTime).AddYears(15));
        leido.CorrigeA.ShouldBeNull();
        leido.CorregidoPor.ShouldBeNull();
    }

    [Fact]
    public async Task Arl_ReadingAnotherTenantsCase_ShouldReturnCasoNoEncontrado()
    {
        Guid casoB = await InsertCasoAsync(db, ArlB);
        await RegistrarAsync(db, ActorArlB, casoB, Contenido("De ARL_B"));

        Result<IReadOnlyList<RegistroClinicoResponse>> result = await LeerAsync(ActorArlA, casoB);

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBe(ClinicoErrors.CasoNoEncontrado);
    }

    [Fact]
    public async Task Arl_WritingOnAnotherTenantsCase_ShouldReturnCasoNoEncontrado()
    {
        Guid casoB = await InsertCasoAsync(db, ArlB);

        Result<Guid> result = await SendAsync(db, ActorArlA, new RegistrarRegistroClinicoCommand(
            new CasoMedicoId(casoB), TipoRegistroClinico.ConceptoMedico,
            DateTimeOffset.UtcNow.AddHours(-1), Contenido("Intruso"), Medico));

        result.Error.ShouldBe(ClinicoErrors.CasoNoEncontrado);
    }

    [Fact]
    public async Task ClinicalContext_WithTenantA_ShouldNotSeeTenantBRecords_EvenWithoutTheQueryFilter()
    {
        Guid casoB = await InsertCasoAsync(db, ArlB);
        Guid registroB = await RegistrarAsync(db, ActorArlB, casoB, Contenido("De ARL_B"));

        await using var clinical = db.CreateClinicalDbContext(ActorArlA);

        bool visible = await clinical.HistoriaClinica
            .IgnoreQueryFilters()
            .AnyAsync(h => h.Id == new HistoriaClinicaDetalleId(registroB));

        visible.ShouldBeFalse();
    }

    // ── IPS ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Ips_WithVigenteRemision_ShouldRegisterAndRead_UntilTheRemisionIsRevoked()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid remision = await InsertRemisionAsync(db, caso, ArlA, Ips);

        Guid registro = await RegistrarAsync(db, ActorIps, caso, Contenido("Valoración IPS"));

        // The IPS reads it, and the record belongs to the case's tenant: the ARL reads it too.
        (await LeerAsync(ActorIps, caso)).Value.ShouldHaveSingleItem().Id.ShouldBe(registro);
        (await LeerAsync(ActorArlA, caso)).Value.ShouldHaveSingleItem().Id.ShouldBe(registro);

        await RevocarRemisionAsync(db, remision, ArlA);

        Result<IReadOnlyList<RegistroClinicoResponse>> despues = await LeerAsync(ActorIps, caso);
        despues.Error.ShouldBe(ClinicoErrors.CasoNoEncontrado);

        await using NpgsqlConnection conn = await OpenAsync(db.ClinicalConnectionString, ipsId: Ips);
        (await ScalarAsync(conn, "SELECT count(*) FROM clinico.historia_clinica_detalle WHERE caso_medico_id = @caso",
            ("caso", caso))).ShouldBe(0);
    }

    [Fact]
    public async Task Ips_WithoutRemision_ShouldReturnCasoNoEncontrado()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        await RegistrarAsync(db, ActorArlA, caso, Contenido("Solo ARL"));

        (await LeerAsync(ActorIps, caso)).Error.ShouldBe(ClinicoErrors.CasoNoEncontrado);
    }

    // ── Corrections ───────────────────────────────────────────────────────────

    [Fact]
    public async Task Corregir_ShouldLeaveBothRecords_AndMarkTheOriginal()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid original = await RegistrarAsync(db, ActorArlA, caso, Contenido("Con error"));

        Result<Guid> correccion = await SendAsync(db, ActorArlA, new CorregirRegistroClinicoCommand(
            new HistoriaClinicaDetalleId(original), new CasoMedicoId(caso), TipoRegistroClinico.ConceptoMedico,
            DateTimeOffset.UtcNow.AddHours(-1), Contenido("Corregido"), Medico));

        correccion.IsSuccess.ShouldBeTrue();

        IReadOnlyList<RegistroClinicoResponse> registros = (await LeerAsync(ActorArlA, caso)).Value;

        registros.Count.ShouldBe(2);
        registros[0].Id.ShouldBe(original);
        registros[0].Contenido.Motivo.ShouldBe("Con error");
        registros[0].CorregidoPor.ShouldBe(correccion.Value);
        registros[1].Id.ShouldBe(correccion.Value);
        registros[1].CorrigeA.ShouldBe(original);
        registros[1].Contenido.Motivo.ShouldBe("Corregido");
    }

    [Fact]
    public async Task Corregir_ARecordOfAnotherCase_ShouldReturnRegistroNoEncontrado()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid otroCaso = await InsertCasoAsync(db, ArlA);
        Guid registroDeOtroCaso = await RegistrarAsync(db, ActorArlA, otroCaso, Contenido("Otro caso"));

        Result<Guid> result = await SendAsync(db, ActorArlA, new CorregirRegistroClinicoCommand(
            new HistoriaClinicaDetalleId(registroDeOtroCaso), new CasoMedicoId(caso),
            TipoRegistroClinico.ConceptoMedico, DateTimeOffset.UtcNow.AddHours(-1), Contenido("x"), Medico));

        result.Error.ShouldBe(ClinicoErrors.RegistroNoEncontrado);
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Registrar_WithoutRethus_OutsideReporteInicial_ShouldFailValidation()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);

        Result<Guid> result = await SendAsync(db, ActorArlA, new RegistrarRegistroClinicoCommand(
            new CasoMedicoId(caso), TipoRegistroClinico.Evolucion, DateTimeOffset.UtcNow.AddHours(-1),
            Contenido("Sin RETHUS"), Medico with { RegistroRethus = " " }));

        result.IsFailure.ShouldBeTrue();
        result.Error.ShouldBeOfType<ValidationError>()
            .Errors.ShouldContain(e => e.Code == "Clinico.RethusRequerido");
    }

    [Fact]
    public async Task Registrar_WithAnEmptyContenido_ShouldFailValidation()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);

        Result<Guid> result = await SendAsync(db, ActorArlA, new RegistrarRegistroClinicoCommand(
            new CasoMedicoId(caso), TipoRegistroClinico.ConceptoMedico, DateTimeOffset.UtcNow.AddHours(-1),
            new ContenidoClinico(" ", null, null, null, null, [], ""), Medico));

        result.Error.ShouldBeOfType<ValidationError>()
            .Errors.ShouldContain(e => e.Code == "Clinico.ContenidoVacio");
    }

    // ── Keys under concurrency ────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentFirstRegistrations_ShouldLeaveOneActiveKeyPerPurpose()
    {
        // A tenant no other test uses, so these 20 registrations race for its very first DEKs.
        const string tenant = "ARL_CONCURRENCIA";
        Guid caso = await InsertCasoAsync(db, tenant);
        var actor = FakeTenantContext.Arl(tenant);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i =>
            RegistrarAsync(db, actor, caso, Contenido($"Concurrente {i}", new CodigoClinico("CIE-10", "S62.6")))));

        await using NpgsqlConnection conn = await OpenAsync(db.OwnerConnectionString);

        (await ScalarAsync(conn, "SELECT count(*) FROM clinico.llaves_tenant WHERE tenant_id = @t AND activa",
            ("t", tenant))).ShouldBe(2);
        (await ScalarAsync(conn, """
            SELECT count(*) FROM (
                SELECT proposito FROM clinico.llaves_tenant
                WHERE tenant_id = @t AND activa GROUP BY proposito HAVING count(*) = 1) unicas
            """, ("t", tenant))).ShouldBe(2);

        (await LeerAsync(actor, caso)).Value.Count.ShouldBe(20);
    }
}

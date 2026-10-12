using Halsa.Backend.Application.Casos.Commands.Asignar;
using Halsa.Backend.Application.Casos.Commands.Desasignar;
using Halsa.Backend.Application.Casos.Commands.Remitir;
using Halsa.Backend.Application.Casos.Commands.RevocarRemision;
using Halsa.Backend.Application.Clinico.Queries.GetRegistrosClinicos;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Infrastructure.Auditoria;
using Halsa.Backend.Infrastructure.Database.Auditoria;
using Halsa.Backend.IntegrationTests.Abstractions;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Halsa.Backend.IntegrationTests.Auditoria.AuditoriaSql;
using static Halsa.Backend.IntegrationTests.Clinico.ClinicoSql;

namespace Halsa.Backend.IntegrationTests.Auditoria;

/// <summary>
/// SPEC 04, what gets audited and how: writes commit with their audit row or not at all, reads are
/// recorded with the tenant of the resource, and every stored hash can be recomputed.
/// </summary>
[Collection(nameof(DatabaseCollection))]
public sealed class AuditoriaAccesosTests(DatabaseFixture db)
{
    private static readonly FakeTenantContext ActorArlA = FakeTenantContext.Arl(ArlA);
    private static readonly FakeTenantContext ActorIps = FakeTenantContext.Ips(Ips);

    // ── Escrituras administrativas ────────────────────────────────────────────

    [Fact]
    public async Task Remitir_ShouldLeaveExactlyOneCrearRemisionExitoRow_WithTheCasesTenant()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);

        Result<Guid> remision = await SendAsync(db, ActorArlA, new RemitirCasoCommand(new CasoMedicoId(caso), Ips));

        remision.IsSuccess.ShouldBeTrue();
        AccesoAuditado fila = (await AccesosDelCasoAsync(db, caso)).ShouldHaveSingleItem();
        fila.Accion.ShouldBe("CREAR");
        fila.RecursoTipo.ShouldBe("REMISION");
        fila.Resultado.ShouldBe("EXITO");
        fila.TenantId.ShouldBe(ArlA);
        fila.RecursoId.ShouldBe(remision.Value.ToString());
        fila.ActorTipo.ShouldBe("arl");
        fila.ActorId.ShouldBe(ActorArlA.Sub);
    }

    [Fact]
    public async Task Remitir_ShouldNotPersistTheRemision_WhenTheAuditInsertFails()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);

        // A constraint that only this case's audit row violates, so other tests are unaffected.
        string constraint = $"ck_test_falla_{caso:N}";
        await ExecuteAsync(db.OwnerConnectionString,
            $"ALTER TABLE auditoria.accesos ADD CONSTRAINT {constraint} CHECK (caso_medico_id IS DISTINCT FROM '{caso}')");

        try
        {
            await Should.ThrowAsync<DbUpdateException>(
                () => SendAsync(db, ActorArlA, new RemitirCasoCommand(new CasoMedicoId(caso), Ips)));
        }
        finally
        {
            await ExecuteAsync(db.OwnerConnectionString, $"ALTER TABLE auditoria.accesos DROP CONSTRAINT {constraint}");
        }

        (await ScalarAsSuperuserAsync(db, "SELECT count(*) FROM atel.remisiones WHERE caso_medico_id = @id", caso))
            .ShouldBe(0L);
        (await AccesosDelCasoAsync(db, caso)).ShouldBeEmpty();
    }

    [Fact]
    public async Task Revocar_ShouldLeaveAnActualizarRemisionRow()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        Guid remision = await InsertRemisionAsync(db, caso, ArlA, Ips);

        Result result = await EnviarAsync(db, ActorArlA,
            new RevocarRemisionCommand(new CasoMedicoId(caso), new RemisionId(remision)));

        result.IsSuccess.ShouldBeTrue();
        AccesoAuditado fila = (await AccesosDelCasoAsync(db, caso)).ShouldHaveSingleItem();
        (fila.Accion, fila.RecursoTipo, fila.RecursoId).ShouldBe(("ACTUALIZAR", "REMISION", remision.ToString()));
    }

    [Fact]
    public async Task Asignar_ShouldBeAudited_EvenWhenTheAssignmentAlreadyExisted_AndDesasignarToo()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        var casoId = new CasoMedicoId(caso);

        (await EnviarAsync(db, ActorArlA, new AsignarCasoCommand(casoId, "sub-equipo"))).IsSuccess.ShouldBeTrue();
        (await EnviarAsync(db, ActorArlA, new AsignarCasoCommand(casoId, "sub-equipo"))).IsSuccess.ShouldBeTrue();
        (await EnviarAsync(db, ActorArlA, new DesasignarCasoCommand(casoId, "sub-equipo"))).IsSuccess.ShouldBeTrue();

        List<AccesoAuditado> filas = await AccesosDelCasoAsync(db, caso);
        filas.Select(f => (f.Accion, f.RecursoTipo, f.RecursoId))
            .ShouldBe([("CREAR", "ASIGNACION", "sub-equipo"), ("CREAR", "ASIGNACION", "sub-equipo"), ("ELIMINAR", "ASIGNACION", "sub-equipo")]);
    }

    // ── Clínico ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task RegistrarClinico_ByTheIps_OnAnArlBCase_ShouldRecordTheResourceTenantAndTheIpsActor()
    {
        Guid caso = await InsertCasoAsync(db, ArlB);
        await InsertRemisionAsync(db, caso, ArlB, Ips);

        Guid registro = await RegistrarAsync(db, ActorIps, caso, Contenido("Control"));

        AccesoAuditado fila = (await AccesosDelCasoAsync(db, caso)).ShouldHaveSingleItem();
        fila.Accion.ShouldBe("CREAR");
        fila.RecursoTipo.ShouldBe("REGISTRO_CLINICO");
        fila.RecursoId.ShouldBe(registro.ToString());
        fila.TenantId.ShouldBe(ArlB);
        fila.ActorTipo.ShouldBe("ips");
        fila.ActorIpsId.ShouldBe(Ips);
        fila.ActorTenantId.ShouldBeNull();
    }

    [Fact]
    public async Task GetRegistrosClinicos_ShouldLeaveALeerExitoRow()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        await RegistrarAsync(db, ActorArlA, caso, Contenido("Consulta"));

        var result = await SendAsync(db, ActorArlA, new GetRegistrosClinicosQuery(new CasoMedicoId(caso)));

        result.IsSuccess.ShouldBeTrue();
        AccesoAuditado lectura = (await AccesosDelCasoAsync(db, caso)).Single(f => f.Accion == "LEER");
        (lectura.RecursoTipo, lectura.Resultado, lectura.TenantId).ShouldBe(("REGISTRO_CLINICO", "EXITO", ArlA));
    }

    [Fact]
    public async Task GetRegistrosClinicos_OnAnInvisibleCase_ShouldLeaveANoEncontradoRow()
    {
        Guid casoB = await InsertCasoAsync(db, ArlB);

        var result = await SendAsync(db, ActorArlA, new GetRegistrosClinicosQuery(new CasoMedicoId(casoB)));

        result.IsFailure.ShouldBeTrue();
        AccesoAuditado fila = (await AccesosDelCasoAsync(db, casoB)).ShouldHaveSingleItem();
        (fila.Accion, fila.RecursoTipo, fila.Resultado).ShouldBe(("LEER", "REGISTRO_CLINICO", "NO_ENCONTRADO"));
        // The actor could not see the case, so the tenant of the resource is unknown.
        fila.TenantId.ShouldBeNull();
        fila.ActorTenantId.ShouldBe(ArlA);
    }

    // ── Hash ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HashRegistro_ShouldBeRecomputable_OnEveryRowRead()
    {
        Guid caso = await InsertCasoAsync(db, ArlA);
        await SendAsync(db, ActorArlA, new RemitirCasoCommand(new CasoMedicoId(caso), Ips));
        await RegistrarAsync(db, ActorIps, caso, Contenido("Con IPS"));
        await SendAsync(db, ActorIps, new GetRegistrosClinicosQuery(new CasoMedicoId(caso)));

        List<AccesoAuditado> filas = await AccesosDelCasoAsync(db, caso);

        filas.Count.ShouldBe(3);
        foreach (AccesoAuditado fila in filas)
        {
            CanonicalAuditHasher.Hash(fila).ShouldBe(fila.HashRegistro, $"{fila.Accion} {fila.RecursoTipo}");
        }
    }

    /// <summary><see cref="ClinicoSql.SendAsync{TResponse}"/> for commands without a payload.</summary>
    private static async Task<Result> EnviarAsync(DatabaseFixture fixture, FakeTenantContext actor, IRequest<Result> request)
    {
        await using AsyncServiceScope scope = fixture.CreateScope(actor);
        return await scope.ServiceProvider.GetRequiredService<ISender>().Send(request);
    }
}

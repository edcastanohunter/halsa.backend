using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Data;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Dictamenes;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Dictamenes;
using Halsa.Backend.Domain.Terminos;
using Halsa.Backend.SharedKernel.Results;
using Microsoft.EntityFrameworkCore;

namespace Halsa.Backend.Application.Clinico.Queries.GetDictamenPcl;

/// <summary>
/// El dictamen PCL vigente del caso (SPEC 08): la parte administrativa (<c>atel.dictamenes_pcl</c>, con
/// <c>halsa_app</c>) y la motivación descifrada de su registro <c>DICTAMEN_PCL</c> (con <c>halsa_clinico</c>). Es la
/// única salida de ese registro; la lectura deja <c>LEER / REGISTRO_CLINICO</c>.
/// </summary>
public sealed record GetDictamenPclQuery(CasoMedicoId CasoId) : IQuery<DictamenPclResponse>, IClinicalQuery
{
    public AccionAuditoria Accion => AccionAuditoria.Leer;

    public ObjetivoAuditoria Objetivo => new(RecursoAuditoria.RegistroClinico, null, CasoId.Value);
}

internal sealed class GetDictamenPclQueryHandler(
    IClinicalCaseGate gate,
    IReadDbContext readDb,
    IClinicalDbContext db,
    IClinicalCipher cipher,
    IAuditScope auditScope)
    : IQueryHandler<GetDictamenPclQuery, DictamenPclResponse>
{
    public async Task<Result<DictamenPclResponse>> Handle(GetDictamenPclQuery query, CancellationToken cancellationToken)
    {
        CasoVisible? caso = await gate.CasoVisibleAsync(query.CasoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<DictamenPclResponse>(ClinicoErrors.CasoNoEncontrado);
        }

        auditScope.TenantDelRecurso(caso.TenantId);

        DictamenPcl? dictamen = await readDb.DictamenesPcl
            .FirstOrDefaultAsync(d => d.CasoMedicoId == query.CasoId && d.CorregidoPor == null, cancellationToken);
        if (dictamen is null)
        {
            return Result.Failure<DictamenPclResponse>(DictamenErrors.NoEncontrado);
        }

        var registroId = new HistoriaClinicaDetalleId(dictamen.RegistroClinicoId);
        HistoriaClinicaDetalle? registro = await db.Registros
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.Id == registroId && r.CasoMedicoId == query.CasoId && r.Tipo == TipoRegistroClinico.DictamenPcl,
                cancellationToken);

        // The administrative row and its clinical record are written in one transaction: one without the other is a bug.
        if (registro is null)
        {
            throw new InvalidOperationException($"El dictamen {dictamen.Id.Value} no tiene su registro DICTAMEN_PCL.");
        }

        byte[] claro = await cipher.DescifrarAsync(
            registro.TenantId, registro.Id.Value, RegistroClinicoEscritura.CampoContenido, registro.Contenido,
            cancellationToken);

        // SPEC 09: the plazo opened by its notification. Only the tenant sees plazos, so for an IPS it stays null.
        Guid dictamenId = dictamen.Id.Value;
        var controversia = await readDb.Plazos
            .Where(p => p.CasoMedicoId == query.CasoId
                        && p.ReglaCodigo == CodigosRegla.ControversiaDictamen
                        && p.ReferenciaId == dictamenId)
            .Select(p => new { p.VenceEn })
            .FirstOrDefaultAsync(cancellationToken);

        return DictamenPclClinico.Respuesta(
            dictamen, registro.Profesional, RegistroClinicoEscritura.Deserializar<ContenidoClinico>(claro),
            controversia is null ? null : CalculadoraTerminos.FechaLocal(controversia.VenceEn));
    }
}

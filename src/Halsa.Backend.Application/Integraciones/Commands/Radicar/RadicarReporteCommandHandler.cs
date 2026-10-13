using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Integraciones.Canonico;
using Halsa.Backend.Application.Integraciones.Contratos;
using Halsa.Backend.Application.Integraciones.Eventos;
using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Application.Integraciones.Commands.Radicar;

/// <summary>
/// El flujo de la SPEC 05: actor, llave, idempotencia, validación, adaptación y una sola transacción con el
/// caso, el reporte, el mensaje cifrado, el <c>REPORTE_INICIAL</c> cifrado, la solicitud, la llave, el outbox
/// y la auditoría. No hay un momento en que el caso exista sin su reporte clínico, ni al revés.
/// </summary>
internal sealed class RadicarReporteCommandHandler(
    ITenantContext tenantContext,
    IEnumerable<IReporteAdapter> adapters,
    IIdempotencyStore idempotencia,
    IRadicadoGenerator radicados,
    IIngestaDbContext db,
    IClinicalCipher clinicalCipher,
    IMensajeCipher mensajeCipher,
    IIngestaAuditTrail auditTrail,
    TimeProvider time)
    : ICommandHandler<RadicarReporteCommand, RadicacionResponse>
{
    /// <summary>Lo que se conserva una <c>Idempotency-Key</c> (contrato).</summary>
    public static readonly TimeSpan VigenciaIdempotencia = TimeSpan.FromHours(72);

    private const int MaxClientId = 100;
    private const int MaxCorrelationId = 64;

    public async Task<Result<RadicacionResponse>> Handle(RadicarReporteCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. Only an ARL integration, which brings its tenant. The permission is checked by the endpoint.
        if (!PuedeRadicar(tenantContext))
        {
            return Result.Failure<RadicacionResponse>(IntegracionErrors.ActorNoAutorizado);
        }

        // 2. The key.
        if (!Guid.TryParse(command.IdempotencyKey, out Guid llave))
        {
            return Result.Failure<RadicacionResponse>(IntegracionErrors.IdempotencyKeyInvalida);
        }

        TenantId tenant = tenantContext.TenantId!.Value;
        string emisor = Emisor(tenant);
        byte[] huella = SHA256.HashData(command.Cuerpo.Span);
        DateTimeOffset ahora = time.GetUtcNow();

        // 3. A live key answers before anything else: same body, the stored response; another body, 409.
        RespuestaIdempotente? previa = await idempotencia.BuscarVigenteAsync(emisor, llave, ahora, cancellationToken);
        if (previa is not null)
        {
            return Repetir(previa, huella);
        }

        // 4 and 5. Format validation and the canonical report.
        Result<JsonObject> cuerpo = LecturaJson.Objeto(command.Cuerpo);
        if (cuerpo.IsFailure)
        {
            return Result.Failure<RadicacionResponse>(cuerpo.Error);
        }

        IReporteAdapter adapter = adapters.Single(a => a.Tipo == command.Tipo);
        Result<ReporteCanonico> reporte = await adapter.AdaptarAsync(cuerpo.Value, tenantContext.Sub, cancellationToken);
        if (reporte.IsFailure)
        {
            return Result.Failure<RadicacionResponse>(reporte.Error);
        }

        // 6. One transaction.
        return await RadicarAsync(command, reporte.Value, tenant, emisor, llave, huella, ahora, cancellationToken);
    }

    private async Task<Result<RadicacionResponse>> RadicarAsync(
        RadicarReporteCommand command,
        ReporteCanonico reporte,
        TenantId tenant,
        string emisor,
        Guid llave,
        byte[] huella,
        DateTimeOffset ahora,
        CancellationToken ct)
    {
        Guid solicitudId = Guid.CreateVersion7();

        // The report keeps its zone (-05:00); timestamptz stores the instant, and Npgsql only writes it in UTC.
        DateTimeOffset fechaEvento = reporte.FechaEvento.ToUniversalTime();

        await using IDbContextTransaction transaccion = await db.BeginTransactionAsync(ct);

        string radicado = await radicados.SiguienteAsync(tenant, ahora, ct);

        Result<CasoMedico> radicacion = CasoMedico.Radicar(
            tenant, radicado, reporte.TipoEvento, reporte.Trabajador, reporte.Empleador,
            fechaEvento, reporte.MunicipioDivipola, reporte.EsGraveOMortal, ahora);

        if (radicacion.IsFailure)
        {
            return Result.Failure<RadicacionResponse>(radicacion.Error);
        }

        CasoMedico caso = radicacion.Value;
        var respuesta = new RadicacionResponse(solicitudId, caso.Id.Value, radicado, EstadosRadicacion.Radicado, ahora, []);

        // 7. Claim the key first: a concurrent request with the same key loses here, and replays the winner.
        bool reclamada = await idempotencia.ReclamarAsync(new SolicitudIdempotente
        {
            Emisor = emisor,
            IdempotencyKey = llave,
            TenantId = tenant.Value,
            CuerpoSha256 = huella,
            SolicitudId = solicitudId,
            Respuesta = JsonSerializer.Serialize(respuesta, LecturaJson.Opciones),
            CreadaEn = ahora,
            ExpiraEn = ahora + VigenciaIdempotencia
        }, ct);

        if (!reclamada)
        {
            await transaccion.RollbackAsync(ct);

            RespuestaIdempotente ganadora = await idempotencia.BuscarVigenteAsync(emisor, llave, ahora, ct)
                ?? throw new InvalidOperationException("La Idempotency-Key estaba reclamada pero no se encontró vigente.");

            return Repetir(ganadora, huella);
        }

        db.Add(caso);
        db.Add(new ReporteEvento
        {
            CasoMedicoId = caso.Id,
            TenantId = tenant,
            Tipo = reporte.Tipo,
            NumeroReporteOrigen = reporte.NumeroReporteOrigen,
            Version = reporte.DatosAdministrativos["version"]!.GetValue<string>(),
            Datos = reporte.DatosAdministrativos.ToJsonString(),
            RecibidoEn = ahora
        });
        db.Add(new SolicitudIntegracion
        {
            Id = solicitudId,
            TenantId = tenant,
            Tipo = reporte.Tipo,
            CasoMedicoId = caso.Id,
            Radicado = radicado,
            RecibidoEn = ahora,
            ClientId = Truncar(command.ClientId, MaxClientId),
            CorrelationId = Truncar(command.CorrelationId, MaxCorrelationId) ?? string.Empty
        });
        db.Add(new MensajeEntrante
        {
            SolicitudId = solicitudId,
            TenantId = tenant,
            Tipo = reporte.Tipo,
            Cuerpo = await mensajeCipher.CifrarAsync(tenant, solicitudId, command.Cuerpo, ct),
            RecibidoEn = ahora,
            RetenerHasta = DateOnly.FromDateTime(ahora.UtcDateTime).AddYears(MensajeEntrante.AniosRetencion)
        });

        Result<HistoriaClinicaDetalle> registro = await ReporteInicialAsync(reporte, tenant, caso.Id, fechaEvento, ahora, ct);
        if (registro.IsFailure)
        {
            return Result.Failure<RadicacionResponse>(registro.Error);
        }

        db.Add(registro.Value);

        // CasoRadicadoDomainEvent becomes the integration event that the asynchronous validation consumes.
        db.Encolar(new CasoRadicadoIntegrationEvent(
            caso.Id.Value, tenant.Value, radicado, reporte.Tipo.Codigo(), reporte.NumeroReporteOrigen, solicitudId));
        caso.ClearDomainEvents();

        auditTrail.Registrar(
            AccionAuditoria.Crear,
            new ObjetivoAuditoria(RecursoAuditoria.Caso, caso.Id.Value.ToString(), caso.Id.Value),
            tenant);
        auditTrail.Registrar(
            AccionAuditoria.Crear,
            new ObjetivoAuditoria(RecursoAuditoria.RegistroClinico, registro.Value.Id.Value.ToString(), caso.Id.Value),
            tenant);

        // 8. The business key (tenant, tipo, numeroReporteOrigen) already radicated under another key.
        ResultadoGuardado guardado = await db.GuardarAsync(ct);

        // An ARL writes only its own tenant: an RLS rejection here is a bug, not an expected outcome.
        if (guardado == ResultadoGuardado.FueraDeAlcance)
        {
            throw new InvalidOperationException("La RLS rechazó una fila de la radicación del propio tenant.");
        }

        if (guardado == ResultadoGuardado.ReporteYaRadicado)
        {
            await transaccion.RollbackAsync(ct);

            string existente = await db.RadicadoDelReporteAsync(reporte.Tipo, reporte.NumeroReporteOrigen, ct)
                ?? throw new InvalidOperationException("El reporte chocó con uno ya radicado que no se encontró.");

            return Result.Failure<RadicacionResponse>(new ReporteYaRadicadoError(existente));
        }

        await transaccion.CommitAsync(ct);

        // 9.
        return Result.Success(respuesta);
    }

    /// <summary>
    /// El <c>REPORTE_INICIAL</c> de la historia clínica (SPEC 03), cifrado como cualquier otro registro. El
    /// diagnóstico principal es el primer código CIE-10, o el primer código si no hay ninguno.
    /// </summary>
    private async Task<Result<HistoriaClinicaDetalle>> ReporteInicialAsync(
        ReporteCanonico reporte, TenantId tenant, CasoMedicoId casoId, DateTimeOffset atendidoEn, DateTimeOffset ahora,
        CancellationToken ct)
    {
        HistoriaClinicaDetalleId id = HistoriaClinicaDetalleId.New();
        IReadOnlyList<CodigoClinico> codigos = reporte.Clinica.Codigos;

        var contenido = new ContenidoClinico(
            Motivo: $"Reporte inicial {reporte.Tipo.Codigo()}",
            Anamnesis: reporte.Clinica.Relato,
            ExamenFisico: null,
            Analisis: null,
            Plan: null,
            Diagnosticos: codigos,
            Observaciones: null);

        CampoCifrado contenidoCifrado = await clinicalCipher.CifrarAsync(
            tenant, id.Value, RegistroClinicoEscritura.CampoContenido, RegistroClinicoEscritura.Serializar(contenido), ct);

        CodigoClinico? principal =
            codigos.FirstOrDefault(c => string.Equals(c.Sistema, ValoresV1.SistemaCie10, StringComparison.OrdinalIgnoreCase))
            ?? codigos.FirstOrDefault();

        CampoCifrado? diagnostico = null;
        byte[]? indiceCiego = null;

        if (principal is not null)
        {
            diagnostico = await clinicalCipher.CifrarAsync(
                tenant, id.Value, RegistroClinicoEscritura.CampoDiagnostico, RegistroClinicoEscritura.Serializar(principal), ct);
            indiceCiego = await clinicalCipher.IndiceCiegoAsync(tenant, principal, ct);
        }

        return HistoriaClinicaDetalle.Registrar(
            id, tenant, casoId, TipoRegistroClinico.ReporteInicial, reporte.Clinica.Autor,
            atendidoEn, contenidoCifrado, diagnostico, indiceCiego, corrigeA: null, ahora);
    }

    internal static bool PuedeRadicar(ITenantContext tenantContext) =>
        tenantContext.Resuelto && tenantContext.Tipo == ActorTipo.Integracion && tenantContext.TenantId is not null;

    /// <summary>El emisor de una ARL en la idempotencia: <c>tenant:{ARL}</c>.</summary>
    internal static string Emisor(TenantId tenant) => $"tenant:{tenant.Value}";

    private static Result<RadicacionResponse> Repetir(RespuestaIdempotente previa, byte[] huella)
    {
        if (!CryptographicOperations.FixedTimeEquals(previa.CuerpoSha256, huella))
        {
            return Result.Failure<RadicacionResponse>(IntegracionErrors.IdempotenciaConflicto);
        }

        RadicacionResponse respuesta = JsonSerializer.Deserialize<RadicacionResponse>(previa.Respuesta, LecturaJson.Opciones)
            ?? throw new JsonException("La respuesta guardada de la Idempotency-Key está vacía.");

        return Result.Success(respuesta);
    }

    private static string? Truncar(string? valor, int max) =>
        string.IsNullOrEmpty(valor) ? null : valor.Length <= max ? valor : valor[..max];
}

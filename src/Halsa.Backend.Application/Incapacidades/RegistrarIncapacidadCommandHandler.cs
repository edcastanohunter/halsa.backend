using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using FluentValidation.Results;
using Halsa.Backend.Application.Abstractions.Archivos;
using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Application.Abstractions.Incapacidades;
using Halsa.Backend.Application.Abstractions.Integraciones;
using Halsa.Backend.Application.Abstractions.Messaging;
using Halsa.Backend.Application.Abstractions.Tenancy;
using Halsa.Backend.Application.Clinico;
using Halsa.Backend.Application.Integraciones;
using Halsa.Backend.Application.Integraciones.Commands.Radicar;
using Halsa.Backend.Application.Integraciones.Contratos;
using Halsa.Backend.Application.Integraciones.Registros;
using Halsa.Backend.Domain.Casos;
using Halsa.Backend.Domain.Clinico;
using Halsa.Backend.Domain.Incapacidades;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.EntityFrameworkCore.Storage;

namespace Halsa.Backend.Application.Incapacidades;

/// <summary>
/// La carga de una incapacidad (SPEC 07), después de lo que el endpoint ya comprobó: el actor, la idempotencia, el
/// formato de <c>datos</c>, el antivirus, el archivo cifrado (fuera de la transacción: un fallo posterior deja un
/// huérfano cifrado, que limpia la SPEC 09) y una sola transacción de <c>halsa_ingesta</c> con el caso bloqueado, las
/// reglas, la historia clínica, el soporte, la incapacidad, la llave, la auditoría y el outbox.
/// </summary>
internal sealed class RegistrarIncapacidadCommandHandler(
    ITenantContext tenantContext,
    IValidator<IncapacidadRequest> validator,
    IIdempotencyStore idempotencia,
    IIncapacidadesDbContext db,
    IClinicalCipher cipher,
    IAntivirusScanner antivirus,
    IFileStore archivos,
    IIngestaAuditTrail auditTrail,
    IncapacidadesOptions opciones,
    TimeProvider time)
    : ICommandHandler<RegistrarIncapacidadCommand, IncapacidadResponse>
{
    /// <summary>La categoría CIE-10: los tres primeros caracteres del código (<c>S62</c> de <c>S62.6</c>).</summary>
    public const int LargoCategoria = 3;

    /// <summary>Prefijo de las rutas de los errores de formato: los campos están en la parte <c>datos</c>.</summary>
    public const string ParteDatos = "datos";

    private static readonly TimeSpan ZonaColombia = TimeSpan.FromHours(-5);

    public async Task<Result<IncapacidadResponse>> Handle(RegistrarIncapacidadCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        // 1. A person of an ARL or an IPS (with the professional of the token), or the integration of an IPS.
        string? emisor = Emisor(tenantContext);
        if (emisor is null)
        {
            return Result.Failure<IncapacidadResponse>(IncapacidadCargaErrors.ActorNoAutorizado);
        }

        bool integracion = tenantContext.Tipo == ActorTipo.Integracion;
        if (!integracion && command.ProfesionalDelToken is null)
        {
            return Result.Failure<IncapacidadResponse>(ClinicoErrors.ProfesionalSinRegistro);
        }

        // 2. The key, and a live one answers before anything else is read, scanned or stored.
        if (!Guid.TryParse(command.IdempotencyKey, out Guid llave))
        {
            return Result.Failure<IncapacidadResponse>(IntegracionErrors.IdempotencyKeyInvalida);
        }

        byte[] huella = Huella(command.Datos.Span, command.Soporte.Span);
        DateTimeOffset ahora = time.GetUtcNow();

        RespuestaIdempotente? previa = await idempotencia.BuscarVigenteAsync(emisor, llave, ahora, cancellationToken);
        if (previa is not null)
        {
            return Repetir(previa, huella);
        }

        // 3. The format of datos.
        Result<IncapacidadRequest> datos = await LeerAsync(command.Datos, integracion, cancellationToken);
        if (datos.IsFailure)
        {
            return Result.Failure<IncapacidadResponse>(datos.Error);
        }

        // 4. The antivirus, synchronous and failing closed.
        ResultadoAntivirus analisis = await antivirus.AnalizarAsync(new MemoryStream(command.Soporte.ToArray()), cancellationToken);
        switch (analisis)
        {
            case ResultadoAntivirus.InfectadoResultado:
                return Result.Failure<IncapacidadResponse>(IncapacidadCargaErrors.SoporteInfectado);
            case not ResultadoAntivirus.LimpioResultado:
                return Result.Failure<IncapacidadResponse>(IncapacidadCargaErrors.AntivirusNoDisponible);
        }

        // 5. The case decides the tenant whose DEKs encrypt everything. Read again, locked, in the transaction.
        CasoMedicoId casoId = command.CasoId;
        CasoVisible? caso = await db.CasoVisibleAsync(casoId, cancellationToken);
        if (caso is null)
        {
            return Result.Failure<IncapacidadResponse>(IncapacidadCargaErrors.CasoNoEncontrado);
        }

        // 6. The clinical record and the support's file, before the transaction.
        Carga carga = Preparar(datos.Value, command);

        Result<HistoriaClinicaDetalle> registro = await RegistroClinicoEscritura.CifrarAsync(
            caso.TenantId, casoId, TipoRegistroClinico.CertificadoIncapacidad, carga.FirmaClinica, carga.AtendidoEn,
            carga.Contenido, corrigeA: null, cipher, ahora, cancellationToken);
        if (registro.IsFailure)
        {
            return Result.Failure<IncapacidadResponse>(registro.Error);
        }

        byte[] categoria = await cipher.IndiceCiegoAsync(caso.TenantId, carga.Categoria, cancellationToken);

        Guid soporteId = Guid.CreateVersion7();
        ArchivoGuardado archivo = await archivos.GuardarAsync(
            caso.TenantId, soporteId, new MemoryStream(command.Soporte.ToArray()), cancellationToken);

        // 7. One transaction.
        return await RegistrarAsync(
            command, carga, casoId, registro.Value, categoria, soporteId, archivo, emisor, llave, huella, ahora,
            cancellationToken);
    }

    private async Task<Result<IncapacidadResponse>> RegistrarAsync(
        RegistrarIncapacidadCommand command,
        Carga carga,
        CasoMedicoId casoId,
        HistoriaClinicaDetalle registro,
        byte[] categoria,
        Guid soporteId,
        ArchivoGuardado archivo,
        string emisor,
        Guid llave,
        byte[] huella,
        DateTimeOffset ahora,
        CancellationToken ct)
    {
        ParametrosIncapacidad parametros = opciones.Parametros();

        await using IDbContextTransaction transaccion = await db.BeginTransactionAsync(ct);

        await db.BloquearCasoAsync(casoId, ct);

        CasoVisible? caso = await db.CasoVisibleAsync(casoId, ct);
        if (caso is null || caso.TenantId != registro.TenantId)
        {
            return Result.Failure<IncapacidadResponse>(IncapacidadCargaErrors.CasoNoEncontrado);
        }

        IReadOnlyList<Incapacidad> delCaso = await db.IncapacidadesDelCasoAsync(casoId, ct);

        Result<Incapacidad> incapacidad = Incapacidad.Registrar(
            carga.Datos(casoId, categoria, soporteId, registro.Id.Value, tenantContext.Sub, command.ClientId),
            new CasoParaIncapacidad(caso.TenantId, caso.FechaEvento, caso.Estado),
            delCaso,
            parametros,
            ahora);
        if (incapacidad.IsFailure)
        {
            return Result.Failure<IncapacidadResponse>(incapacidad.Error);
        }

        IncapacidadResponse respuesta = IncapacidadResponse.Desde(incapacidad.Value, parametros);

        // A concurrent request with the same key loses here, and replays the winner.
        bool reclamada = await idempotencia.ReclamarAsync(new SolicitudIdempotente
        {
            Emisor = emisor,
            IdempotencyKey = llave,
            TenantId = caso.TenantId.Value,
            CuerpoSha256 = huella,
            SolicitudId = respuesta.Id,
            Respuesta = JsonSerializer.Serialize(respuesta, LecturaJson.Opciones),
            CreadaEn = ahora,
            ExpiraEn = ahora + RadicarReporteCommandHandler.VigenciaIdempotencia
        }, ct);

        if (!reclamada)
        {
            await transaccion.RollbackAsync(ct);

            RespuestaIdempotente ganadora = await idempotencia.BuscarVigenteAsync(emisor, llave, ahora, ct)
                ?? throw new InvalidOperationException("La Idempotency-Key estaba reclamada pero no se encontró vigente.");

            return Repetir(ganadora, huella);
        }

        db.Add(registro);
        db.Add(Soporte.Registrar(
            soporteId, caso.TenantId, casoId, TipoSoporte.Incapacidad, archivo.Ruta, archivo.KeyId,
            archivo.Sha256Claro, archivo.Tamano, ahora));
        db.Add(incapacidad.Value);

        auditTrail.Registrar(
            AccionAuditoria.Crear,
            new ObjetivoAuditoria(RecursoAuditoria.Incapacidad, respuesta.Id.ToString(), casoId.Value),
            caso.TenantId);
        auditTrail.Registrar(AccionAuditoria.Crear, RegistroClinicoEscritura.Objetivo(registro), caso.TenantId);

        db.Encolar(new IncapacidadRegistradaIntegrationEvent(
            respuesta.Id, casoId.Value, caso.TenantId.Value, respuesta.Tipo, respuesta.FechaInicio, respuesta.FechaFin,
            respuesta.Dias, respuesta.DiasAcumulados, [.. respuesta.Alertas.Select(a => a.Code)]));

        switch (await db.GuardarAsync(ct))
        {
            case ResultadoGuardadoIncapacidad.CertificadoDuplicado:
                await transaccion.RollbackAsync(ct);
                return Result.Failure<IncapacidadResponse>(IncapacidadCargaErrors.CertificadoDuplicado);

            case ResultadoGuardadoIncapacidad.FueraDeAlcance:
                await transaccion.RollbackAsync(ct);
                return Result.Failure<IncapacidadResponse>(IncapacidadCargaErrors.CasoNoEncontrado);
        }

        await transaccion.CommitAsync(ct);

        return Result.Success(respuesta);
    }

    private async Task<Result<IncapacidadRequest>> LeerAsync(ReadOnlyMemory<byte> datos, bool integracion, CancellationToken ct)
    {
        Result<JsonObject> objeto = LecturaJson.Objeto(datos);
        if (objeto.IsFailure)
        {
            return Result.Failure<IncapacidadRequest>(EnDatos(objeto.Error));
        }

        Result<IncapacidadRequest> leido = LecturaJson.Leer<IncapacidadRequest>(objeto.Value);
        if (leido.IsFailure)
        {
            return Result.Failure<IncapacidadRequest>(EnDatos(leido.Error));
        }

        ValidationResult validacion = await validator.ValidateAsync(leido.Value, ct);

        // The integration signs for a doctor of its own: it must say who. The backoffice's comes from the token.
        if (integracion && leido.Value.Profesional is null)
        {
            validacion.Errors.Add(new ValidationFailure(nameof(IncapacidadRequest.Profesional), MensajesV1.Requerido));
        }

        return validacion.IsValid
            ? leido
            : Result.Failure<IncapacidadRequest>(EnDatos(LecturaJson.Errores(validacion)));
    }

    /// <summary>Las rutas de los errores, dentro de la parte: <c>fechaInicio</c> → <c>datos.fechaInicio</c>.</summary>
    private static Error EnDatos(Error error) => error is ValidationError validacion
        ? IntegracionErrors.Validacion(validacion.Errors.OfType<ErrorDeCampo>().Select(e => new ErrorDeCampo(
            e.Ruta == LecturaJson.RutaRaiz ? ParteDatos : $"{ParteDatos}.{e.Ruta}", e.Description)))
        : error;

    /// <summary>
    /// <c>ips:{REPS}</c> para la integración de una IPS, <c>sub:{sub}</c> para una persona (se amplía el formato de la
    /// SPEC 05). Cualquier otro actor no carga incapacidades.
    /// </summary>
    internal static string? Emisor(ITenantContext tenantContext)
    {
        if (!tenantContext.Resuelto)
        {
            return null;
        }

        return tenantContext.Tipo switch
        {
            ActorTipo.Integracion when tenantContext.IpsId is IpsId ips => $"ips:{ips.Value}",
            ActorTipo.Arl when tenantContext.TenantId is not null => $"sub:{tenantContext.Sub}",
            ActorTipo.Ips when tenantContext.IpsId is not null => $"sub:{tenantContext.Sub}",
            _ => null
        };
    }

    /// <summary>
    /// SHA-256 de <c>SHA-256(datos) ‖ SHA-256(soporte)</c>: el mismo cuerpo es la misma parte <c>datos</c> y el mismo PDF,
    /// sin importar el <c>boundary</c> del multipart, que cambia en cada envío.
    /// </summary>
    internal static byte[] Huella(ReadOnlySpan<byte> datos, ReadOnlySpan<byte> soporte)
    {
        Span<byte> partes = stackalloc byte[2 * SHA256.HashSizeInBytes];
        SHA256.HashData(datos, partes[..SHA256.HashSizeInBytes]);
        SHA256.HashData(soporte, partes[SHA256.HashSizeInBytes..]);
        return SHA256.HashData(partes);
    }

    private static Result<IncapacidadResponse> Repetir(RespuestaIdempotente previa, byte[] huella)
    {
        if (!CryptographicOperations.FixedTimeEquals(previa.CuerpoSha256, huella))
        {
            return Result.Failure<IncapacidadResponse>(IntegracionErrors.IdempotenciaConflicto);
        }

        IncapacidadResponse respuesta =
            JsonSerializer.Deserialize<IncapacidadResponse>(previa.Respuesta, LecturaJson.Opciones)
            ?? throw new JsonException("La respuesta guardada de la Idempotency-Key está vacía.");

        return Result.Success(respuesta);
    }

    /// <summary>Los datos ya validados, traducidos al dominio y a la historia clínica.</summary>
    private Carga Preparar(IncapacidadRequest datos, RegistrarIncapacidadCommand command)
    {
        if (!CodigosIncapacidad.TryParse(datos.Tipo, out TipoIncapacidad tipo) ||
            !CodigosIncapacidad.TryParse(datos.Modalidad, out ModalidadIncapacidad modalidad))
        {
            throw new InvalidOperationException("El tipo y la modalidad se validan antes de preparar la carga.");
        }

        // The integration's doctor comes in the body and the service account signs; the backoffice's, from the token.
        (ProfesionalRef firma, ProfesionalIncapacidad profesional) = command.ProfesionalDelToken is { } token
            ? (token.Ref, new ProfesionalIncapacidad(
                token.Ref.TipoDocumento, token.Ref.NumeroDocumento, token.Nombre, token.Ref.RegistroRethus!))
            : (new ProfesionalRef(
                    tenantContext.Sub,
                    datos.Profesional!.Identificacion!.Tipo!,
                    datos.Profesional.Identificacion.Numero!,
                    datos.Profesional.RegistroRethus),
                new ProfesionalIncapacidad(
                    datos.Profesional.Identificacion.Tipo!,
                    datos.Profesional.Identificacion.Numero!,
                    datos.Profesional.NombreCompleto,
                    datos.Profesional.RegistroRethus!));

        CodificadoV1 principal = datos.DiagnosticoPrincipal!;
        IEnumerable<CodificadoV1> todos = [principal, .. datos.DiagnosticosRelacionados ?? []];

        var contenido = new ContenidoClinico(
            Motivo: null, Anamnesis: null, ExamenFisico: null, Analisis: null, Plan: null,
            [.. todos.Select(d => new CodigoClinico(d.Sistema!, d.Codigo!))],
            datos.Observaciones);

        DateOnly fechaInicio = datos.FechaInicio!.Value;

        return new Carga(
            datos,
            tipo,
            modalidad,
            firma,
            profesional,
            // 00:00 in Colombia, stored in UTC like every timestamptz.
            new DateTimeOffset(fechaInicio.ToDateTime(TimeOnly.MinValue), ZonaColombia).ToUniversalTime(),
            contenido,
            new CodigoClinico(principal.Sistema!, principal.Codigo![..LargoCategoria]));
    }

    /// <param name="FirmaClinica">Quién firma el <c>CERTIFICADO_INCAPACIDAD</c> de la historia clínica.</param>
    /// <param name="AtendidoEn">La fecha de inicio a las 00:00 de Colombia.</param>
    /// <param name="Categoria">La categoría del diagnóstico principal, para el índice ciego.</param>
    private sealed record Carga(
        IncapacidadRequest Request,
        TipoIncapacidad Tipo,
        ModalidadIncapacidad Modalidad,
        ProfesionalRef FirmaClinica,
        ProfesionalIncapacidad Profesional,
        DateTimeOffset AtendidoEn,
        ContenidoClinico Contenido,
        CodigoClinico Categoria)
    {
        public DatosIncapacidad Datos(
            CasoMedicoId casoId, byte[] categoria, Guid soporteId, Guid registroClinicoId, string sub, string? clientId) =>
            new(
                IncapacidadId.New(),
                casoId,
                Request.NumeroCertificado!,
                new IpsEmisora(Request.IpsEmisora!.CodigoReps!, Request.IpsEmisora.Nombre),
                Tipo,
                Request.ProrrogaDe is Guid prorrogaDe ? new IncapacidadId(prorrogaDe) : null,
                Request.FechaInicio!.Value,
                Request.Dias!.Value,
                Modalidad,
                Profesional,
                categoria,
                soporteId,
                registroClinicoId,
                sub,
                clientId);
    }
}

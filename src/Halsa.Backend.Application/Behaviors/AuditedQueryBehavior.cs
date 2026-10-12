using Halsa.Backend.Application.Abstractions.Auditoria;
using Halsa.Backend.Mediation;
using Halsa.Backend.SharedKernel.Results;
using Halsa.Backend.SharedKernel.Tenancy;
using Microsoft.Extensions.Logging;

namespace Halsa.Backend.Application.Behaviors;

/// <summary>
/// Audita cada <see cref="IAuditedQuery"/> después de ejecutarla y falla cerrado: si la fila no se
/// confirma, la respuesta se descarta y se devuelve <see cref="AuditoriaErrors.NoRegistrada"/>.
/// La fila se escribe con <see cref="IAuditWriter"/>, en un contexto propio: una lectura no tiene un
/// cambio con el que confirmarse.
/// </summary>
public sealed class AuditedQueryBehavior<TRequest, TResponse>(
    IAuditWriter auditWriter,
    AuditScope auditScope,
    ILogger<AuditedQueryBehavior<TRequest, TResponse>> logger)
    : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>, IAuditedQuery
    where TResponse : Result
{
    private static readonly string RequestName = typeof(TRequest).Name;

    public async Task<TResponse> Handle(
        TRequest request,
        RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        TResponse response = await next(cancellationToken).ConfigureAwait(false);

        try
        {
            await auditWriter
                .RegistrarAsync(request.Accion, request.Objetivo, auditScope.Tenant, Clasificar(response), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "Could not audit {RequestName}; the response was discarded", RequestName);

            return ResultFactory.Failure<TResponse>(AuditoriaErrors.NoRegistrada);
        }

        return response;
    }

    private static ResultadoAuditoria Clasificar(Result response) => response switch
    {
        { IsSuccess: true } => ResultadoAuditoria.Exito,
        { Error.Type: ErrorType.NotFound } => ResultadoAuditoria.NoEncontrado,
        { Error.Type: ErrorType.Forbidden } => ResultadoAuditoria.Denegado,
        _ => ResultadoAuditoria.Error
    };
}

/// <summary>
/// Lo que el handler informa por <see cref="IAuditScope"/> y el behavior lee al registrar. Scoped:
/// vive lo que dura el request.
/// </summary>
public sealed class AuditScope : IAuditScope
{
    public TenantId? Tenant { get; private set; }

    public void TenantDelRecurso(TenantId tenant) => Tenant = tenant;
}

using System.Formats.Asn1;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Halsa.Backend.Infrastructure.Observabilidad;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Quartz;

namespace Halsa.Backend.Infrastructure.Worker.Jobs;

/// <summary>
/// SPEC 13: cada hora revisa lo que, al vencer, tumba el borde entero, y avisa antes:
/// <list type="bullet">
///   <item><see cref="Alertas.CrlPorVencer"/>: una CRL de la CA de integraciones (<see cref="ObservabilidadOptions.CrlPath"/>,
///   intermedia y raíz) vence en menos de 48 h. Con una CRL vencida, Nginx rechaza todos los certificados cliente.</item>
///   <item><see cref="Alertas.TlsPorVencer"/>: el certificado TLS de un host público vence en menos de 14 días (Let's
///   Encrypt no renovó).</item>
/// </list>
/// No poder leer la CRL o el certificado también avisa: falla cerrado.
/// </summary>
[DisallowConcurrentExecution]
internal sealed partial class VigenciaCertificadosJob(
    IOptions<ObservabilidadOptions> options,
    TimeProvider time,
    ILogger<VigenciaCertificadosJob> logger) : IJob
{
    public static readonly JobKey Key = new("vigencia-certificados", TrabajosWorker.Grupo);

    public async Task Execute(IJobExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RevisarCrl();
        await RevisarTlsAsync(context.CancellationToken);
    }

    /// <summary>Devuelve cuántas alertas emitió (para los tests).</summary>
    internal int RevisarCrl()
    {
        ObservabilidadOptions valor = options.Value;

        if (string.IsNullOrWhiteSpace(valor.CrlPath))
        {
            return 0;
        }

        IReadOnlyList<DateTimeOffset> vencimientos;
        try
        {
            vencimientos = Crl.ProximasActualizaciones(File.ReadAllText(valor.CrlPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or AsnContentException or FormatException)
        {
            LogIlegible(logger, Alertas.CrlPorVencer, valor.CrlPath, ex.Message);
            return 1;
        }

        if (vencimientos.Count == 0)
        {
            LogIlegible(logger, Alertas.CrlPorVencer, valor.CrlPath, "no contiene ninguna CRL con nextUpdate");
            return 1;
        }

        DateTimeOffset primera = vencimientos.Min();
        TimeSpan restante = primera - time.GetUtcNow();

        if (restante >= valor.UmbralCrl)
        {
            return 0;
        }

        LogPorVencer(logger, Alertas.CrlPorVencer, valor.CrlPath, primera, Math.Round(restante.TotalHours, 1));
        return 1;
    }

    internal async Task<int> RevisarTlsAsync(CancellationToken ct)
    {
        ObservabilidadOptions valor = options.Value;
        int alertas = 0;

        foreach (string host in valor.HostsTls.Where(h => !string.IsNullOrWhiteSpace(h)))
        {
            DateTimeOffset? vence = await VencimientoTlsAsync(host, ct);

            if (vence is null)
            {
                alertas++;
                continue;
            }

            TimeSpan restante = vence.Value - time.GetUtcNow();
            if (restante < valor.UmbralTls)
            {
                LogPorVencer(logger, Alertas.TlsPorVencer, host, vence.Value, Math.Round(restante.TotalHours, 1));
                alertas++;
            }
        }

        return alertas;
    }

    /// <summary>El <c>notAfter</c> del certificado que presenta <paramref name="host"/>:443; <c>null</c> si no se pudo leer.</summary>
    private async Task<DateTimeOffset?> VencimientoTlsAsync(string host, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            using var tcp = new TcpClient();
            await tcp.ConnectAsync(host, 443, timeout.Token);

            X509Certificate2? certificado = null;
            await using var ssl = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false, (_, cert, _, _) =>
            {
                // Read, not trust: an expired or wrong certificate is exactly what this job reports.
                certificado = cert is null ? null : new X509Certificate2(cert);
                return true;
            });
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, timeout.Token);

            using (certificado)
            {
                return certificado is null ? null : new DateTimeOffset(certificado.NotAfter.ToUniversalTime());
            }
        }
        catch (Exception ex) when (ex is SocketException or IOException or OperationCanceledException or System.Security.Authentication.AuthenticationException)
        {
            if (ct.IsCancellationRequested)
            {
                throw;
            }

            LogIlegible(logger, Alertas.TlsPorVencer, host, ex.Message);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Alerta {Alerta}: {Objeto} vence el {Vence:O} (en {Horas} h)")]
    private static partial void LogPorVencer(ILogger logger, string alerta, string objeto, DateTimeOffset vence, double horas);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Alerta {Alerta}: no se pudo leer la vigencia de {Objeto}: {Motivo}")]
    private static partial void LogIlegible(ILogger logger, string alerta, string objeto, string motivo);
}

/// <summary>Lo mínimo de una CRL X.509 (RFC 5280 §5.1) para saber cuándo vence: su <c>nextUpdate</c>.</summary>
internal static class Crl
{
    private const string Inicio = "-----BEGIN X509 CRL-----";
    private const string Fin = "-----END X509 CRL-----";

    /// <summary>El <c>nextUpdate</c> de cada CRL del archivo PEM (puede traer varias concatenadas).</summary>
    public static IReadOnlyList<DateTimeOffset> ProximasActualizaciones(string pem)
    {
        ArgumentNullException.ThrowIfNull(pem);

        var resultado = new List<DateTimeOffset>();
        int desde = 0;

        while ((desde = pem.IndexOf(Inicio, desde, StringComparison.Ordinal)) >= 0)
        {
            int hasta = pem.IndexOf(Fin, desde, StringComparison.Ordinal);
            if (hasta < 0)
            {
                throw new FormatException("Una CRL del PEM no tiene su línea END.");
            }

            string base64 = pem[(desde + Inicio.Length)..hasta];
            DateTimeOffset? proxima = ProximaActualizacion(Convert.FromBase64String(base64));
            if (proxima is not null)
            {
                resultado.Add(proxima.Value);
            }

            desde = hasta + Fin.Length;
        }

        return resultado;
    }

    /// <summary>
    /// <c>CertificateList ::= SEQUENCE { tbsCertList, signatureAlgorithm, signatureValue }</c> y
    /// <c>TBSCertList ::= SEQUENCE { version INTEGER OPTIONAL, signature, issuer, thisUpdate Time, nextUpdate Time OPTIONAL, ... }</c>.
    /// </summary>
    public static DateTimeOffset? ProximaActualizacion(byte[] der)
    {
        var lista = new AsnReader(der, AsnEncodingRules.DER).ReadSequence();
        AsnReader tbs = lista.ReadSequence();

        if (tbs.PeekTag().HasSameClassAndValue(Asn1Tag.Integer))
        {
            tbs.ReadInteger();
        }

        tbs.ReadSequence();   // signature
        tbs.ReadSequence();   // issuer
        LeerTiempo(tbs);      // thisUpdate

        return tbs.HasData && EsTiempo(tbs.PeekTag()) ? LeerTiempo(tbs) : null;
    }

    private static bool EsTiempo(Asn1Tag tag) =>
        tag.HasSameClassAndValue(Asn1Tag.UtcTime) || tag.HasSameClassAndValue(Asn1Tag.GeneralizedTime);

    private static DateTimeOffset LeerTiempo(AsnReader reader) =>
        reader.PeekTag().HasSameClassAndValue(Asn1Tag.UtcTime)
            ? reader.ReadUtcTime()
            : reader.ReadGeneralizedTime();
}

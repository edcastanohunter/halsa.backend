using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Halsa.Backend.Infrastructure.Observabilidad;
using Halsa.Backend.Infrastructure.Worker.Jobs;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Halsa.Backend.UnitTests.Observabilidad;

/// <summary>
/// SPEC 13, paso 9: la alerta de una CRL a menos de 48 h de vencer. Con la CRL vencida Nginx rechaza todas las
/// integraciones, así que también avisa si no la puede leer.
/// </summary>
public sealed class VigenciaCertificadosJobTests : IDisposable
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _archivo = Path.Combine(Path.GetTempPath(), $"halsa-crl-{Guid.NewGuid():N}.crl");

    public void Dispose() => File.Delete(_archivo);

    [Fact]
    public void Crl_ShouldReadTheNextUpdateOfEveryCrlInThePem()
    {
        DateTimeOffset intermedia = Ahora.AddDays(10);
        DateTimeOffset raiz = Ahora.AddDays(365);

        IReadOnlyList<DateTimeOffset> vencimientos = Crl.ProximasActualizaciones(CrlPem(intermedia) + CrlPem(raiz));

        vencimientos.ShouldBe([intermedia, raiz]);
    }

    [Fact]
    public void FarFromExpiring_ShouldNotAlert()
    {
        File.WriteAllText(_archivo, CrlPem(Ahora.AddDays(10)) + CrlPem(Ahora.AddDays(365)));

        Job(_archivo).RevisarCrl().ShouldBe(0);
    }

    [Theory]
    [InlineData(47)]
    [InlineData(1)]
    [InlineData(-5)]
    public void WithinTheThreshold_OrExpired_ShouldAlert(int horas)
    {
        // The earliest one decides: here the intermediate's, the root's is a year away.
        File.WriteAllText(_archivo, CrlPem(Ahora.AddHours(horas)) + CrlPem(Ahora.AddDays(365)));

        Job(_archivo).RevisarCrl().ShouldBe(1);
    }

    [Fact]
    public void AnUnreadableCrl_ShouldAlert()
    {
        File.WriteAllText(_archivo, "-----BEGIN X509 CRL-----\nno-es-base64\n-----END X509 CRL-----\n");

        Job(_archivo).RevisarCrl().ShouldBe(1);
    }

    [Fact]
    public void AMissingCrl_ShouldAlert() =>
        Job(Path.Combine(Path.GetTempPath(), $"no-existe-{Guid.NewGuid():N}.crl")).RevisarCrl().ShouldBe(1);

    [Fact]
    public void WithoutAPath_ShouldDoNothing() =>
        Job(crlPath: null).RevisarCrl().ShouldBe(0);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static VigenciaCertificadosJob Job(string? crlPath) => new(
        Options.Create(new ObservabilidadOptions { CrlPath = crlPath }),
        new FakeTimeProvider(Ahora),
        NullLogger<VigenciaCertificadosJob>.Instance);

    /// <summary>A CRL signed by a throwaway CA, as openssl ca -gencrl would write it.</summary>
    private static string CrlPem(DateTimeOffset nextUpdate)
    {
        using var llave = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var solicitud = new CertificateRequest("CN=Halsa Integraciones de prueba", llave, HashAlgorithmName.SHA384);
        solicitud.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        solicitud.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        solicitud.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(solicitud.PublicKey, false));
        using X509Certificate2 ca = solicitud.CreateSelfSigned(Ahora.AddYears(-1), Ahora.AddYears(3));

        byte[] der = new CertificateRevocationListBuilder().Build(
            ca, BigInteger.One, nextUpdate, HashAlgorithmName.SHA384, thisUpdate: Ahora.AddDays(-1));

        return PemEncoding.WriteString("X509 CRL", der) + "\n";
    }
}

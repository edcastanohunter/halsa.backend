namespace Halsa.Backend.Infrastructure.Observabilidad;

/// <summary>
/// Sección <c>Observabilidad</c> (SPEC 13). Lo que el Worker vigila y avisa por fuera de las métricas: el heartbeat hacia
/// el monitor externo y las señales que Seq convierte en alertas por correo (runbook 07).
/// </summary>
public sealed class ObservabilidadOptions
{
    public const string SectionName = "Observabilidad";

    /// <summary>
    /// El <em>dead man's switch</em> externo: <see cref="Worker.Jobs.HeartbeatJob"/> le hace un <c>GET</c> cada 5 minutos.
    /// Si deja de llegar, el servicio externo alerta: el VPS no puede avisar de su propia caída. Vacío lo desactiva.
    /// </summary>
    public string? HeartbeatUrl { get; set; }

    /// <summary>
    /// La CRL que monta Nginx (intermedia + raíz). Si una vence, Nginx rechaza todas las integraciones; se alerta
    /// <see cref="UmbralCrl"/> antes. Vacío lo desactiva.
    /// </summary>
    public string? CrlPath { get; set; }

    public TimeSpan UmbralCrl { get; set; } = TimeSpan.FromHours(48);

    /// <summary>Los hosts públicos cuyo certificado TLS (Let's Encrypt) se revisa, p. ej. <c>api.halsa.co</c>.</summary>
    public string[] HostsTls { get; set; } = [];

    public TimeSpan UmbralTls { get; set; } = TimeSpan.FromDays(14);
}

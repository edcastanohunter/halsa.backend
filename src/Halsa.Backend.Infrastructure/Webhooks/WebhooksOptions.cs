using Halsa.Backend.Infrastructure.Integraciones;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Webhooks;

/// <summary>La sección <c>Webhooks</c> (SPEC 10).</summary>
public sealed class WebhooksOptions
{
    public const string SectionName = "Webhooks";

    /// <summary>
    /// Acepta <c>http://</c> y los destinos locales (loopback y redes privadas). Solo para los receptores de prueba de
    /// Development y Testing: en cualquier otro entorno el arranque falla si está activado.
    /// </summary>
    public bool PermitirDestinosLocales { get; set; }

    /// <summary>Cada cuánto da una vuelta el dispatcher.</summary>
    public int IntervaloSegundos { get; set; } = 5;

    /// <summary>Las entregas que toma cada vuelta.</summary>
    public int Lote { get; set; } = 50;

    /// <summary>Los envíos simultáneos de una vuelta: un receptor lento no agota los hilos.</summary>
    public int MaxParalelo { get; set; } = 8;

    /// <summary>El timeout de cada envío.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// Se ejecuta al arrancar (<c>ValidateOnStart</c>): <see cref="WebhooksOptions.PermitirDestinosLocales"/> abriría el SSRF
/// que el cliente cierra, así que fuera de Development y Testing el proceso no arranca con él.
/// </summary>
internal sealed class WebhooksOptionsValidator(string? environmentName) : IValidateOptions<WebhooksOptions>
{
    public ValidateOptionsResult Validate(string? name, WebhooksOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.IntervaloSegundos <= 0 || options.Lote <= 0 || options.MaxParalelo <= 0 || options.Timeout <= TimeSpan.Zero)
        {
            return ValidateOptionsResult.Fail("Webhooks:IntervaloSegundos, Lote, MaxParalelo y Timeout deben ser positivos.");
        }

        // The same environments that admit the affiliation stub: the ones with nothing real behind them.
        if (options.PermitirDestinosLocales && !DevAfiliacionGateway.Admitido(environmentName))
        {
            return ValidateOptionsResult.Fail(
                $"Webhooks:PermitirDestinosLocales=true solo se admite en Development y Testing, no en '{environmentName}'.");
        }

        return ValidateOptionsResult.Success;
    }
}

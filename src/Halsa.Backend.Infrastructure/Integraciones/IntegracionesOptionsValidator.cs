using Halsa.Backend.Application.Integraciones;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Integraciones;

/// <summary>Se ejecuta al arrancar (<c>ValidateOnStart</c>): la ventana de duplicados debe ser positiva.</summary>
internal sealed class IntegracionesOptionsValidator : IValidateOptions<IntegracionesOptions>
{
    public ValidateOptionsResult Validate(string? name, IntegracionesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        return options.VentanaDuplicado <= TimeSpan.Zero
            ? ValidateOptionsResult.Fail("Integraciones:VentanaDuplicado debe ser positiva.")
            : ValidateOptionsResult.Success;
    }
}

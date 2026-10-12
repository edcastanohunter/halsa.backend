using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Clinico.Crypto;

/// <summary>
/// Se ejecuta al arrancar (<c>ValidateOnStart</c>): sin una KEK válida la API no arranca, en vez de
/// fallar con el primer registro clínico. En Production solo se admite la KEK en archivo.
/// </summary>
/// <param name="environmentName">Nombre del entorno del host. Null (sin host) cuenta como no productivo.</param>
internal sealed class ClinicoOptionsValidator(string? environmentName) : IValidateOptions<ClinicoOptions>
{
    private const string Production = "Production";

    public ValidateOptionsResult Validate(string? name, ClinicoOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (string.IsNullOrWhiteSpace(options.KekId))
        {
            return ValidateOptionsResult.Fail("Clinico:KekId es obligatorio.");
        }

        if (string.Equals(environmentName, Production, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(options.KekBase64))
        {
            return ValidateOptionsResult.Fail(
                "Clinico:KekBase64 no se admite en Production: la KEK debe venir de Clinico:KekPath.");
        }

        return KekLoader.TryLoad(options, out _, out string error)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(error);
    }
}

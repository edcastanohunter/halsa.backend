using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace Halsa.Bff.Paciente.Sesion;

/// <summary>
/// Lo que el BFF hace con los tokens de la sesión, siempre en el servidor (SPEC 12): vence la sesión a las 12 h aunque
/// haya actividad, refresca el access token cuando le quedan menos de 60 s, y cierra la sesión de Keycloak al salir.
/// </summary>
internal sealed partial class SesionTokens(
    IHttpClientFactory httpClientFactory,
    IOptionsMonitor<OpenIdConnectOptions> oidc,
    IOptions<BffOptions> options,
    TimeProvider time,
    ILogger<SesionTokens> logger)
{
    /// <summary>El cliente con que el BFF habla con Keycloak (token y logout).</summary>
    public const string ClienteKeycloak = "keycloak";

    private readonly BffOptions _options = options.Value;

    /// <summary><c>OnSigningIn</c>: marca el inicio de la sesión, que la renovación de la cookie no mueve.</summary>
    public void AlIniciar(CookieSigningInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.Properties.IsPersistent = false;
        context.Properties.Items.TryAdd(Bff.InicioSesion, time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture));
    }

    /// <summary><c>OnValidatePrincipal</c>: la duración máxima y el refresco del access token.</summary>
    public async Task ValidarAsync(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        DateTimeOffset ahora = time.GetUtcNow();

        if (!context.Properties.Items.TryGetValue(Bff.InicioSesion, out string? inicio) ||
            !DateTimeOffset.TryParse(inicio, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset iniciadaEn) ||
            ahora - iniciadaEn > _options.DuracionMaxima)
        {
            await TerminarAsync(context);
            return;
        }

        string? expiraEn = context.Properties.GetTokenValue("expires_at");
        if (DateTimeOffset.TryParse(expiraEn, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset vence) &&
            vence - ahora > _options.MargenRefresco)
        {
            return;
        }

        string? refreshToken = context.Properties.GetTokenValue("refresh_token");
        TokensRefrescados? nuevos = refreshToken is null
            ? null
            : await RefrescarAsync(refreshToken, context.HttpContext.RequestAborted);

        if (nuevos is null)
        {
            await TerminarAsync(context);
            return;
        }

        context.Properties.UpdateTokenValue("access_token", nuevos.AccessToken);
        if (nuevos.RefreshToken is not null)
        {
            context.Properties.UpdateTokenValue("refresh_token", nuevos.RefreshToken);
        }

        if (nuevos.IdToken is not null)
        {
            context.Properties.UpdateTokenValue("id_token", nuevos.IdToken);
        }

        context.Properties.UpdateTokenValue(
            "expires_at", ahora.AddSeconds(nuevos.ExpiraEnSegundos).ToString("O", CultureInfo.InvariantCulture));
        context.ShouldRenew = true;
    }

    /// <summary>
    /// Cierra la sesión de Keycloak por el canal de servidor (el <c>end_session_endpoint</c> con el refresh token y las
    /// credenciales del client): ningún token pasa por el navegador. Si Keycloak no responde, la sesión local se cierra
    /// igual.
    /// </summary>
    public async Task CerrarEnKeycloakAsync(string? refreshToken, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(refreshToken))
        {
            return;
        }

        try
        {
            OpenIdConnectConfiguration configuracion = await ConfiguracionAsync(ct);
            using HttpResponseMessage respuesta = await httpClientFactory.CreateClient(ClienteKeycloak).PostAsync(
                configuracion.EndSessionEndpoint,
                Formulario(new Dictionary<string, string> { ["refresh_token"] = refreshToken }),
                ct);

            if (!respuesta.IsSuccessStatusCode)
            {
                LogLogoutFallido(logger, (int)respuesta.StatusCode);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or TaskCanceledException)
        {
            LogLogoutError(logger, ex);
        }
    }

    private async Task<TokensRefrescados?> RefrescarAsync(string refreshToken, CancellationToken ct)
    {
        try
        {
            OpenIdConnectConfiguration configuracion = await ConfiguracionAsync(ct);
            using HttpResponseMessage respuesta = await httpClientFactory.CreateClient(ClienteKeycloak).PostAsync(
                configuracion.TokenEndpoint,
                Formulario(new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = refreshToken
                }),
                ct);

            if (!respuesta.IsSuccessStatusCode)
            {
                LogRefrescoFallido(logger, (int)respuesta.StatusCode);
                return null;
            }

            using JsonDocument json = JsonDocument.Parse(await respuesta.Content.ReadAsStreamAsync(ct));
            JsonElement raiz = json.RootElement;

            return new TokensRefrescados(
                raiz.GetProperty("access_token").GetString()!,
                raiz.TryGetProperty("refresh_token", out JsonElement refresh) ? refresh.GetString() : null,
                raiz.TryGetProperty("id_token", out JsonElement id) ? id.GetString() : null,
                raiz.TryGetProperty("expires_in", out JsonElement expira) ? expira.GetInt32() : 300);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or JsonException or KeyNotFoundException)
        {
            LogRefrescoError(logger, ex);
            return null;
        }
    }

    private FormUrlEncodedContent Formulario(Dictionary<string, string> campos)
    {
        campos["client_id"] = _options.ClientId;
        campos["client_secret"] = _options.ClientSecret;
        return new FormUrlEncodedContent(campos);
    }

    private Task<OpenIdConnectConfiguration> ConfiguracionAsync(CancellationToken ct) =>
        oidc.Get(OpenIdConnectDefaults.AuthenticationScheme).ConfigurationManager?.GetConfigurationAsync(ct)
        ?? throw new InvalidOperationException("El handler OIDC no tiene un ConfigurationManager.");

    private static async Task TerminarAsync(CookieValidatePrincipalContext context)
    {
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }

    private sealed record TokensRefrescados(string AccessToken, string? RefreshToken, string? IdToken, int ExpiraEnSegundos);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak rechazó el refresco del token ({Status}): la sesión termina")]
    private static partial void LogRefrescoFallido(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo refrescar el token: la sesión termina")]
    private static partial void LogRefrescoError(ILogger logger, Exception ex);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Keycloak respondió {Status} al cierre de sesión")]
    private static partial void LogLogoutFallido(ILogger logger, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "No se pudo cerrar la sesión en Keycloak; la local se cierra igual")]
    private static partial void LogLogoutError(ILogger logger, Exception ex);
}

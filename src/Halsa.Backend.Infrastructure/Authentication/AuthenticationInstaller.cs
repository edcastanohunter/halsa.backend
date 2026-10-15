using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Application.Abstractions.Clinico;
using Halsa.Backend.Infrastructure.Auditoria;
using Halsa.Backend.Infrastructure.Authorization;
using Halsa.Backend.Infrastructure.Clinico;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Halsa.Backend.Infrastructure.Authentication;

/// <summary>
/// Wiring de autenticación JWT con dos realms de Keycloak.
///
/// El esquema por defecto (<see cref="AuthSchemes.Bearer"/>) es un <em>policy scheme</em>
/// que lee el claim <c>iss</c> del token (sin validarlo) y reenvía el request al esquema
/// <see cref="AuthSchemes.Personal"/> (realm <c>halsa</c>) o
/// <see cref="AuthSchemes.Paciente"/> (realm <c>halsa-pacientes</c>).
/// Un emisor desconocido se reenvía a <see cref="AuthSchemes.Personal"/>, que lo rechaza con 401.
/// </summary>
internal static class AuthenticationInstaller
{
    internal static IServiceCollection AddAuthenticationInternal(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services
            .AddOptions<KeycloakOptions>()
            .Bind(configuration.GetSection(KeycloakOptions.SectionName))
            .ValidateOnStart();

        KeycloakOptions keycloak = configuration
            .GetSection(KeycloakOptions.SectionName)
            .Get<KeycloakOptions>() ?? new KeycloakOptions();

        services
            .AddAuthentication(AuthSchemes.Bearer)

            // ── Esquema selector ──────────────────────────────────────────────
            .AddPolicyScheme(AuthSchemes.Bearer, AuthSchemes.Bearer, options =>
            {
                options.ForwardDefaultSelector = context =>
                {
                    string? authorization = context.Request.Headers.Authorization;

                    if (!string.IsNullOrEmpty(authorization) &&
                        authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                    {
                        string token = authorization["Bearer ".Length..].Trim();
                        try
                        {
                            var handler = new JsonWebTokenHandler();
                            if (handler.CanReadToken(token))
                            {
                                JsonWebToken jwt = handler.ReadJsonWebToken(token);
                                if (jwt.Issuer == keycloak.PacientesAuthority)
                                {
                                    return AuthSchemes.Paciente;
                                }
                            }
                        }
                        catch
                        {
                            // Token malformado → reenviar a Personal, que lo rechazará con 401.
                        }
                    }

                    return AuthSchemes.Personal;
                };
            })

            // ── Realm halsa (personal) ────────────────────────────────────────
            .AddJwtBearer(AuthSchemes.Personal, options =>
            {
                options.Authority = keycloak.PersonalAuthority;
                options.Audience = keycloak.Audience;
                options.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = keycloak.PersonalAuthority,
                    ValidateAudience = !string.IsNullOrWhiteSpace(keycloak.Audience),
                    ValidAudience = keycloak.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles"
                };
            })

            // ── Realm halsa-pacientes ─────────────────────────────────────────
            .AddJwtBearer(AuthSchemes.Paciente, options =>
            {
                options.Authority = keycloak.PacientesAuthority;
                options.Audience = keycloak.Audience;
                options.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = keycloak.PacientesAuthority,
                    ValidateAudience = !string.IsNullOrWhiteSpace(keycloak.Audience),
                    ValidAudience = keycloak.Audience,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30),
                    NameClaimType = "preferred_username",
                    RoleClaimType = "roles"
                };
            });

        services.AddHttpContextAccessor();
        services.AddScoped<IClaimsTransformation, KeycloakClaimsTransformation>();
        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IProfesionalActual, ProfesionalActual>();

        return services;
    }

    internal static IServiceCollection AddAuthorizationInternal(this IServiceCollection services)
    {
        services.AddAuthorization(options =>
        {
            options.AddPolicy(Policies.IncapacidadEscritura, policy => policy.AddRequirements(
                new PermissionRequirement(Permissions.IncapacidadesWrite),
                new StepUpRequirement(Policies.AcrIncapacidadEscritura, MaxAntiguedadAuth: null)));

            options.AddPolicy(Policies.Paciente, policy => policy.AddRequirements(new ActorPacienteRequirement()));

            // SPEC 08: the clinical policies are evaluated with the case as resource (IClinicalAuthorization).
            foreach (string politica in Policies.Clinicas)
            {
                options.AddPolicy(politica, policy => policy
                    .AddRequirements(Policies.RequisitosPrevios(politica))
                    .AddRequirements(new AccesoClinicoRequirement(Policies.AdmiteEmergencia(politica))));
            }
        });

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();
        services.AddScoped<IAuthorizationHandler, StepUpHandler>();
        services.AddScoped<IAuthorizationHandler, ActorPacienteHandler>();
        services.AddScoped<IAuthorizationHandler, AccesoClinicoHandler>();
        services.AddScoped<AccesoEmergencia>();
        services.AddScoped<ICasoAccessReader, CasoAccessReader>();
        services.AddScoped<IClinicalAuthorization, ClinicalAuthorization>();

        // Records policy denials on .Auditable routes (SPEC 04), then answers as the default handler would.
        services.AddSingleton<IAuthorizationMiddlewareResultHandler, AuditingAuthorizationResultHandler>();

        return services;
    }
}

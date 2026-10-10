using Halsa.Backend.Application.Abstractions.Authentication;
using Halsa.Backend.Infrastructure.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;

namespace Halsa.Backend.Infrastructure.Authentication;

/// <summary>
/// Keycloak / OIDC wiring. The API is a pure resource server: it validates access tokens and never
/// issues them, which is what lets several applications share one identity.
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
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = keycloak.Authority;
                options.Audience = keycloak.Audience;
                options.RequireHttpsMetadata = keycloak.RequireHttpsMetadata;
                options.MapInboundClaims = false;

                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = keycloak.Authority,
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

        return services;
    }

    internal static IServiceCollection AddAuthorizationInternal(this IServiceCollection services)
    {
        services.AddAuthorization();

        services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
        services.AddScoped<IAuthorizationHandler, PermissionAuthorizationHandler>();

        return services;
    }
}

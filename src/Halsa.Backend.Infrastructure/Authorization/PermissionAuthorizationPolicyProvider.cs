using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Halsa.Backend.Infrastructure.Authorization;

/// <summary>
/// Turns any policy name into a permission policy on demand, so endpoints can call
/// <c>RequireAuthorization("casos:write")</c> without a registration ceremony per permission.
/// </summary>
internal sealed class PermissionAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        AuthorizationPolicy? policy = await base.GetPolicyAsync(policyName).ConfigureAwait(false);

        if (policy is not null)
        {
            return policy;
        }

        // No scheme is pinned here: the default authentication scheme wins, which lets
        // functional tests swap in a stub scheme without rewriting every policy.
        return new AuthorizationPolicyBuilder()
            .AddRequirements(new PermissionRequirement(policyName))
            .Build();
    }
}

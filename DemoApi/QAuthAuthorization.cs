using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace DemoApi;

public static class QAuthPolicies
{
    /// <summary>Valid token that carries <c>q_tenant</c>.</summary>
    public const string Tenant = "Tenant";

    /// <summary>Valid token, <c>q_tenant</c>, and the Admin role.</summary>
    public const string Admin = "Admin";

    public const string MissingTenant = "missing_tenant";

    public static AuthorizationBuilder AddQAuthPolicies(this AuthorizationBuilder auth) => auth
        .AddPolicy(Tenant, p => p
            .RequireAuthenticatedUser()
            .AddRequirements(new TenantRequirement()))
        .AddPolicy(Admin, p => p
            .RequireAuthenticatedUser()
            .AddRequirements(new TenantRequirement())
            .RequireAssertion(ctx => QAuthClaims.From(ctx.User).IsAdmin));
}

/// <summary>
/// Refuses tokens without <c>q_tenant</c>. If Frontegg's targeting rule doesn't match, it silently
/// issues its default token without our claims; this is what catches that.
/// </summary>
public sealed class TenantRequirement : AuthorizationHandler<TenantRequirement>, IAuthorizationRequirement
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, TenantRequirement requirement)
    {
        if (QAuthClaims.From(context.User).HasTenant)
            context.Succeed(requirement);
        else if (context.User.Identity?.IsAuthenticated == true)
            context.Fail(new AuthorizationFailureReason(this, QAuthPolicies.MissingTenant));
        return Task.CompletedTask;
    }
}

/// <summary>Turns a missing-tenant failure into <c>403 {"error":"missing_tenant"}</c>; everything else is default.</summary>
public sealed class QAuthAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public async Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult result)
    {
        if (result.Forbidden &&
            result.AuthorizationFailure?.FailureReasons.Any(r => r.Message == QAuthPolicies.MissingTenant) == true)
        {
            await Results.Json(new { error = QAuthPolicies.MissingTenant }, statusCode: StatusCodes.Status403Forbidden)
                .ExecuteAsync(context);
            return;
        }

        await _default.HandleAsync(next, context, policy, result);
    }
}

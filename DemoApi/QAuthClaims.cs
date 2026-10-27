using System.Security.Claims;

namespace DemoApi;

/// <summary>
/// The Q.auth view of a validated Frontegg access token. Seed of the qauth-dotnet library
/// (q.auth/05). Claim names are the raw token names: JwtBearer runs with MapInboundClaims = false.
/// </summary>
public sealed record QAuthClaims(
    string? Subject,
    string? Email,
    string? Tenant,
    IReadOnlyList<string> Roles,
    string? Caller,
    string? AppId)
{
    public const string TenantClaim = "q_tenant";
    public const string EmailClaim = "q_email";
    public const string CallerClaim = "q_caller";
    public const string AppIdClaim = "applicationId";
    public const string SubjectClaim = "sub";

    /// <summary>
    /// Frontegg's own <c>roles</c> claim. It is the contract's <c>q_roles</c> (q.auth/05 §4): Frontegg's
    /// template engine turns arrays into strings, so <c>q_roles</c> can't be emitted from a template.
    /// </summary>
    public const string RolesClaim = "roles";

    /// <summary>Frontegg's role key for administrators (exact case).</summary>
    public const string AdminRole = "Admin";

    public bool HasTenant => !string.IsNullOrEmpty(Tenant);

    public bool IsAdmin => Roles.Contains(AdminRole, StringComparer.Ordinal);

    public static QAuthClaims From(ClaimsPrincipal user) => new(
        Subject: user.FindFirst(SubjectClaim)?.Value,
        Email: user.FindFirst(EmailClaim)?.Value,
        Tenant: user.FindFirst(TenantClaim)?.Value,
        // A JSON array arrives as one claim per element. A plain string (what the template engine
        // makes of an array) arrives as a single claim: tolerated, not relied on.
        Roles: user.FindAll(RolesClaim).Select(c => c.Value).ToArray(),
        Caller: user.FindFirst(CallerClaim)?.Value,
        AppId: user.FindFirst(AppIdClaim)?.Value);
}

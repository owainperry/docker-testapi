namespace DemoApi;

/// <summary>
/// The portfolio endpoints. Two layers of checks, in this order:
/// 1. the token: valid, has <c>q_tenant</c>, and the portfolio is in that tenant (as before OpenFGA);
/// 2. OpenFGA: what this user may do with this portfolio.
/// A portfolio the caller can't view looks absent (404). One they can view but not edit or share
/// gives 403 for those actions. If OpenFGA can't answer, 503: never allow by default.
/// </summary>
public static class PortfolioEndpoints
{
    public sealed record RenameBody(string? Name);
    public sealed record GrantBody(string? Relation);

    public static void MapPortfolios(this WebApplication app)
    {
        var group = app.MapGroup("/portfolios")
            .RequireAuthorization(QAuthPolicies.Tenant)
            .AddEndpointFilter(FailClosed);

        // Only the portfolios the caller can view, e.g. for a list page.
        group.MapGet("/", async (HttpContext http, IPortfolioAccess access, CancellationToken ct) =>
        {
            var caller = QAuthClaims.From(http.User);
            var ids = await access.Viewable(caller, ct);
            return Results.Ok(ids
                .Select(id => Portfolios.TryGet(id, out var p) && p.Tenant == caller.Tenant ? p : null)
                .OfType<Portfolio>());
        });

        group.MapGet("/{id}", async (string id, HttpContext http, IPortfolioAccess access, CancellationToken ct) =>
            await Visible(id, http, access, ct) is { } p ? Results.Ok(p) : Results.NotFound());

        // For a UI: which buttons to show. The API still checks each action when it's called.
        group.MapGet("/{id}/permissions", async (string id, HttpContext http, IPortfolioAccess access, CancellationToken ct) =>
        {
            var caller = QAuthClaims.From(http.User);
            if (!InTenant(id, caller, out _)) return Results.NotFound();
            var perms = await access.Permissions(caller, id, ct);
            return perms.Contains(PortfolioPermission.View)
                ? Results.Ok(new
                {
                    view = true,
                    edit = perms.Contains(PortfolioPermission.Edit),
                    share = perms.Contains(PortfolioPermission.Share),
                })
                : Results.NotFound();
        });

        group.MapPut("/{id}", async (string id, RenameBody body, HttpContext http, IPortfolioAccess access, CancellationToken ct) =>
        {
            if (await Visible(id, http, access, ct) is not { } p) return Results.NotFound();
            if (!await access.Can(QAuthClaims.From(http.User), PortfolioPermission.Edit, id, ct)) return Results.Forbid();
            if (string.IsNullOrWhiteSpace(body.Name)) return Error("name_required");
            return Results.Ok(Portfolios.Rename(p, body.Name.Trim()));
        });

        // Sharing: who has direct access, and granting or removing it. Needs can_share.
        group.MapGet("/{id}/access", async (string id, HttpContext http, IPortfolioAccess access, CancellationToken ct) =>
        {
            if (await Sharable(id, http, access, ct) is { } refused) return refused;
            var grants = await access.Grants(id, ct);
            return Results.Ok(grants.Select(g => new { userId = g.UserId, relation = Name(g.Relation) }));
        });

        group.MapPut("/{id}/access/{userId}", async (string id, string userId, GrantBody body, HttpContext http, IPortfolioAccess access, CancellationToken ct) =>
        {
            if (await Sharable(id, http, access, ct) is { } refused) return refused;
            if (!PortfolioAccessIds.ValidUserId().IsMatch(userId)) return Error("invalid_user_id");
            if (ParseRelation(body.Relation) is not { } relation) return Error("relation_must_be_viewer_editor_or_owner");

            await access.Grant(id, userId, relation, ct);
            return Results.Ok(new { userId, relation = Name(relation) });
        });

        group.MapDelete("/{id}/access/{userId}", async (string id, string userId, HttpContext http, IPortfolioAccess access, CancellationToken ct) =>
        {
            if (await Sharable(id, http, access, ct) is { } refused) return refused;
            if (!PortfolioAccessIds.ValidUserId().IsMatch(userId)) return Error("invalid_user_id");
            await access.Revoke(id, userId, ct);
            return Results.NoContent();
        });
    }

    /// <summary>The token's tenant check, unchanged from before OpenFGA: another tenant's portfolio doesn't exist.</summary>
    private static bool InTenant(string id, QAuthClaims caller, out Portfolio portfolio) =>
        Portfolios.TryGet(id, out portfolio) && portfolio.Tenant == caller.Tenant;

    private static async Task<Portfolio?> Visible(string id, HttpContext http, IPortfolioAccess access, CancellationToken ct)
    {
        var caller = QAuthClaims.From(http.User);
        return InTenant(id, caller, out var p) && await access.Can(caller, PortfolioPermission.View, id, ct) ? p : null;
    }

    /// <summary>Null when the caller may share the portfolio; otherwise the response to send (404 or 403).</summary>
    private static async Task<IResult?> Sharable(string id, HttpContext http, IPortfolioAccess access, CancellationToken ct)
    {
        if (await Visible(id, http, access, ct) is null) return Results.NotFound();
        return await access.Can(QAuthClaims.From(http.User), PortfolioPermission.Share, id, ct) ? null : Results.Forbid();
    }

    private static PortfolioRelation? ParseRelation(string? s) => s switch
    {
        "viewer" => PortfolioRelation.Viewer,
        "editor" => PortfolioRelation.Editor,
        "owner" => PortfolioRelation.Owner,
        _ => null,
    };

    private static string Name(PortfolioRelation r) => r.ToString().ToLowerInvariant();

    private static IResult Error(string error) => Results.BadRequest(new { error });

    private static async ValueTask<object?> FailClosed(EndpointFilterInvocationContext ctx, EndpointFilterDelegate next)
    {
        try
        {
            return await next(ctx);
        }
        catch (AuthorizationUnavailableException e)
        {
            ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                .CreateLogger("DemoApi.OpenFga")
                .LogWarning("Refused {Path} because OpenFGA could not answer: {Reason}", ctx.HttpContext.Request.Path, e.Message);
            return Results.Json(new { error = "authorization_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}

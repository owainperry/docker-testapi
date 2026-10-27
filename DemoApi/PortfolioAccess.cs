using System.Text.RegularExpressions;
using OpenFga.Sdk.Client;
using OpenFga.Sdk.Client.Model;
using OpenFga.Sdk.Configuration;

namespace DemoApi;

/// <summary>What the API asks about. Maps to the model's <c>can_*</c> relations (openfga/model.fga).</summary>
public enum PortfolioPermission { View, Edit, Share }

/// <summary>What can be granted directly. Maps to the model's <c>viewer</c> / <c>editor</c> / <c>owner</c>.</summary>
public enum PortfolioRelation { Viewer, Editor, Owner }

public sealed record PortfolioGrant(string UserId, PortfolioRelation Relation);

/// <summary>
/// Data-level access to portfolios: who can view, edit and share which one. Backed by OpenFGA; the
/// tests swap in a fake. Tenant isolation and roles still come from the token (<see cref="QAuthClaims"/>).
/// </summary>
public interface IPortfolioAccess
{
    Task<bool> Can(QAuthClaims caller, PortfolioPermission permission, string portfolioId, CancellationToken ct);

    /// <summary>All three permissions in one round trip, for a UI to show or hide its buttons.</summary>
    Task<IReadOnlySet<PortfolioPermission>> Permissions(QAuthClaims caller, string portfolioId, CancellationToken ct);

    /// <summary>IDs of the portfolios the caller can view.</summary>
    Task<IReadOnlyList<string>> Viewable(QAuthClaims caller, CancellationToken ct);

    /// <summary>Direct grants on a portfolio (not the ones implied by being a tenant admin).</summary>
    Task<IReadOnlyList<PortfolioGrant>> Grants(string portfolioId, CancellationToken ct);

    /// <summary>Gives the user exactly this relation, replacing any direct one they had.</summary>
    Task Grant(string portfolioId, string userId, PortfolioRelation relation, CancellationToken ct);

    /// <summary>Removes every direct grant the user has on the portfolio.</summary>
    Task Revoke(string portfolioId, string userId, CancellationToken ct);
}

/// <summary>OpenFGA could not answer (down, misconfigured, not configured). The API fails closed with 503.</summary>
public sealed class AuthorizationUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public static partial class PortfolioAccessIds
{
    /// <summary>
    /// User IDs accepted in URLs. Frontegg's <c>sub</c> is a UUID; this also keeps out the characters
    /// that mean something in an OpenFGA tuple (<c>:</c>, <c>#</c>, spaces).
    /// </summary>
    [GeneratedRegex("^[A-Za-z0-9-]{1,64}$")]
    public static partial Regex ValidUserId();
}

public sealed class OpenFgaOptions
{
    public string? ApiUrl { get; set; }
    public string? StoreId { get; set; }

    /// <summary>Pin the model version. Empty means the store's latest model, fine for a demo.</summary>
    public string? AuthorizationModelId { get; set; }

    /// <summary>OpenFGA's preshared key, if the server needs one. Environment variable only, never in a file.</summary>
    public string? ApiToken { get; set; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiUrl) && !string.IsNullOrWhiteSpace(StoreId);
}

public sealed class OpenFgaPortfolioAccess(OpenFgaClient fga, ILogger<OpenFgaPortfolioAccess> logger) : IPortfolioAccess
{
    public static OpenFgaClient CreateClient(OpenFgaOptions o) => new(new ClientConfiguration
    {
        ApiUrl = o.ApiUrl!,
        StoreId = o.StoreId,
        AuthorizationModelId = string.IsNullOrWhiteSpace(o.AuthorizationModelId) ? null : o.AuthorizationModelId,
        Credentials = string.IsNullOrWhiteSpace(o.ApiToken)
            ? null
            : new Credentials { Method = CredentialsMethod.ApiToken, Config = new CredentialsConfig { ApiToken = o.ApiToken } },
    });

    public Task<bool> Can(QAuthClaims caller, PortfolioPermission permission, string portfolioId, CancellationToken ct) =>
        Call(async () =>
        {
            if (caller.Subject is null) return false;
            var res = await fga.Check(new ClientCheckRequest
            {
                User = User(caller.Subject),
                Relation = Relation(permission),
                Object = Portfolio(portfolioId),
                ContextualTuples = ContextualTuples(caller),
            }, cancellationToken: ct);
            logger.LogDebug("Check {Permission} on {Portfolio} for {Sub}: {Allowed}", permission, portfolioId, caller.Subject, res.Allowed);
            return res.Allowed == true;
        });

    public Task<IReadOnlySet<PortfolioPermission>> Permissions(QAuthClaims caller, string portfolioId, CancellationToken ct) =>
        Call<IReadOnlySet<PortfolioPermission>>(async () =>
        {
            if (caller.Subject is null) return new HashSet<PortfolioPermission>();
            // BatchCheck items take the API's own type rather than the client's list.
            var contextual = new OpenFga.Sdk.Model.ContextualTupleKeys
            {
                TupleKeys = ContextualTuples(caller)
                    .Select(t => new OpenFga.Sdk.Model.TupleKey(t.User, t.Relation, t.Object))
                    .ToList(),
            };
            var res = await fga.BatchCheck(new ClientBatchCheckRequest
            {
                Checks = Enum.GetValues<PortfolioPermission>().Select(p => new ClientBatchCheckItem
                {
                    User = User(caller.Subject),
                    Relation = Relation(p),
                    Object = Portfolio(portfolioId),
                    ContextualTuples = contextual,
                    CorrelationId = p.ToString(),
                }).ToList(),
            }, cancellationToken: ct);
            return res.Result
                .Where(r => r.Allowed)
                .Select(r => Enum.Parse<PortfolioPermission>(r.CorrelationId))
                .ToHashSet();
        });

    public Task<IReadOnlyList<string>> Viewable(QAuthClaims caller, CancellationToken ct) =>
        Call<IReadOnlyList<string>>(async () =>
        {
            if (caller.Subject is null) return [];
            var res = await fga.ListObjects(new ClientListObjectsRequest
            {
                User = User(caller.Subject),
                Relation = Relation(PortfolioPermission.View),
                Type = "portfolio",
                ContextualTuples = ContextualTuples(caller),
            }, cancellationToken: ct);
            return res.Objects.Select(o => o["portfolio:".Length..]).Order(StringComparer.Ordinal).ToList();
        });

    public Task<IReadOnlyList<PortfolioGrant>> Grants(string portfolioId, CancellationToken ct) =>
        Call<IReadOnlyList<PortfolioGrant>>(async () => (await DirectTuples(portfolioId, userId: null, ct))
            .Select(t => new PortfolioGrant(t.User["user:".Length..], Enum.Parse<PortfolioRelation>(t.Relation, ignoreCase: true)))
            .OrderBy(g => g.UserId, StringComparer.Ordinal)
            .ToList());

    public Task Grant(string portfolioId, string userId, PortfolioRelation relation, CancellationToken ct) =>
        Call(async () =>
        {
            // OpenFGA refuses to write a tuple that exists or delete one that doesn't, so read first.
            var existing = await DirectTuples(portfolioId, userId, ct);
            var wanted = Relation(relation);
            if (existing.Count == 1 && existing[0].Relation == wanted) return true;

            await fga.Write(new ClientWriteRequest
            {
                Deletes = existing.Select(t => new ClientTupleKeyWithoutCondition
                    { User = t.User, Relation = t.Relation, Object = t.Object }).ToList(),
                Writes = [new ClientTupleKey { User = User(userId), Relation = wanted, Object = Portfolio(portfolioId) }],
            }, cancellationToken: ct);
            return true;
        });

    public Task Revoke(string portfolioId, string userId, CancellationToken ct) =>
        Call(async () =>
        {
            var existing = await DirectTuples(portfolioId, userId, ct);
            if (existing.Count == 0) return true;
            await fga.Write(new ClientWriteRequest
            {
                Deletes = existing.Select(t => new ClientTupleKeyWithoutCondition
                    { User = t.User, Relation = t.Relation, Object = t.Object }).ToList(),
            }, cancellationToken: ct);
            return true;
        });

    /// <summary>
    /// What the token says, handed to OpenFGA for this one request instead of being stored: a user with
    /// the Admin role is an admin of their own tenant. Roles stay managed in Frontegg only.
    /// </summary>
    private static List<ClientTupleKey> ContextualTuples(QAuthClaims caller) =>
        caller.IsAdmin && caller.HasTenant && caller.Subject is not null
            ? [new ClientTupleKey { User = User(caller.Subject), Relation = "admin", Object = $"tenant:{caller.Tenant}" }]
            : [];

    private async Task<List<OpenFga.Sdk.Model.TupleKey>> DirectTuples(string portfolioId, string? userId, CancellationToken ct)
    {
        var tuples = new List<OpenFga.Sdk.Model.TupleKey>();
        string? continuation = null;
        do
        {
            var res = await fga.Read(
                new ClientReadRequest { Object = Portfolio(portfolioId), User = userId is null ? null : User(userId) },
                new ClientReadOptions { ContinuationToken = continuation },
                ct);
            tuples.AddRange(res.Tuples.Select(t => t.Key).Where(k => k.User.StartsWith("user:", StringComparison.Ordinal)));
            continuation = string.IsNullOrEmpty(res.ContinuationToken) ? null : res.ContinuationToken;
        } while (continuation is not null);
        return tuples;
    }

    private static string User(string sub) => $"user:{sub}";
    private static string Portfolio(string id) => $"portfolio:{id}";

    private static string Relation(PortfolioPermission p) => p switch
    {
        PortfolioPermission.View => "can_view",
        PortfolioPermission.Edit => "can_edit",
        PortfolioPermission.Share => "can_share",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    private static string Relation(PortfolioRelation r) => r.ToString().ToLowerInvariant();

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new AuthorizationUnavailableException($"OpenFGA call failed: {e.Message}", e);
        }
    }
}

/// <summary>Used when OpenFGA isn't configured: every portfolio endpoint answers 503, the rest of the API works.</summary>
public sealed class UnconfiguredPortfolioAccess : IPortfolioAccess
{
    private static AuthorizationUnavailableException NotConfigured() =>
        new("OpenFGA is not configured: set OpenFga__ApiUrl and OpenFga__StoreId.");

    public Task<bool> Can(QAuthClaims caller, PortfolioPermission permission, string portfolioId, CancellationToken ct) => throw NotConfigured();
    public Task<IReadOnlySet<PortfolioPermission>> Permissions(QAuthClaims caller, string portfolioId, CancellationToken ct) => throw NotConfigured();
    public Task<IReadOnlyList<string>> Viewable(QAuthClaims caller, CancellationToken ct) => throw NotConfigured();
    public Task<IReadOnlyList<PortfolioGrant>> Grants(string portfolioId, CancellationToken ct) => throw NotConfigured();
    public Task Grant(string portfolioId, string userId, PortfolioRelation relation, CancellationToken ct) => throw NotConfigured();
    public Task Revoke(string portfolioId, string userId, CancellationToken ct) => throw NotConfigured();
}

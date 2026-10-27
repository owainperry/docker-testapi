using System.Collections.Concurrent;

namespace DemoApi.Tests;

/// <summary>
/// In-memory stand-in for OpenFGA with the same rules as openfga/model.fga, so the API tests need no
/// server. The model itself is tested by <c>fga model test</c>, and the real client by
/// <see cref="OpenFgaIntegrationTests"/>.
/// </summary>
public sealed class FakePortfolioAccess : IPortfolioAccess
{
    private readonly ConcurrentDictionary<(string Portfolio, string User), PortfolioRelation> _grants = new();

    /// <summary>Simulates OpenFGA being unreachable.</summary>
    public bool Down { get; set; }

    public Task<bool> Can(QAuthClaims caller, PortfolioPermission permission, string portfolioId, CancellationToken ct) =>
        Task.FromResult(Allowed(caller, portfolioId).Contains(permission));

    public Task<IReadOnlySet<PortfolioPermission>> Permissions(QAuthClaims caller, string portfolioId, CancellationToken ct) =>
        Task.FromResult<IReadOnlySet<PortfolioPermission>>(Allowed(caller, portfolioId));

    public Task<IReadOnlyList<string>> Viewable(QAuthClaims caller, CancellationToken ct)
    {
        ThrowIfDown();
        IReadOnlyList<string> ids = new[] { "acme-1", "other-1" }
            .Where(id => Allowed(caller, id).Contains(PortfolioPermission.View))
            .ToList();
        return Task.FromResult(ids);
    }

    public Task<IReadOnlyList<PortfolioGrant>> Grants(string portfolioId, CancellationToken ct)
    {
        ThrowIfDown();
        IReadOnlyList<PortfolioGrant> grants = _grants
            .Where(g => g.Key.Portfolio == portfolioId)
            .Select(g => new PortfolioGrant(g.Key.User, g.Value))
            .ToList();
        return Task.FromResult(grants);
    }

    public Task Grant(string portfolioId, string userId, PortfolioRelation relation, CancellationToken ct)
    {
        ThrowIfDown();
        _grants[(portfolioId, userId)] = relation;
        return Task.CompletedTask;
    }

    public Task Revoke(string portfolioId, string userId, CancellationToken ct)
    {
        ThrowIfDown();
        _grants.TryRemove((portfolioId, userId), out _);
        return Task.CompletedTask;
    }

    private HashSet<PortfolioPermission> Allowed(QAuthClaims caller, string portfolioId)
    {
        ThrowIfDown();
        var tenantAdmin = caller.IsAdmin && Portfolios.TryGet(portfolioId, out var p) && p.Tenant == caller.Tenant;
        var granted = _grants.TryGetValue((portfolioId, caller.Subject ?? ""), out var relation);

        var allowed = new HashSet<PortfolioPermission>();
        if (tenantAdmin || granted) allowed.Add(PortfolioPermission.View);
        if (tenantAdmin || (granted && relation is PortfolioRelation.Editor or PortfolioRelation.Owner)) allowed.Add(PortfolioPermission.Edit);
        if (tenantAdmin || (granted && relation is PortfolioRelation.Owner)) allowed.Add(PortfolioPermission.Share);
        return allowed;
    }

    private void ThrowIfDown()
    {
        if (Down) throw new AuthorizationUnavailableException("fake OpenFGA is down");
    }
}

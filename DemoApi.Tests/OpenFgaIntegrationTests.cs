using Microsoft.Extensions.Logging.Abstractions;

namespace DemoApi.Tests;

/// <summary>Runs only when a real OpenFGA store is given, e.g. after <c>make fga-up fga-init</c>.</summary>
public sealed class FgaFactAttribute : FactAttribute
{
    public FgaFactAttribute()
    {
        if (OpenFgaIntegrationTests.Options() is null)
            Skip = "Set OPENFGA_TEST_API_URL and OPENFGA_TEST_STORE_ID (a store made from openfga/store.fga.yaml) to run.";
    }
}

/// <summary>
/// The real <see cref="OpenFgaPortfolioAccess"/> against a real server: checks the SDK calls, the
/// contextual admin tuple and the seeded tenant tuples. Each test uses fresh user IDs.
/// </summary>
public sealed class OpenFgaIntegrationTests
{
    private const string OtherTenant = "00000000-0000-0000-0000-000000000001";

    internal static OpenFgaOptions? Options()
    {
        var o = new OpenFgaOptions
        {
            ApiUrl = Environment.GetEnvironmentVariable("OPENFGA_TEST_API_URL"),
            StoreId = Environment.GetEnvironmentVariable("OPENFGA_TEST_STORE_ID"),
            ApiToken = Environment.GetEnvironmentVariable("OPENFGA_TEST_API_TOKEN"),
        };
        return o.IsConfigured ? o : null;
    }

    private readonly OpenFgaPortfolioAccess _fga = Options() is { } o
        ? new(OpenFgaPortfolioAccess.CreateClient(o), NullLogger<OpenFgaPortfolioAccess>.Instance)
        : null!;

    private static QAuthClaims Caller(string role = "User", string tenant = TestTokens.Tenant) =>
        new(Guid.NewGuid().ToString(), "someone@example.com", tenant, [role], "user", TestTokens.ApiAppId);

    [FgaFact]
    public async Task Admin_role_in_the_token_makes_a_tenant_admin()
    {
        var admin = Caller(role: "Admin");
        Assert.True(await _fga.Can(admin, PortfolioPermission.Share, "acme-1", default));
        Assert.Contains("acme-1", await _fga.Viewable(admin, default));
        Assert.DoesNotContain("other-1", await _fga.Viewable(admin, default));

        Assert.False(await _fga.Can(Caller(), PortfolioPermission.View, "acme-1", default));
        Assert.False(await _fga.Can(Caller(role: "Admin", tenant: OtherTenant), PortfolioPermission.View, "acme-1", default));
        Assert.True(await _fga.Can(Caller(role: "Admin", tenant: OtherTenant), PortfolioPermission.Edit, "other-1", default));
    }

    [FgaFact]
    public async Task Grant_change_and_revoke()
    {
        var user = Caller();

        await _fga.Grant("acme-1", user.Subject!, PortfolioRelation.Viewer, default);
        Assert.Equal([PortfolioPermission.View], await _fga.Permissions(user, "acme-1", default));
        Assert.Equal(["acme-1"], await _fga.Viewable(user, default));

        // Granting again is a no-op; a different relation replaces the old one.
        await _fga.Grant("acme-1", user.Subject!, PortfolioRelation.Viewer, default);
        await _fga.Grant("acme-1", user.Subject!, PortfolioRelation.Editor, default);
        Assert.Equal(
            [new PortfolioGrant(user.Subject!, PortfolioRelation.Editor)],
            (await _fga.Grants("acme-1", default)).Where(g => g.UserId == user.Subject));
        Assert.True(await _fga.Can(user, PortfolioPermission.Edit, "acme-1", default));
        Assert.False(await _fga.Can(user, PortfolioPermission.Share, "acme-1", default));

        await _fga.Revoke("acme-1", user.Subject!, default);
        await _fga.Revoke("acme-1", user.Subject!, default);
        Assert.False(await _fga.Can(user, PortfolioPermission.View, "acme-1", default));
        Assert.Empty(await _fga.Viewable(user, default));
    }

    [FgaFact]
    public async Task Unreachable_server_is_unavailable_not_denied()
    {
        var down = new OpenFgaPortfolioAccess(
            OpenFgaPortfolioAccess.CreateClient(new OpenFgaOptions { ApiUrl = "http://127.0.0.1:1", StoreId = Options()!.StoreId }),
            NullLogger<OpenFgaPortfolioAccess>.Instance);

        await Assert.ThrowsAsync<AuthorizationUnavailableException>(() =>
            down.Can(Caller(), PortfolioPermission.View, "acme-1", default));
    }
}

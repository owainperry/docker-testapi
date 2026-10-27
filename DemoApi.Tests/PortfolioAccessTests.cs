using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace DemoApi.Tests;

/// <summary>
/// Data-level access through the API, with OpenFGA faked. Each test uses its own user IDs, because
/// the grants live in one fake shared by the class.
/// </summary>
public sealed class PortfolioAccessTests(DemoApiFactory factory) : IClassFixture<DemoApiFactory>
{
    private const string OtherTenant = "00000000-0000-0000-0000-000000000001";

    /// <summary>A plain user (role "User") of the test tenant, with a fresh ID.</summary>
    private static (string Sub, string Token) NewUser(string role = "User", string tenant = TestTokens.Tenant)
    {
        var sub = Guid.NewGuid().ToString();
        return (sub, TestTokens.Create(c =>
        {
            c["sub"] = sub;
            c["roles"] = new[] { role };
            c["q_tenant"] = tenant;
        }));
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, string path, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) req.Content = JsonContent.Create(body);
        return await factory.CreateClient().SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private Task Grant(string sub, string relation) =>
        factory.Access.Grant("acme-1", sub, Enum.Parse<PortfolioRelation>(relation, ignoreCase: true), default);

    [Fact]
    public async Task User_without_a_grant_sees_nothing()
    {
        var (_, token) = NewUser();

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/portfolios/acme-1", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/portfolios/acme-1/permissions", token)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Put, "/portfolios/acme-1", token, new { name = "x" })).StatusCode);
        Assert.Equal(0, (await Json(await Send(HttpMethod.Get, "/portfolios", token))).GetArrayLength());
    }

    [Fact]
    public async Task Viewer_can_view_but_not_edit_or_share()
    {
        var (sub, token) = NewUser();
        await Grant(sub, "viewer");

        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/portfolios/acme-1", token)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Put, "/portfolios/acme-1", token, new { name = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await Send(HttpMethod.Get, "/portfolios/acme-1/access", token)).StatusCode);

        var perms = await Json(await Send(HttpMethod.Get, "/portfolios/acme-1/permissions", token));
        Assert.True(perms.GetProperty("view").GetBoolean());
        Assert.False(perms.GetProperty("edit").GetBoolean());
        Assert.False(perms.GetProperty("share").GetBoolean());

        var list = await Json(await Send(HttpMethod.Get, "/portfolios", token));
        Assert.Equal(["acme-1"], list.EnumerateArray().Select(p => p.GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Editor_can_edit_but_not_share()
    {
        var (sub, token) = NewUser();
        await Grant(sub, "editor");

        var res = await Send(HttpMethod.Put, "/portfolios/acme-1", token, new { name = "Acme credit book" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("Acme credit book", (await Json(res)).GetProperty("name").GetString());

        Assert.Equal(HttpStatusCode.BadRequest, (await Send(HttpMethod.Put, "/portfolios/acme-1", token, new { name = " " })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Send(HttpMethod.Put, $"/portfolios/acme-1/access/{Guid.NewGuid()}", token, new { relation = "viewer" })).StatusCode);
    }

    [Fact]
    public async Task Owner_shares_and_unshares()
    {
        var (owner, ownerToken) = NewUser();
        var (friend, friendToken) = NewUser();
        await Grant(owner, "owner");

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/portfolios/acme-1", friendToken)).StatusCode);

        var share = await Send(HttpMethod.Put, $"/portfolios/acme-1/access/{friend}", ownerToken, new { relation = "viewer" });
        Assert.Equal(HttpStatusCode.OK, share.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/portfolios/acme-1", friendToken)).StatusCode);

        var grants = await Json(await Send(HttpMethod.Get, "/portfolios/acme-1/access", ownerToken));
        Assert.Contains(grants.EnumerateArray(), g =>
            g.GetProperty("userId").GetString() == friend && g.GetProperty("relation").GetString() == "viewer");

        Assert.Equal(HttpStatusCode.NoContent, (await Send(HttpMethod.Delete, $"/portfolios/acme-1/access/{friend}", ownerToken)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/portfolios/acme-1", friendToken)).StatusCode);
    }

    [Theory]
    [InlineData("user:x", "viewer", "invalid_user_id")]
    [InlineData("a%23b", "viewer", "invalid_user_id")]
    [InlineData("valid-id", "admin", "relation_must_be_viewer_editor_or_owner")]
    [InlineData("valid-id", "Viewer", "relation_must_be_viewer_editor_or_owner")]
    [InlineData("valid-id", "1", "relation_must_be_viewer_editor_or_owner")]
    public async Task Bad_grants_are_400(string userId, string relation, string error)
    {
        var (owner, token) = NewUser();
        await Grant(owner, "owner");

        var res = await Send(HttpMethod.Put, $"/portfolios/acme-1/access/{userId}", token, new { relation });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(error, (await Json(res)).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Tenant_admin_can_do_everything_in_their_own_tenant_only()
    {
        var (_, token) = NewUser(role: "Admin");

        var perms = await Json(await Send(HttpMethod.Get, "/portfolios/acme-1/permissions", token));
        Assert.True(perms.GetProperty("share").GetBoolean());
        var list = await Json(await Send(HttpMethod.Get, "/portfolios", token));
        Assert.Equal(["acme-1"], list.EnumerateArray().Select(p => p.GetProperty("id").GetString()));

        var (_, otherAdmin) = NewUser(role: "Admin", tenant: OtherTenant);
        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/portfolios/acme-1", otherAdmin)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Send(HttpMethod.Get, "/portfolios/other-1", otherAdmin)).StatusCode);
    }

    [Fact]
    public async Task A_grant_never_crosses_tenants()
    {
        // Even with a grant in OpenFGA, a user of another tenant can't see this tenant's portfolio:
        // the token's tenant check comes first.
        var (sub, token) = NewUser(tenant: OtherTenant);
        await Grant(sub, "owner");

        Assert.Equal(HttpStatusCode.NotFound, (await Send(HttpMethod.Get, "/portfolios/acme-1", token)).StatusCode);
    }

    [Fact]
    public async Task OpenFga_down_is_503_never_allow()
    {
        var (_, token) = NewUser(role: "Admin");
        factory.Access.Down = true;
        try
        {
            var res = await Send(HttpMethod.Get, "/portfolios/acme-1", token);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
            Assert.Equal("authorization_unavailable", (await Json(res)).GetProperty("error").GetString());
        }
        finally
        {
            factory.Access.Down = false;
        }
    }
}

public sealed class UnconfiguredOpenFgaTests(UnconfiguredOpenFgaFactory factory) : IClassFixture<UnconfiguredOpenFgaFactory>
{
    [Fact]
    public async Task Portfolios_are_503_the_rest_still_works()
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestTokens.Create());

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/portfolios/acme-1")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/whoami")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin/ping")).StatusCode);
    }
}

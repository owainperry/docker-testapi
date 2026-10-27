using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DemoApi.Tests;

public sealed class ApiTests(DemoApiFactory factory) : IClassFixture<DemoApiFactory>
{
    private async Task<HttpResponseMessage> Get(string path, string? token)
    {
        var client = factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.SendAsync(req);
    }

    private static async Task<JsonElement> Json(HttpResponseMessage res) =>
        JsonDocument.Parse(await res.Content.ReadAsStringAsync()).RootElement;

    private static string[] Strings(JsonElement e) => e.EnumerateArray().Select(x => x.GetString()!).ToArray();

    [Fact]
    public async Task Healthz_needs_no_token()
    {
        var res = await Get("/healthz", null);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await Json(res)).GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task No_token_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/whoami", null)).StatusCode);
    }

    [Fact]
    public async Task Valid_token_is_accepted()
    {
        var token = TestTokens.Create();

        var whoami = await Get("/whoami", token);
        Assert.Equal(HttpStatusCode.OK, whoami.StatusCode);
        var body = await Json(whoami);
        Assert.Equal(TestTokens.ApiAppId, body.GetProperty("appId").GetString());
        Assert.Equal([TestTokens.ApiAppId], Strings(body.GetProperty("aud")));
        Assert.Equal(TestTokens.Issuer, body.GetProperty("iss").GetString());
        Assert.Equal(TestTokens.Tenant, body.GetProperty("tenant").GetString());
        Assert.Equal("user", body.GetProperty("caller").GetString());
        Assert.Equal(["Admin"], Strings(body.GetProperty("roles")));
        Assert.Contains("q_tenant", Strings(body.GetProperty("claimNames")));
        Assert.True(body.GetProperty("exp").GetInt64() > 0);

        var portfolio = await Get("/portfolios/acme-1", token);
        Assert.Equal(HttpStatusCode.OK, portfolio.StatusCode);
        Assert.Equal("acme-1", (await Json(portfolio)).GetProperty("id").GetString());
    }

    [Theory]
    [InlineData(TestTokens.EnvironmentClientId)]
    [InlineData(TestTokens.VmcpAppId)]
    public async Task Other_audiences_are_401(string aud)
    {
        var token = TestTokens.Create(c => { c["aud"] = aud; c["applicationId"] = aud; });

        var res = await Get("/whoami", token);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("audience", res.Headers.WwwAuthenticate.ToString());
    }

    [Theory]
    [InlineData("https://app-4a5u5lob7bvi.frontegg.com/")]
    [InlineData("https://evil.example.com")]
    [InlineData("https://app-other.frontegg.com")]
    public async Task Wrong_issuer_is_401(string issuer)
    {
        var res = await Get("/whoami", TestTokens.Create(issuer: issuer));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("invalid_token", res.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Expired_token_is_401()
    {
        var res = await Get("/whoami", TestTokens.Create(expires: DateTime.UtcNow.AddMinutes(-5)));
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        Assert.Contains("expired", res.Headers.WwwAuthenticate.ToString());
    }

    [Fact]
    public async Task Token_signed_with_another_key_is_401()
    {
        // Same kid as the trusted key, so it's the signature check that refuses it.
        var forged = new Microsoft.IdentityModel.Tokens.RsaSecurityKey(TestTokens.OtherKey.Rsa) { KeyId = TestTokens.SigningKey.KeyId };
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/whoami", TestTokens.Create(key: forged))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/whoami", TestTokens.Create(key: TestTokens.OtherKey))).StatusCode);
    }

    [Fact]
    public async Task Unsigned_token_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/whoami", TestTokens.Unsigned())).StatusCode);
    }

    [Fact]
    public async Task Hs256_token_is_401()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await Get("/whoami", TestTokens.HmacSigned())).StatusCode);
    }

    [Fact]
    public async Task Missing_tenant_is_403_except_on_whoami()
    {
        // What Frontegg issues when the targeting rule doesn't match: no q_* claims at all.
        var token = TestTokens.Create(c => c.Remove("q_tenant"));

        Assert.Equal(HttpStatusCode.OK, (await Get("/whoami", token)).StatusCode);

        foreach (var path in new[] { "/portfolios/acme-1", "/admin/ping" })
        {
            var res = await Get(path, token);
            Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
            Assert.Equal("missing_tenant", (await Json(res)).GetProperty("error").GetString());
        }
    }

    [Theory]
    [InlineData("other-1")]
    [InlineData("does-not-exist")]
    public async Task Other_tenants_portfolio_looks_absent(string id)
    {
        Assert.Equal(HttpStatusCode.NotFound, (await Get($"/portfolios/{id}", TestTokens.Create())).StatusCode);
    }

    [Fact]
    public async Task Admin_role_reaches_admin_ping()
    {
        var res = await Get("/admin/ping", TestTokens.Create(c => c["roles"] = new[] { "User", "Admin" }));
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True((await Json(res)).GetProperty("admin").GetBoolean());
    }

    [Fact]
    public async Task Non_admin_is_403_on_admin_ping()
    {
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Get("/admin/ping", TestTokens.Create(c => c["roles"] = new[] { "User" }))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Get("/admin/ping", TestTokens.Create(c => c.Remove("roles")))).StatusCode);
        // Role keys are exact-case.
        Assert.Equal(HttpStatusCode.Forbidden,
            (await Get("/admin/ping", TestTokens.Create(c => c["roles"] = new[] { "admin" }))).StatusCode);
    }

    /// <summary>
    /// Frontegg's template engine turns arrays into strings, which is why the API reads Frontegg's
    /// own <c>roles</c> (a real array) rather than a templated <c>q_roles</c>. A single string is
    /// tolerated as a one-element list, but nothing should rely on it: a templated multi-role
    /// array would arrive as one comma-joined string, not as separate roles.
    /// </summary>
    [Fact]
    public async Task Roles_as_a_string_is_tolerated_not_relied_on()
    {
        var token = TestTokens.Create(c => c["roles"] = "Admin");

        var body = await Json(await Get("/whoami", token));
        Assert.Equal(["Admin"], Strings(body.GetProperty("roles")));
        Assert.Equal(HttpStatusCode.OK, (await Get("/admin/ping", token)).StatusCode);
    }

    [Fact]
    public async Task Email_with_apostrophe_is_returned_unchanged()
    {
        const string email = "o'test2@peek.org.uk";
        var res = await Get("/whoami", TestTokens.Create(c => { c["q_email"] = email; c["email"] = email; }));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains(email, await res.Content.ReadAsStringAsync());
        Assert.Equal(email, (await Json(res)).GetProperty("email").GetString());
    }
}

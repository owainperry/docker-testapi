using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace DemoApi.Tests;

/// <summary>Mints tokens shaped like Frontegg's, signed with a key generated per test run.</summary>
public static class TestTokens
{
    public const string Issuer = "https://app-4a5u5lob7bvi.frontegg.com";
    public const string ApiAppId = "aaaaaaaa-0000-4000-8000-000000000001";
    public const string EnvironmentClientId = "9ce64ee6-b754-4b1c-8076-a9adf14362dc";
    public const string VmcpAppId = "99396ec4-1f3c-4a64-a41b-71110a4f8ae9";
    public const string Tenant = "d3275653-4f23-4862-843d-9b8052d5f9af";

    public static readonly RsaSecurityKey SigningKey = NewRsaKey("test-key");
    public static readonly RsaSecurityKey OtherKey = NewRsaKey("other-key");

    private static RsaSecurityKey NewRsaKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    /// <summary>The claims a token through the API app carries with the qauth-api template.</summary>
    public static Dictionary<string, object> DefaultClaims() => new()
    {
        ["sub"] = "11111111-2222-3333-4444-555555555555",
        ["aud"] = ApiAppId,
        ["applicationId"] = ApiAppId,
        ["q_tenant"] = Tenant,
        ["q_caller"] = "user",
        ["q_email"] = "testuser1@peek.org.uk",
        ["email"] = "testuser1@peek.org.uk",
        ["roles"] = new[] { "Admin" },
        ["type"] = "userToken",
    };

    public static string Create(
        Action<Dictionary<string, object>>? edit = null,
        string issuer = Issuer,
        DateTime? expires = null,
        SecurityKey? key = null,
        string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var claims = DefaultClaims();
        edit?.Invoke(claims);

        var exp = expires ?? DateTime.UtcNow.AddMinutes(5);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Claims = claims,
            IssuedAt = exp.AddMinutes(-5),
            NotBefore = exp.AddMinutes(-5),
            Expires = exp,
            SigningCredentials = new SigningCredentials(key ?? SigningKey, algorithm),
        };
        return new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false }.CreateToken(descriptor);
    }

    public static string HmacSigned() => Create(
        key: new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32)) { KeyId = "test-key" },
        algorithm: SecurityAlgorithms.HmacSha256);

    /// <summary>An unsigned token: <c>{"alg":"none"}</c> header, empty signature.</summary>
    public static string Unsigned()
    {
        var claims = DefaultClaims();
        claims["iss"] = Issuer;
        claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        return $"{B64(new { alg = "none", typ = "JWT" })}.{B64(claims)}.";
    }

    private static string B64(object o) => Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(o)));
}

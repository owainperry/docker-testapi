using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

namespace DemoApi.Tests;

/// <summary>
/// The real app, with Frontegg's discovery/JWKS replaced by a static configuration holding the test
/// key, so no network is needed. Everything else (issuer, audience, validation rules) is the app's own.
/// </summary>
public sealed class DemoApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseSetting("Frontegg:Issuer", TestTokens.Issuer);
        builder.UseSetting("Frontegg:Audience", TestTokens.ApiAppId);

        builder.ConfigureTestServices(services =>
            services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
            {
                // A non-null Configuration makes JwtBearer use it instead of fetching metadata.
                var config = new OpenIdConnectConfiguration { Issuer = TestTokens.Issuer };
                config.SigningKeys.Add(TestTokens.SigningKey);
                o.Configuration = config;
            }));
    }
}

using System.Text.Encodings.Web;
using DemoApi;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });

var issuer = builder.Configuration["Frontegg:Issuer"];
var audience = builder.Configuration["Frontegg:Audience"];
if (string.IsNullOrWhiteSpace(issuer))
    throw new InvalidOperationException("Frontegg:Issuer is not set.");
if (string.IsNullOrWhiteSpace(audience) || audience == "<API_APP_ID>")
    throw new InvalidOperationException(
        "Frontegg:Audience is not set. Set it to the API's Frontegg app ID, e.g. Frontegg__Audience=<app id>.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        // Signing keys come from <issuer>/.well-known/openid-configuration -> jwks_uri.
        o.Authority = issuer;
        o.RequireHttpsMetadata = true;
        o.MapInboundClaims = false;
        o.IncludeErrorDetails = true;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            // Exact, ordinal match (the library default): "https://…frontegg.com/" with a trailing slash fails.
            ValidIssuer = issuer,
            ValidateAudience = true,
            // Only this app. Frontegg's default aud (the environment client ID) and other apps' IDs must fail.
            ValidAudience = audience,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = QAuthClaims.RolesClaim,
            NameClaimType = "email",
        };
        o.Events = new JwtBearerEvents
        {
            OnAuthenticationFailed = ctx =>
            {
                var reason = ctx.Exception.Message;
                ctx.HttpContext.Items[RequestLog.AuthFailureKey] = reason;
                ctx.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("DemoApi.Auth")
                    .LogWarning("Token refused on {Path}: {Reason}", ctx.Request.Path, reason);
                return Task.CompletedTask;
            },
        };
    });

builder.Services.AddAuthorizationBuilder().AddQAuthPolicies();
builder.Services.AddSingleton<IAuthorizationHandler, TenantRequirement>();
builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, QAuthAuthorizationResultHandler>();

// Data-level access (who can view, edit, share which portfolio) lives in OpenFGA. Without it the API
// still starts, so /whoami and /admin/ping work, but every /portfolios call answers 503.
var fga = builder.Configuration.GetSection("OpenFga").Get<OpenFgaOptions>() ?? new OpenFgaOptions();
if (fga.IsConfigured)
{
    builder.Services.AddSingleton(OpenFgaPortfolioAccess.CreateClient(fga));
    builder.Services.AddSingleton<IPortfolioAccess, OpenFgaPortfolioAccess>();
}
else
{
    builder.Services.AddSingleton<IPortfolioAccess, UnconfiguredPortfolioAccess>();
}

// Keep e-mail addresses like o'test2@… readable in responses (default encoder writes ' as ').
builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

var app = builder.Build();

app.UseMiddleware<RequestLog>();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/healthz", () => Results.Ok(new { ok = true }));

app.MapGet("/whoami", (HttpContext http) =>
{
    var user = http.User;
    var q = QAuthClaims.From(user);
    return Results.Ok(new
    {
        q.Subject,
        q.Email,
        q.Tenant,
        q.Roles,
        q.Caller,
        q.AppId,
        Aud = user.FindAll("aud").Select(c => c.Value).ToArray(),
        Iss = user.FindFirst("iss")?.Value,
        Exp = long.TryParse(user.FindFirst("exp")?.Value, out var exp) ? exp : (long?)null,
        ClaimNames = user.Claims.Select(c => c.Type).Distinct().Order(StringComparer.Ordinal).ToArray(),
    });
}).RequireAuthorization();

app.MapPortfolios();

app.MapGet("/admin/ping", () => Results.Ok(new { admin = true }))
    .RequireAuthorization(QAuthPolicies.Admin);

if (!fga.IsConfigured)
    app.Logger.LogWarning("OpenFGA is not configured (OpenFga__ApiUrl, OpenFga__StoreId): /portfolios answers 503.");
else
    app.Logger.LogInformation("OpenFGA: {ApiUrl}, store {StoreId}, model {ModelId}",
        fga.ApiUrl, fga.StoreId, string.IsNullOrWhiteSpace(fga.AuthorizationModelId) ? "latest" : fga.AuthorizationModelId);

app.Run();

public partial class Program;

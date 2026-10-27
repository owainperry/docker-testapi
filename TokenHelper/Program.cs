// Gets a real Frontegg access token for one app as a PUBLIC client: authorization code + PKCE
// (S256), token endpoint auth "none". No client secret: the user's own sign-in plus the PKCE
// verifier is all it takes. Prints ONLY the access token to stdout; everything else to stderr.
// (Not the device flow: Frontegg's hosted login has no page to enter a device code.)
// No refresh, no cache, no files. The ID token is ignored on purpose: its aud is the environment
// client ID even when signing in through an app, so validating it against the app ID would fail.

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

const string BaseUrl = "https://app-4a5u5lob7bvi.frontegg.com";
const string RedirectUri = "http://localhost:8765/callback";

var appId = ArgValue(args, "--app-id");
if (string.IsNullOrWhiteSpace(appId))
{
    Console.Error.WriteLine("usage: TokenHelper --app-id <frontegg app id>");
    return 2;
}

var state = Base64Url(RandomNumberGenerator.GetBytes(16));
var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

// 1. Callback listener. Kestrel's "localhost" listens on both 127.0.0.1 and ::1, so it doesn't
//    matter which one the browser resolves localhost to.
var callback = new TaskCompletionSource<(string? Code, string? Error)>(TaskCreationOptions.RunContinuationsAsynchronously);
var builder = WebApplication.CreateSlimBuilder();
builder.Logging.ClearProviders();
builder.WebHost.UseUrls("http://localhost:8765");
var listener = builder.Build();
listener.MapGet("/callback", (HttpRequest req) =>
{
    var q = req.Query;
    if (q["state"] != state)
        callback.TrySetResult((null, "state mismatch"));
    else if (!string.IsNullOrEmpty(q["error"]))
        callback.TrySetResult((null, $"{q["error"]}: {q["error_description"]}"));
    else
        callback.TrySetResult((q["code"], null));
    return Results.Text("Done. You can close this tab and go back to the terminal.");
});
await listener.StartAsync();

// 2. Browser.
var authorizeUrl = $"{BaseUrl}/oauth/authorize?" + Query(
    ("response_type", "code"),
    ("client_id", appId),
    ("redirect_uri", RedirectUri),
    ("scope", "openid profile email"),
    ("state", state),
    ("code_challenge", challenge),
    ("code_challenge_method", "S256"));

Console.Error.WriteLine($"""
    Signing in through app {appId}.
    To switch user, first open {BaseUrl}/oauth/logout in the browser,
    or Frontegg silently reuses the last session.

    If the browser doesn't open, go to:
    {authorizeUrl}

    """);
OpenBrowser(authorizeUrl);

// 3. Code + state.
var (code, error) = await callback.Task.WaitAsync(TimeSpan.FromMinutes(5));
await listener.StopAsync();
if (error is not null || string.IsNullOrEmpty(code))
{
    Console.Error.WriteLine($"Sign-in failed: {error ?? "no code"}");
    return 1;
}

// 4. Code -> token as a public client: client_id + code_verifier, no client_secret.
using var http = new HttpClient();
var tokenResponse = await http.PostAsync($"{BaseUrl}/oauth/token", new FormUrlEncodedContent(new Dictionary<string, string>
{
    ["grant_type"] = "authorization_code",
    ["code"] = code,
    ["redirect_uri"] = RedirectUri,
    ["client_id"] = appId,
    ["code_verifier"] = verifier,
}));
var body = await tokenResponse.Content.ReadAsStringAsync();
if (!tokenResponse.IsSuccessStatusCode)
{
    Console.Error.WriteLine($"Token request failed: {(int)tokenResponse.StatusCode} {body}");
    return 1;
}

var accessToken = JsonDocument.Parse(body).RootElement.GetProperty("access_token").GetString()!;

// 5. Claims to stderr, token to stdout.
var payload = JsonDocument.Parse(Base64UrlDecode(accessToken.Split('.')[1])).RootElement;
Console.Error.WriteLine("Access token claims:");
foreach (var name in new[] { "aud", "applicationId", "q_tenant", "roles", "exp" })
{
    var value = payload.TryGetProperty(name, out var v) ? v.GetRawText() : "(absent)";
    if (name == "exp" && v.ValueKind == JsonValueKind.Number)
        value += $"  ({DateTimeOffset.FromUnixTimeSeconds(v.GetInt64()).ToLocalTime():HH:mm:ss})";
    Console.Error.WriteLine($"  {name,-14} {value}");
}
if (!payload.TryGetProperty("q_tenant", out _))
    Console.Error.WriteLine("  WARNING: no q_tenant. The targeting rule didn't match; Frontegg issued its default token.");

Console.Out.WriteLine(accessToken);
return 0;

static string? ArgValue(string[] args, string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static string Query(params (string Key, string Value)[] items) =>
    string.Join("&", items.Select(i => $"{Uri.EscapeDataString(i.Key)}={Uri.EscapeDataString(i.Value)}"));

static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

static byte[] Base64UrlDecode(string s)
{
    s = s.Replace('-', '+').Replace('_', '/');
    return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
}

static void OpenBrowser(string url)
{
    try
    {
        if (OperatingSystem.IsWindows())
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        else
            // Swallow the opener's own output so it can't end up in $(...) next to the token.
            Process.Start(new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", [url])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
    }
    catch
    {
        // The URL is already on stderr.
    }
}

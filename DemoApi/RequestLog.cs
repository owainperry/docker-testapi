namespace DemoApi;

/// <summary>One structured line per request. Never logs the token.</summary>
public sealed class RequestLog(RequestDelegate next, ILogger<RequestLog> logger)
{
    public const string AuthFailureKey = "qauth.auth_failure";

    public async Task InvokeAsync(HttpContext http)
    {
        await next(http);

        var user = http.User;
        var status = http.Response.StatusCode;
        var reason = status == StatusCodes.Status401Unauthorized
            ? http.Items[AuthFailureKey] as string
              ?? (http.Request.Headers.Authorization.Count == 0 ? "no bearer token" : "token not accepted")
            : null;

        logger.LogInformation(
            "{Method} {Path} -> {Status} sub={Sub} q_tenant={Tenant} applicationId={AppId} reason={Reason}",
            http.Request.Method, http.Request.Path.Value, status,
            user.FindFirst(QAuthClaims.SubjectClaim)?.Value,
            user.FindFirst(QAuthClaims.TenantClaim)?.Value,
            user.FindFirst(QAuthClaims.AppIdClaim)?.Value,
            reason);
    }
}

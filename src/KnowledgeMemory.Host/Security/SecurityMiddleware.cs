using KnowledgeMemory.Core;

namespace KnowledgeMemory.Host.Security;

public enum Caller { None, Machine, Browser }

/// <summary>
/// Request guards in one place:
/// 1. Host header must be our exact loopback host:port (DNS rebinding).
/// 2. A present Origin must be our own origin (no CORS, no cross-site requests).
/// 3. Authentication: machine bearer token or browser session cookie.
/// 4. Browser mutations require Origin + CSRF header + JSON content type.
/// 5. Security headers on every response.
/// </summary>
public sealed class SecurityMiddleware
{
    public const string CsrfHeader = "X-CSRF-Token";
    public const string CallerItem = "km.caller";

    private readonly RequestDelegate _next;
    private readonly HashSet<string> _allowedHosts;
    private readonly HashSet<string> _allowedOrigins;
    private readonly string _token;
    private readonly BrowserSessions _sessions;
    private readonly string _cookieName;

    public SecurityMiddleware(RequestDelegate next, AppOptions o, ServiceSecrets secrets, BrowserSessions sessions)
    {
        _next = next;
        _token = secrets.ApiToken;
        _sessions = sessions;
        _cookieName = CookieName(o);
        var port = o.Server.Port;
        _allowedHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"127.0.0.1:{port}", $"localhost:{port}", $"[::1]:{port}" };
        _allowedOrigins = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { $"http://127.0.0.1:{port}", $"http://localhost:{port}", $"http://[::1]:{port}" };
    }

    public static string CookieName(AppOptions o) => $"km_session_{o.Server.Port}";

    public async Task InvokeAsync(HttpContext ctx)
    {
        var h = ctx.Response.Headers;
        h["X-Content-Type-Options"] = "nosniff";
        h["Referrer-Policy"] = "no-referrer";
        h["X-Frame-Options"] = "DENY";
        h["Cross-Origin-Opener-Policy"] = "same-origin";
        h["Cross-Origin-Resource-Policy"] = "same-origin";
        h["Content-Security-Policy"] =
            "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; font-src 'self'; " +
            "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";
        h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";

        if (!_allowedHosts.Contains(ctx.Request.Host.Value ?? ""))
        {
            await Deny(ctx, 400, ErrorCodes.Forbidden, "Invalid Host header.");
            return;
        }
        var origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !_allowedOrigins.Contains(origin))
        {
            await Deny(ctx, 403, ErrorCodes.Forbidden, "Cross-origin requests are not allowed.");
            return;
        }
        var fetchSite = ctx.Request.Headers["Sec-Fetch-Site"].ToString();
        if (fetchSite is "cross-site" or "same-site")
        {
            await Deny(ctx, 403, ErrorCodes.Forbidden, "Cross-site requests are not allowed.");
            return;
        }

        var path = ctx.Request.Path;
        var isApi = path.StartsWithSegments("/api");
        var isMcp = path.StartsWithSegments("/mcp");
        if (!isApi && !isMcp)
        {
            if (!HttpMethods.IsGet(ctx.Request.Method) && !HttpMethods.IsHead(ctx.Request.Method))
            {
                await Deny(ctx, 405, ErrorCodes.Forbidden, "Method not allowed.");
                return;
            }
            await _next(ctx);
            return;
        }

        h.CacheControl = "no-store";
        var caller = Authenticate(ctx, out var session);
        ctx.Items[CallerItem] = caller;
        var isMutation = !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method));

        // The only anonymous endpoint: exchanging a one-time bootstrap code for a session.
        var anonymousAllowed = isApi && path.Equals("/api/auth/exchange") && HttpMethods.IsPost(ctx.Request.Method);

        if (isMcp && caller != Caller.Machine)
        {
            await Deny(ctx, 401, ErrorCodes.Unauthorized, "MCP endpoint requires the local API token (Authorization: Bearer).");
            return;
        }
        if (caller == Caller.None && !anonymousAllowed && !path.Equals("/api/session"))
        {
            await Deny(ctx, 401, ErrorCodes.Unauthorized, "Not authenticated. Open the UI with: KnowledgeMemory.exe open-ui --config \"<path>\"");
            return;
        }

        if (isApi && isMutation)
        {
            var contentType = ctx.Request.ContentType ?? "";
            if (!contentType.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) && !HttpMethods.IsDelete(ctx.Request.Method))
            {
                await Deny(ctx, 415, ErrorCodes.Validation, "Content-Type must be application/json.");
                return;
            }
            if (caller == Caller.Browser || anonymousAllowed)
            {
                // Browsers always send Origin on POST/DELETE; require it to be ours.
                if (origin.Length == 0)
                {
                    await Deny(ctx, 403, ErrorCodes.Forbidden, "Missing Origin header.");
                    return;
                }
                if (caller == Caller.Browser && !TokenStore.FixedTimeEquals(ctx.Request.Headers[CsrfHeader].ToString(), session!.Csrf))
                {
                    await Deny(ctx, 403, ErrorCodes.Forbidden, "Missing or invalid CSRF token. Reload the page.");
                    return;
                }
            }
        }
        await _next(ctx);
    }

    private Caller Authenticate(HttpContext ctx, out BrowserSessions.Session? session)
    {
        session = null;
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth.StartsWith("Bearer ", StringComparison.Ordinal))
            return TokenStore.FixedTimeEquals(auth["Bearer ".Length..].Trim(), _token) ? Caller.Machine : Caller.None;
        if (ctx.Request.Cookies.TryGetValue(_cookieName, out var sid))
        {
            session = _sessions.Get(sid);
            if (session is not null) return Caller.Browser;
        }
        return Caller.None;
    }

    internal static Task Deny(HttpContext ctx, int status, string code, string message)
    {
        ctx.Response.StatusCode = status;
        return ctx.Response.WriteAsJsonAsync(new ErrorEnvelope(new ErrorBody(code, message)));
    }
}

public sealed record ServiceSecrets(string ApiToken, string InstanceId);

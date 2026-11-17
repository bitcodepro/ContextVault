using System.Text;
using System.Text.Json;
using KnowledgeMemory.Core;
using KnowledgeMemory.Host.Security;
using KnowledgeMemory.Storage;

namespace KnowledgeMemory.Host.Api;

public sealed record ExchangeRequest(string? Code);
public sealed record DeleteRequest(long? ExpectedVersion);
public sealed record RestoreRequest(long? ExpectedVersion);

/// <summary>Typed local REST API used by the web UI and by the stdio MCP adapter.</summary>
public static class ApiEndpoints
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void Map(WebApplication app)
    {
        var api = app.MapGroup("/api");

        api.MapGet("/identity", (AppOptions o, ServiceSecrets s) => Results.Json(Identity(o, s), Json));

        // ---- browser session
        api.MapPost("/auth/bootstrap", (HttpContext ctx, BrowserSessions sessions) =>
        {
            if (CallerOf(ctx) != Caller.Machine) return Forbidden("Only the local open-ui command can issue a login link.");
            return Results.Json(new { code = sessions.IssueBootstrap(), expiresInSeconds = (int)BrowserSessions.BootstrapTtl.TotalSeconds }, Json);
        });

        api.MapPost("/auth/exchange", (HttpContext ctx, ExchangeRequest body, BrowserSessions sessions, AppOptions o) =>
        {
            var s = sessions.Exchange(body.Code);
            if (s is null)
                return Error(new KnowledgeException(ErrorCodes.Unauthorized, "The login link is invalid, expired or was already used. Run open-ui again."));
            // Plain-HTTP loopback: Secure is not set (the browser would drop the cookie); HttpOnly + SameSite=Strict.
            ctx.Response.Cookies.Append(SecurityMiddleware.CookieName(o), s.Id, new CookieOptions
            {
                HttpOnly = true,
                SameSite = SameSiteMode.Strict,
                Secure = false,
                Path = "/",
                MaxAge = BrowserSessions.SessionTtl,
                IsEssential = true,
            });
            return Results.Json(new { authenticated = true, csrfToken = s.Csrf }, Json);
        });

        api.MapGet("/session", (HttpContext ctx, BrowserSessions sessions, AppOptions o) =>
        {
            ctx.Request.Cookies.TryGetValue(SecurityMiddleware.CookieName(o), out var sid);
            var s = sessions.Get(sid);
            return s is null
                ? Results.Json(new { authenticated = false }, Json, statusCode: 401)
                : Results.Json(new { authenticated = true, csrfToken = s.Csrf }, Json);
        });

        api.MapPost("/auth/logout", (HttpContext ctx, BrowserSessions sessions, AppOptions o) =>
        {
            ctx.Request.Cookies.TryGetValue(SecurityMiddleware.CookieName(o), out var sid);
            sessions.Remove(sid);
            ctx.Response.Cookies.Delete(SecurityMiddleware.CookieName(o));
            return Results.Json(new { authenticated = false }, Json);
        });

        // ---- knowledge
        api.MapGet("/documents", (string? q, string? project, string? externalKey, string? kind, string? status, int? sinceDays, int? limit, string? cursor,
            KnowledgeStore store, CancellationToken ct) => Run(async () =>
                await store.SearchAsync(new SearchQuery { Query = q, Project = project, ExternalKey = externalKey, Kind = kind, Status = status, SinceDays = sinceDays, Limit = limit, Cursor = cursor }, ct)));

        // ---- version history (read: browser and machine; restore: user action in the browser, like delete)
        api.MapGet("/documents/{id}/versions", (string id, KnowledgeStore store) =>
            Run(() => Task.FromResult(store.ListVersions(id))));

        api.MapGet("/documents/{id}/versions/{version:long}", (string id, long version, KnowledgeStore store) =>
            Run(() => Task.FromResult(store.GetVersion(id, version))));

        api.MapPost("/documents/{id}/versions/{version:long}/restore", (string id, long version, RestoreRequest body, KnowledgeStore store) =>
        {
            if (body.ExpectedVersion is null) return Task.FromResult(Error(KnowledgeException.Invalid("expectedVersion is required.")));
            return Run(async () => await store.RestoreAsync(id, version, body.ExpectedVersion.Value, CancellationToken.None));
        });

        api.MapGet("/documents/{id}", (string id, int? offset, int? maxChars, KnowledgeStore store, CancellationToken ct) =>
            Run(async () => await store.GetAsync(id, offset, maxChars, ct)));

        api.MapPost("/documents", (HttpContext ctx, SaveRequest body, KnowledgeStore store, CancellationToken ct) =>
        {
            if (CallerOf(ctx) != Caller.Machine) return Task.FromResult(Forbidden("Cards are saved by AI clients through MCP, not from the browser."));
            return Run(async () => await store.SaveAsync(body, ct));
        });

        api.MapDelete("/documents/{id}", (string id, long? expectedVersion, KnowledgeStore store) =>
        {
            if (expectedVersion is null) return Task.FromResult(Error(KnowledgeException.Invalid("expectedVersion is required.")));
            // Not bound to the request token: once started, a delete is committed or not; the UI re-reads state.
            return Run(async () => await store.DeleteAsync(id, expectedVersion.Value, CancellationToken.None));
        });

        api.MapGet("/projects", (KnowledgeStore store, CancellationToken ct) => Run(async () => await store.ProjectsAsync(ct)));

        // ---- disk
        api.MapGet("/disk", (DiskManager disk) => Run(() => Task.FromResult(disk.GetStats())));
        api.MapPost("/disk/compact", (DiskManager disk) => Run(async () => await disk.CompactAsync(CancellationToken.None)));
        api.MapPost("/disk/backup", (DiskManager disk) => Run(async () => await disk.BackupAsync(CancellationToken.None)));

        api.MapGet("/export", (string? format, KnowledgeStore store) =>
        {
            var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            if (string.Equals(format, "md", StringComparison.OrdinalIgnoreCase))
                return Results.File(Encoding.UTF8.GetBytes(ExportWriter.Markdown(store.ExportAll())), "text/markdown; charset=utf-8", $"knowledge-{stamp}.md");
            return Results.File(Encoding.UTF8.GetBytes(ExportWriter.JsonExport(store.ExportAll())), "application/json; charset=utf-8", $"knowledge-{stamp}.json");
        });

        api.MapFallback(() => Results.Json(new ErrorEnvelope(new ErrorBody(ErrorCodes.NotFound, "Unknown API endpoint.")), Json, statusCode: 404));
    }

    public static IdentityInfo Identity(AppOptions o, ServiceSecrets s) => new()
    {
        InstanceId = s.InstanceId,
        DataDirectoryHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.UTF8.GetBytes(o.ResolvedDataDirectory.ToUpperInvariant())))[..16],
        Version = typeof(ApiEndpoints).Assembly.GetName().Version?.ToString() ?? "0",
    };

    private static Caller CallerOf(HttpContext ctx) => ctx.Items[SecurityMiddleware.CallerItem] is Caller c ? c : Caller.None;

    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Json(await action(), Json);
        }
        catch (KnowledgeException ex)
        {
            return Error(ex);
        }
    }

    public static IResult Error(KnowledgeException ex) =>
        Results.Json(new ErrorEnvelope(new ErrorBody(ex.Code, ex.Message, ex.Details.Count > 0 ? ex.Details : null)), Json, statusCode: ex.HttpStatus);

    private static IResult Forbidden(string message) =>
        Results.Json(new ErrorEnvelope(new ErrorBody(ErrorCodes.Forbidden, message)), Json, statusCode: 403);
}

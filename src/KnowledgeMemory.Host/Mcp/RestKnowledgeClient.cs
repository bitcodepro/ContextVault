using System.Net.Http.Headers;
using System.Net.Http.Json;
using KnowledgeMemory.Core;
using KnowledgeMemory.Host.Api;

namespace KnowledgeMemory.Host.Mcp;

/// <summary>Used by the stdio adapter: forwards the four tool operations to the running serve process.</summary>
public sealed class RestKnowledgeClient : IKnowledgeApi, IDisposable
{
    private readonly HttpClient _http;

    public RestKnowledgeClient(AppOptions o, string token, TimeSpan? timeout = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(o.BaseUrl), Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public async Task<IdentityInfo> IdentityAsync(CancellationToken ct) => await Send<IdentityInfo>(new HttpRequestMessage(HttpMethod.Get, "api/identity"), ct);

    public Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var qs = new List<string>();
        void Add(string name, string? value)
        {
            if (!string.IsNullOrEmpty(value)) qs.Add(name + "=" + Uri.EscapeDataString(value));
        }
        Add("q", q.Query);
        Add("project", q.Project);
        Add("externalKey", q.ExternalKey);
        Add("kind", q.Kind);
        Add("status", q.Status);
        Add("sinceDays", q.SinceDays?.ToString());
        Add("limit", q.Limit?.ToString());
        Add("cursor", q.Cursor);
        return Send<SearchResult>(new HttpRequestMessage(HttpMethod.Get, "api/documents?" + string.Join('&', qs)), ct);
    }

    public Task<DocumentPage> GetAsync(string id, int? offset, int? maxChars, CancellationToken ct)
    {
        var url = $"api/documents/{Uri.EscapeDataString(id)}?offset={offset ?? 0}" + (maxChars is int m ? $"&maxChars={m}" : "");
        return Send<DocumentPage>(new HttpRequestMessage(HttpMethod.Get, url), ct);
    }

    public Task<SaveResult> SaveAsync(SaveRequest request, CancellationToken ct) =>
        Send<SaveResult>(new HttpRequestMessage(HttpMethod.Post, "api/documents") { Content = JsonContent.Create(request, options: ApiEndpoints.Json) }, ct);

    public Task<IReadOnlyList<ProjectInfo>> ProjectsAsync(CancellationToken ct) =>
        Send<IReadOnlyList<ProjectInfo>>(new HttpRequestMessage(HttpMethod.Get, "api/projects"), ct);

    private async Task<T> Send<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await _http.SendAsync(req, ct);
        if (res.IsSuccessStatusCode)
            return (await res.Content.ReadFromJsonAsync<T>(ApiEndpoints.Json, ct))!;
        ErrorEnvelope? env = null;
        try { env = await res.Content.ReadFromJsonAsync<ErrorEnvelope>(ApiEndpoints.Json, ct); }
        catch (System.Text.Json.JsonException) { }
        if (env?.Error is { } e)
            throw new KnowledgeException(e.Code, e.Message, e.Details);
        throw new KnowledgeException(ErrorCodes.ServiceUnavailable, $"Knowledge Memory service answered HTTP {(int)res.StatusCode}.");
    }

    public void Dispose() => _http.Dispose();
}

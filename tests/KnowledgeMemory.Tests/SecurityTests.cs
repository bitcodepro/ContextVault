using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using KnowledgeMemory.Core;
using KnowledgeMemory.Host;

namespace KnowledgeMemory.Tests;

public sealed class SecurityTests : IAsyncLifetime
{
    private TempServer _s = null!;
    private string Origin => _s.BaseUrl;

    public async Task InitializeAsync() => _s = await TempServer.StartAsync();
    public async Task DisposeAsync() => await _s.DisposeAsync();

    private async Task<string> SaveCardAsync(string title = "Карточка", string content = "Текст")
    {
        using var m = _s.MachineClient();
        var res = await m.PostAsJsonAsync("/api/documents", Samples.Card(title: title, content: content, key: Guid.NewGuid().ToString("N")[..6]));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
    }

    private async Task<string> BootstrapAsync()
    {
        using var m = _s.MachineClient();
        var res = await m.PostAsync("/api/auth/bootstrap", JsonContent.Create(new { }));
        res.EnsureSuccessStatusCode();
        return (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString()!;
    }

    /// <summary>Logs a browser in the way the UI does and returns the client and CSRF token.</summary>
    private async Task<(HttpClient Client, string Csrf)> BrowserAsync()
    {
        var code = await BootstrapAsync();
        var c = _s.AnonymousClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/exchange") { Content = JsonContent.Create(new { code }) };
        req.Headers.Add("Origin", Origin);
        var res = await c.SendAsync(req);
        res.EnsureSuccessStatusCode();
        var setCookie = string.Join(";", res.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", setCookie, StringComparison.OrdinalIgnoreCase);
        var csrf = (await res.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("csrfToken").GetString()!;
        return (c, csrf);
    }

    [Fact]
    public async Task Requests_without_auth_get_no_documents_and_cannot_mutate()
    {
        var id = await SaveCardAsync();
        using var anon = _s.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/documents")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync($"/api/documents/{id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.DeleteAsync($"/api/documents/{id}?expectedVersion=1")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/documents", Samples.Card())).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/disk/compact", JsonContent.Create(new { }))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/export")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"))).StatusCode);

        anon.DefaultRequestHeaders.Authorization = new("Bearer", "wrong-token-wrong-token-wrong-token");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/documents")).StatusCode);
        Assert.NotNull(_s.Server.Store.Find(id));
    }

    [Fact]
    public async Task Arbitrary_header_does_not_replace_authentication()
    {
        using var anon = _s.AnonymousClient();
        anon.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        anon.DefaultRequestHeaders.Add("X-CSRF-Token", "anything");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/documents")).StatusCode);
    }

    [Theory]
    [InlineData("evil.example")]
    [InlineData("evil.example:{port}")]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:1")]
    [InlineData("localhost.evil.example:{port}")]
    public async Task Forged_host_header_is_rejected_even_with_token(string host)
    {
        using var m = _s.MachineClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/documents");
        req.Headers.Host = host.Replace("{port}", _s.Options.Server.Port.ToString());
        Assert.Equal(HttpStatusCode.BadRequest, (await m.SendAsync(req)).StatusCode);
    }

    [Fact]
    public async Task Foreign_origin_is_rejected_for_machine_and_browser_calls()
    {
        using var m = _s.MachineClient();
        var req = new HttpRequestMessage(HttpMethod.Get, "/api/documents");
        req.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await m.SendAsync(req)).StatusCode);

        var (b, csrf) = await BrowserAsync();
        var id = await SaveCardAsync();
        var del = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}?expectedVersion=1");
        del.Headers.Add("Origin", "http://127.0.0.1:1");
        del.Headers.Add("X-CSRF-Token", csrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(del)).StatusCode);
        Assert.NotNull(_s.Server.Store.Find(id));
    }

    [Fact]
    public async Task Cross_site_form_post_and_missing_csrf_are_rejected()
    {
        var (b, csrf) = await BrowserAsync();
        var id = await SaveCardAsync();

        // Classic cross-site form: urlencoded body, foreign origin, cookie attached.
        var form = new HttpRequestMessage(HttpMethod.Post, "/api/disk/compact")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["x"] = "1" }),
        };
        form.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(form)).StatusCode);

        // Same origin but form content type (no-cors simple request) is rejected.
        var simple = new HttpRequestMessage(HttpMethod.Post, "/api/disk/backup") { Content = new StringContent("{}", Encoding.UTF8, "text/plain") };
        simple.Headers.Add("Origin", Origin);
        simple.Headers.Add("X-CSRF-Token", csrf);
        Assert.Equal(HttpStatusCode.UnsupportedMediaType, (await b.SendAsync(simple)).StatusCode);

        // Cookie but no CSRF token.
        var noCsrf = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}?expectedVersion=1");
        noCsrf.Headers.Add("Origin", Origin);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(noCsrf)).StatusCode);

        // Wrong CSRF token.
        var badCsrf = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}?expectedVersion=1");
        badCsrf.Headers.Add("Origin", Origin);
        badCsrf.Headers.Add("X-CSRF-Token", csrf + "x");
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(badCsrf)).StatusCode);

        // Cookie + CSRF but no Origin.
        var noOrigin = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}?expectedVersion=1");
        noOrigin.Headers.Add("X-CSRF-Token", csrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(noOrigin)).StatusCode);

        // Sec-Fetch-Site cross-site is rejected.
        var xsite = new HttpRequestMessage(HttpMethod.Get, "/api/documents");
        xsite.Headers.Add("Sec-Fetch-Site", "cross-site");
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(xsite)).StatusCode);

        Assert.NotNull(_s.Server.Store.Find(id));

        // The legitimate UI request works.
        var ok = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}?expectedVersion=1");
        ok.Headers.Add("Origin", Origin);
        ok.Headers.Add("X-CSRF-Token", csrf);
        Assert.Equal(HttpStatusCode.OK, (await b.SendAsync(ok)).StatusCode);
        Assert.Null(_s.Server.Store.Find(id));
    }

    [Fact]
    public async Task Get_requests_never_mutate()
    {
        var id = await SaveCardAsync();
        using var m = _s.MachineClient();
        var res = await m.GetAsync($"/api/documents/{id}/delete?expectedVersion=1");
        Assert.NotEqual(HttpStatusCode.OK, res.StatusCode);
        Assert.NotNull(_s.Server.Store.Find(id));
    }

    [Fact]
    public async Task Bootstrap_is_one_time_and_expires()
    {
        var code = await BootstrapAsync();
        using var c1 = _s.AnonymousClient();
        async Task<HttpStatusCode> Exchange(HttpClient c, string value)
        {
            var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/exchange") { Content = JsonContent.Create(new { code = value }) };
            req.Headers.Add("Origin", Origin);
            return (await c.SendAsync(req)).StatusCode;
        }
        Assert.Equal(HttpStatusCode.OK, await Exchange(c1, code));
        using var c2 = _s.AnonymousClient();
        Assert.Equal(HttpStatusCode.Unauthorized, await Exchange(c2, code)); // reused
        Assert.Equal(HttpStatusCode.Unauthorized, await Exchange(c2, "made-up-code-made-up-code-made-up"));

        // Exchange from a foreign origin is refused.
        var other = await BootstrapAsync();
        var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/auth/exchange") { Content = JsonContent.Create(new { code = other }) };
        foreign.Headers.Add("Origin", "https://evil.example");
        Assert.Equal(HttpStatusCode.Forbidden, (await c2.SendAsync(foreign)).StatusCode);

        // Expiry (unit level with a controllable clock).
        var clock = new ManualTime(DateTimeOffset.UtcNow);
        var sessions = new KnowledgeMemory.Host.Security.BrowserSessions(clock);
        var expiring = sessions.IssueBootstrap();
        clock.Advance(KnowledgeMemory.Host.Security.BrowserSessions.BootstrapTtl + TimeSpan.FromSeconds(1));
        Assert.Null(sessions.Exchange(expiring));
    }

    [Fact]
    public async Task Bootstrap_requires_machine_token_and_browser_cannot_save()
    {
        var (b, csrf) = await BrowserAsync();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/bootstrap") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("Origin", Origin);
        req.Headers.Add("X-CSRF-Token", csrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(req)).StatusCode);

        var save = new HttpRequestMessage(HttpMethod.Post, "/api/documents") { Content = JsonContent.Create(Samples.Card()) };
        save.Headers.Add("Origin", Origin);
        save.Headers.Add("X-CSRF-Token", csrf);
        Assert.Equal(HttpStatusCode.Forbidden, (await b.SendAsync(save)).StatusCode);

        // Browser cannot reach the MCP endpoint with its cookie.
        var mcp = new HttpRequestMessage(HttpMethod.Post, "/mcp") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        mcp.Headers.Add("Origin", Origin);
        Assert.Equal(HttpStatusCode.Unauthorized, (await b.SendAsync(mcp)).StatusCode);
    }

    [Fact]
    public async Task Ui_page_and_responses_carry_security_headers_and_no_token()
    {
        using var anon = _s.AnonymousClient();
        var res = await anon.GetAsync("/");
        res.EnsureSuccessStatusCode();
        var html = await res.Content.ReadAsStringAsync();
        var csp = res.Headers.GetValues("Content-Security-Policy").Single();
        Assert.Contains("frame-ancestors 'none'", csp);
        Assert.Contains("script-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.Equal("nosniff", res.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", res.Headers.GetValues("Referrer-Policy").Single());
        Assert.DoesNotContain(_s.Token, html);
        Assert.DoesNotContain("<script>", html); // no inline scripts
        var js = await anon.GetStringAsync("/app.js");
        Assert.DoesNotContain(_s.Token, js);
        Assert.DoesNotContain("innerHTML", js);
        Assert.DoesNotContain("localStorage", js);
        Assert.False(res.Headers.Contains("Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Xss_payload_is_returned_as_plain_json_text()
    {
        var payload = "<img src=x onerror=alert(1)><script>alert(2)</script>";
        var id = await SaveCardAsync(title: payload, content: payload);
        using var m = _s.MachineClient();
        var res = await m.GetAsync($"/api/documents/{id}");
        Assert.StartsWith("application/json", res.Content.Headers.ContentType!.ToString());
        var doc = await res.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(payload, doc.GetProperty("document").GetProperty("title").GetString());
    }

    [Fact]
    public async Task Body_size_is_limited()
    {
        using var m = _s.MachineClient();
        var huge = new StringContent("{\"content\":\"" + new string('a', 3 * 1024 * 1024) + "\"}", Encoding.UTF8, "application/json");
        var res = await m.PostAsync("/api/documents", huge);
        Assert.False(res.IsSuccessStatusCode);
    }

    [Fact]
    public async Task Save_does_not_accept_file_paths_or_extra_fields_to_write_files()
    {
        using var m = _s.MachineClient();
        var json = JsonSerializer.Serialize(new
        {
            requestId = "req-path-000001", project = "../../../windows", title = "t", summary = "s", content = "c",
            path = "C:\\Windows\\evil.txt", dataDirectory = "C:\\", sources = new[] { new { url = "file:///C:/Windows/win.ini" } },
        });
        var res = await m.PostAsync("/api/documents", new StringContent(json, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.False(File.Exists("C:\\Windows\\evil.txt"));
        var files = Directory.GetFiles(_s.Options.ResolvedDataDirectory, "*", SearchOption.AllDirectories).Select(Path.GetFileName);
        Assert.All(files, f => Assert.Matches(@"^(knowledge\.db(-wal|-shm)?|instance\.lock|api-token)$", f));
    }

    [Fact]
    public void Non_loopback_bind_and_bad_ports_are_rejected_by_config()
    {
        foreach (var host in new[] { "0.0.0.0", "192.168.1.10", "::", "example.com" })
        {
            var o = new AppOptions();
            o.Server.Host = host;
            Assert.Throws<KnowledgeException>(() => ConfigLoader.Validate(o));
        }
        foreach (var port in new[] { 80, 0, 70000 })
        {
            var o = new AppOptions();
            o.Server.Port = port;
            Assert.Throws<KnowledgeException>(() => ConfigLoader.Validate(o));
        }
        var unc = new AppOptions();
        unc.Storage.DataDirectory = @"\\server\share\km";
        Assert.Throws<KnowledgeException>(() => ConfigLoader.Validate(unc));
    }

    [Fact]
    public void Data_directory_is_expanded_and_relative_to_config_file()
    {
        var dir = TestPaths.NewDir();
        try
        {
            var cfg = Path.Combine(dir, "cfg", "appsettings.json");
            Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
            File.WriteAllText(cfg, "{ \"Storage\": { \"DataDirectory\": \"db\" } }");
            Assert.Equal(Path.Combine(dir, "cfg", "db"), ConfigLoader.Load(cfg).ResolvedDataDirectory);
            File.WriteAllText(cfg, "{ \"Storage\": { \"DataDirectory\": \"%LOCALAPPDATA%\\\\KM\" } }");
            Assert.Equal(Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA")!, "KM"), ConfigLoader.Load(cfg).ResolvedDataDirectory);
            File.WriteAllText(cfg, "{ \"Storage\": { \"DataDirectory\": \"%KM_UNDEFINED_VAR_X%\\\\KM\" } }");
            Assert.Throws<KnowledgeException>(() => ConfigLoader.Load(cfg));
        }
        finally { TestPaths.TryDelete(dir); }
    }

    [Fact]
    public async Task Second_serve_on_same_data_directory_is_refused_and_busy_port_is_explained()
    {
        var ex = Assert.Throws<KnowledgeException>(() => ServerApp.Create(_s.Options, _ => { }));
        Assert.Contains("already running", ex.Message);

        // Different data directory, same port: a clear port message, not a crash.
        var dir = TestPaths.NewDir();
        var o = TestPaths.Options(dir);
        o.Server.Port = _s.Options.Server.Port;
        var second = ServerApp.Create(o, _ => { });
        try
        {
            var portEx = await Assert.ThrowsAsync<KnowledgeException>(() => second.StartAsync());
            Assert.Contains($"Port {o.Server.Port}", portEx.Message);
        }
        finally
        {
            await second.DisposeAsync();
            TestPaths.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Token_is_not_written_to_logs_or_export()
    {
        await SaveCardAsync();
        using var m = _s.MachineClient();
        var json = await m.GetStringAsync("/api/export?format=json");
        var md = await m.GetStringAsync("/api/export?format=md");
        Assert.DoesNotContain(_s.Token, json);
        Assert.DoesNotContain(_s.Token, md);
        Assert.Contains("knowledge-memory-export", json);
    }

    [Fact]
    public async Task Delete_flow_conflict_and_not_found_via_api()
    {
        var id = await SaveCardAsync();
        var (b, csrf) = await BrowserAsync();
        HttpRequestMessage Del(long v)
        {
            var r = new HttpRequestMessage(HttpMethod.Delete, $"/api/documents/{id}?expectedVersion={v}");
            r.Headers.Add("Origin", Origin);
            r.Headers.Add("X-CSRF-Token", csrf);
            return r;
        }
        var stale = await b.SendAsync(Del(7));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("version_conflict", (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.OK, (await b.SendAsync(Del(1))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await b.SendAsync(Del(1))).StatusCode);
    }
}

public sealed class ManualTime(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

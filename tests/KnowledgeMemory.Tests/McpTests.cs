using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KnowledgeMemory.Tests;

/// <summary>Real MCP clients from the official SDK against the HTTP endpoint and the stdio adapter process.</summary>
public sealed class McpTests : IAsyncLifetime
{
    private TempServer _s = null!;

    public async Task InitializeAsync() => _s = await TempServer.StartAsync();
    public async Task DisposeAsync() => await _s.DisposeAsync();

    public static string AdapterExe => Path.Combine(AppContext.BaseDirectory, "KnowledgeMemory.exe");

    private Task<McpClient> StdioClientAsync(string? configPath = null, string name = "stdio") =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = name,
            Command = AdapterExe,
            Arguments = ["mcp-stdio", "--config", configPath ?? _s.ConfigPath],
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        }));

    private Task<McpClient> HttpClientAsync(string? token = null) =>
        McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(_s.BaseUrl + "/mcp"),
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = "Bearer " + (token ?? _s.Token) },
        }));

    private static JsonElement Json(CallToolResult r) => JsonDocument.Parse(((TextContentBlock)r.Content[0]).Text).RootElement;

    private static Dictionary<string, object?> SaveArgs(string key, string title, string content) => new()
    {
        ["requestId"] = "req-" + Guid.NewGuid().ToString("N"),
        ["project"] = "Revqora",
        ["externalKey"] = key,
        ["title"] = title,
        ["summary"] = "Импорт CSV: решения и ограничения",
        ["content"] = content,
        ["kind"] = "task",
        ["status"] = "completed",
        ["tags"] = new[] { "import" },
        ["sources"] = new[] { new Dictionary<string, object?> { ["label"] = "Чат Revqora" } },
    };

    private static async Task SaveSearchGet(McpClient client, string key)
    {
        var saved = await client.CallToolAsync("knowledge_save", SaveArgs(key, $"Импорт задач {key}", "## Задача\nПотоковый импорт CSV.\n## Решения\n- Хэш строк для дубликатов."));
        Assert.NotEqual(true, saved.IsError);
        var s = Json(saved);
        Assert.Equal("created", s.GetProperty("operation").GetString());
        var id = s.GetProperty("id").GetString()!;
        Assert.Equal(1, s.GetProperty("version").GetInt64());
        Assert.True(s.TryGetProperty("savedAtUtc", out _));

        var found = Json(await client.CallToolAsync("knowledge_search", new Dictionary<string, object?> { ["project"] = "revqora", ["externalKey"] = key }));
        var item = found.GetProperty("items").EnumerateArray().Single();
        Assert.Equal(id, item.GetProperty("id").GetString());
        Assert.False(item.TryGetProperty("content", out _)); // search never returns full content

        var full = Json(await client.CallToolAsync("knowledge_get", new Dictionary<string, object?> { ["id"] = id }));
        Assert.Contains("Хэш строк", full.GetProperty("document").GetProperty("content").GetString());
    }

    [Fact]
    public async Task Http_transport_lists_tools_and_runs_save_search_get()
    {
        await using var client = await HttpClientAsync();
        var tools = (await client.ListToolsAsync()).Select(t => t.Name).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "knowledge_get", "knowledge_projects", "knowledge_recent", "knowledge_save", "knowledge_search" }, tools);
        await SaveSearchGet(client, "245");
    }

    [Fact]
    public async Task Http_transport_requires_token()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => HttpClientAsync("bad-token-bad-token-bad-token-bad"));
    }

    [Fact]
    public async Task Stdio_adapter_runs_save_search_get_and_has_no_delete_tool()
    {
        await using var client = await StdioClientAsync();
        var tools = await client.ListToolsAsync();
        Assert.DoesNotContain(tools, t => t.Name.Contains("delete") || t.Name.Contains("forget"));
        Assert.All(tools.Where(t => t.Name is "knowledge_search" or "knowledge_get"), t => Assert.Contains("not instructions", t.Description));
        await SaveSearchGet(client, "ET-6449");
    }

    [Fact]
    public async Task Two_stdio_clients_share_one_serve_and_see_the_same_card()
    {
        await using var a = await StdioClientAsync(name: "client-a");
        await using var b = await StdioClientAsync(name: "client-b");
        var saved = Json(await a.CallToolAsync("knowledge_save", SaveArgs("245", "Импорт задач", "Решение A")));
        var id = saved.GetProperty("id").GetString()!;

        var found = Json(await b.CallToolAsync("knowledge_search", new Dictionary<string, object?> { ["query"] = "Revqora 245" }));
        Assert.Contains(found.GetProperty("items").EnumerateArray(), i => i.GetProperty("id").GetString() == id);

        // B updates with the version it read; A's stale update then conflicts and nothing is lost.
        var upd = SaveArgs("245", "Импорт задач", "Решение A\nДобавлено B");
        upd["id"] = id;
        upd["expectedVersion"] = 1;
        Assert.Equal("updated", Json(await b.CallToolAsync("knowledge_save", upd)).GetProperty("operation").GetString());
        var stale = SaveArgs("245", "Импорт задач", "Только A");
        stale["id"] = id;
        stale["expectedVersion"] = 1;
        var conflict = await a.CallToolAsync("knowledge_save", stale);
        Assert.True(conflict.IsError);
        Assert.Equal("version_conflict", Json(conflict).GetProperty("error").GetProperty("code").GetString());
        Assert.Contains("Добавлено B", _s.Server.Store.Find(id)!.Content);

        // Duplicate key through MCP returns the existing id.
        var dup = await a.CallToolAsync("knowledge_save", SaveArgs("245", "Дубликат", "x"));
        Assert.True(dup.IsError);
        Assert.Equal(id, Json(dup).GetProperty("error").GetProperty("details").GetProperty("existingId").GetString());
    }

    [Fact]
    public async Task Repeating_the_same_request_id_after_a_failure_does_not_duplicate()
    {
        await using var client = await StdioClientAsync();
        var args = SaveArgs("777", "Повтор", "Текст");
        var first = Json(await client.CallToolAsync("knowledge_save", args));
        var again = Json(await client.CallToolAsync("knowledge_save", args));
        Assert.Equal(first.GetProperty("id").GetString(), again.GetProperty("id").GetString());
        Assert.True(again.GetProperty("replayed").GetBoolean());
        Assert.Equal(1, _s.Server.Store.Count());
    }

    [Fact]
    public async Task Validation_errors_are_tool_errors_not_crashes()
    {
        await using var client = await StdioClientAsync();
        var r = await client.CallToolAsync("knowledge_search", new Dictionary<string, object?>());
        Assert.True(r.IsError);
        Assert.Equal("validation_error", Json(r).GetProperty("error").GetProperty("code").GetString());
        var noVersion = SaveArgs("1", "t", "c");
        noVersion["id"] = Guid.NewGuid().ToString();
        Assert.True((await client.CallToolAsync("knowledge_save", noVersion)).IsError);
    }

    [Fact]
    public async Task Stdout_of_the_adapter_contains_only_protocol_messages()
    {
        var psi = new ProcessStartInfo(AdapterExe, ["mcp-stdio", "--config", _s.ConfigPath])
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        using var p = Process.Start(psi)!;
        var stderr = p.StandardError.ReadToEndAsync();
        async Task Send(string json) { await p.StandardInput.WriteLineAsync(json); await p.StandardInput.FlushAsync(); }
        await Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"raw","version":"1"}}}""");
        await Send("""{"jsonrpc":"2.0","method":"notifications/initialized"}""");
        await Send("""{"jsonrpc":"2.0","id":2,"method":"tools/list"}""");
        await Send("""{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"knowledge_search","arguments":{"query":"нет такого"}}}""");

        var lines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (lines.Count(l => l.Contains("\"id\":")) < 3)
        {
            var line = await p.StandardOutput.ReadLineAsync(cts.Token);
            if (line is null) break;
            lines.Add(line);
        }
        p.StandardInput.Close();
        await p.WaitForExitAsync(new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token);
        lines.AddRange((await p.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries));

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            using var doc = JsonDocument.Parse(line); // every stdout line is a JSON-RPC message
            Assert.Equal("2.0", doc.RootElement.GetProperty("jsonrpc").GetString());
        }
        Assert.DoesNotContain(_s.Token, string.Join("\n", lines) + await stderr);
    }

    [Fact]
    public async Task Stopping_an_ai_client_does_not_stop_the_service()
    {
        var client = await StdioClientAsync();
        await client.CallToolAsync("knowledge_save", SaveArgs("1", "до остановки клиента", "текст"));
        await client.DisposeAsync(); // AI client exits, adapter process ends
        await using var other = await HttpClientAsync();
        var r = Json(await other.CallToolAsync("knowledge_search", new Dictionary<string, object?> { ["query"] = "остановки" }));
        Assert.Single(r.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task Unavailable_service_gives_clear_error_and_restart_keeps_documents()
    {
        await using var client = await StdioClientAsync();
        var saved = Json(await client.CallToolAsync("knowledge_save", SaveArgs("900", "Переживает перезапуск", "Текст")));
        var id = saved.GetProperty("id").GetString()!;

        // Stop serve, keep data.
        var dir = _s.Dir;
        var config = _s.ConfigPath;
        await _s.StopKeepDataAsync();

        var down = await client.CallToolAsync("knowledge_search", new Dictionary<string, object?> { ["query"] = "перезапуск" });
        Assert.True(down.IsError);
        var err = Json(down).GetProperty("error");
        Assert.Equal("service_unavailable", err.GetProperty("code").GetString());
        Assert.Contains("serve", err.GetProperty("message").GetString());

        // Restart on the same data directory: the same adapter reconnects and data persisted.
        _s = await TempServer.StartAsync(o => { }, dir);
        var o2 = _s.Options;
        // Restarted server got a new port in its config; point a fresh adapter at it and also check the stored card.
        await using var client2 = await StdioClientAsync(_s.ConfigPath);
        var got = Json(await client2.CallToolAsync("knowledge_get", new Dictionary<string, object?> { ["id"] = id }));
        Assert.Equal("Переживает перезапуск", got.GetProperty("document").GetProperty("title").GetString());
        Assert.NotNull(o2);
        Assert.NotNull(config);
    }

    [Fact]
    public async Task Recent_tool_lists_newest_cards_first()
    {
        await using var client = await HttpClientAsync();
        await client.CallToolAsync("knowledge_save", SaveArgs("R-1", "Первая карточка", "текст"));
        await client.CallToolAsync("knowledge_save", SaveArgs("R-2", "Вторая карточка", "текст"));
        var r = Json(await client.CallToolAsync("knowledge_recent", new Dictionary<string, object?> { ["days"] = 1 }));
        var titles = r.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()).ToArray();
        Assert.Equal(new[] { "Вторая карточка", "Первая карточка" }, titles);
        var bad = await client.CallToolAsync("knowledge_recent", new Dictionary<string, object?> { ["days"] = 0 });
        Assert.True(bad.IsError);
    }

    [Fact]
    public async Task Adapter_starts_the_service_when_it_is_not_running()
    {
        // A config with AutoStart on, pointing at a data folder where no serve is running yet.
        var dir = TestPaths.NewDir();
        var port = TestPaths.FreePort();
        var config = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(config, $$"""
            { "Server": { "Host": "127.0.0.1", "Port": {{port}}, "AutoStart": true },
              "Storage": { "DataDirectory": "data", "MaintenanceDelaySeconds": 0 } }
            """);
        var startedAfter = DateTime.Now.AddSeconds(-1);
        try
        {
            await using (var client = await StdioClientAsync(config))
            {
                var saved = Json(await client.CallToolAsync("knowledge_save", SaveArgs("AS-1", "Автозапуск", "текст")));
                Assert.Equal("created", saved.GetProperty("operation").GetString());
            }
            // The service keeps running after the AI client and its adapter exit.
            using var http = new HttpClient();
            var res = await http.GetAsync($"http://127.0.0.1:{port}/");
            Assert.True(res.IsSuccessStatusCode);
        }
        finally
        {
            foreach (var p in Process.GetProcessesByName("KnowledgeMemory"))
            {
                try
                {
                    if (p.StartTime >= startedAfter && string.Equals(p.MainModule?.FileName, AdapterExe, StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        p.WaitForExit(5000);
                    }
                }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            }
            TestPaths.TryDelete(dir);
        }
    }

    [Fact]
    public async Task Adapter_with_missing_config_exits_with_clear_message()
    {
        var psi = new ProcessStartInfo(AdapterExe, ["mcp-stdio", "--config", Path.Combine(_s.Dir, "missing.json")])
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        var outText = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        Assert.NotEqual(0, p.ExitCode);
        Assert.Contains("Config file not found", err);
        Assert.Equal("", outText);
    }
}

using System.Net;
using System.Net.Sockets;
using KnowledgeMemory.Core;
using KnowledgeMemory.Host;
using KnowledgeMemory.Storage;
using Microsoft.Data.Sqlite;

namespace KnowledgeMemory.Tests;

/// <summary>Temporary data directory with its own database. Deleted on dispose.</summary>
public sealed class TempStore : IDisposable
{
    public string Dir { get; }
    public AppOptions Options { get; }
    public SqliteDatabase Db { get; }
    public KnowledgeStore Store { get; }
    public DiskManager Disk { get; }

    public TempStore(Action<AppOptions>? configure = null)
    {
        Dir = TestPaths.NewDir();
        Options = TestPaths.Options(Dir);
        configure?.Invoke(Options);
        Db = SqliteDatabase.Open(Options);
        Store = new KnowledgeStore(Db);
        Disk = new DiskManager(Db);
    }

    public void Dispose()
    {
        Db.Dispose();
        TestPaths.TryDelete(Dir);
    }
}

/// <summary>A real serve process inside the test (real Kestrel on a free loopback port).</summary>
public sealed class TempServer : IAsyncDisposable
{
    public string Dir { get; }
    public AppOptions Options { get; }
    public ServerApp Server { get; }
    public string ConfigPath { get; }
    public string Token => Server.Secrets.ApiToken;
    public string BaseUrl => Options.BaseUrl;

    private TempServer(string dir, AppOptions o, ServerApp server, string configPath)
    {
        Dir = dir; Options = o; Server = server; ConfigPath = configPath;
    }

    public static async Task<TempServer> StartAsync(Action<AppOptions>? configure = null, string? dir = null, bool autoStart = false)
    {
        dir ??= TestPaths.NewDir();
        var port = TestPaths.FreePort();
        var configPath = Path.Combine(dir, "appsettings.json");
        File.WriteAllText(configPath, $$"""
            { "Server": { "Host": "127.0.0.1", "Port": {{port}}, "AutoStart": {{(autoStart ? "true" : "false")}} },
              "Storage": { "DataDirectory": "data", "MaintenanceDelaySeconds": 0 } }
            """);
        var o = ConfigLoader.Load(configPath);
        configure?.Invoke(o);
        var server = ServerApp.Create(o, logging: l => { });
        await server.StartAsync();
        return new TempServer(dir, o, server, configPath);
    }

    /// <summary>Starts a server on an existing folder and its appsettings.json as it is (e.g. after a port change).</summary>
    public static async Task<TempServer> StartWithExistingConfigAsync(string dir)
    {
        var configPath = Path.Combine(dir, "appsettings.json");
        var o = ConfigLoader.Load(configPath);
        var server = ServerApp.Create(o, logging: l => { });
        await server.StartAsync();
        return new TempServer(dir, o, server, configPath);
    }

    public HttpClient MachineClient()
    {
        var c = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        c.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", Token);
        return c;
    }

    public HttpClient AnonymousClient(CookieContainer? cookies = null)
    {
        var handler = new HttpClientHandler { CookieContainer = cookies ?? new CookieContainer(), UseCookies = true };
        return new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
    }

    public async ValueTask DisposeAsync()
    {
        await Server.DisposeAsync();
        TestPaths.TryDelete(Dir);
    }

    public async Task StopKeepDataAsync() => await Server.DisposeAsync();
}

public static class TestPaths
{
    public static string Root { get; } = Path.Combine(Path.GetTempPath(), "km-tests");

    public static string NewDir()
    {
        var d = Path.Combine(Root, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(d);
        return d;
    }

    public static AppOptions Options(string dir)
    {
        var o = new AppOptions();
        o.Storage.DataDirectory = dir;
        o.Storage.MaintenanceDelaySeconds = 0;
        o.ResolvedDataDirectory = Path.Combine(dir, "data");
        return o;
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static void TryDelete(string dir)
    {
        SqliteConnection.ClearAllPools();
        for (var i = 0; i < 5; i++)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
                return;
            }
            catch (IOException) { Thread.Sleep(200); }
            catch (UnauthorizedAccessException) { Thread.Sleep(200); }
        }
    }
}

public static class Samples
{
    private static int _n;

    public static SaveRequest Card(string project = "Revqora", string? key = "245", string? title = null, string? content = null,
        string? summary = null, string kind = "task", string status = "active", string[]? tags = null) => new()
    {
        RequestId = "req-" + Guid.NewGuid().ToString("N"),
        Project = project,
        ExternalKey = key,
        Title = title ?? $"Импорт задач {Interlocked.Increment(ref _n)}",
        Summary = summary ?? "Импорт задач из CSV с проверкой дубликатов.",
        Content = content ?? "## Задача\nИмпорт CSV.\n\n## Решения\n- Использовать потоковый парсер.",
        Kind = kind,
        Status = status,
        Tags = tags ?? ["import", "csv"],
        Sources = [new KnowledgeSource { Label = "Чат: импорт" }],
    };
}

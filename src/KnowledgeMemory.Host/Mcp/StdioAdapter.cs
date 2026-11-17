using System.Diagnostics;
using KnowledgeMemory.Core;
using KnowledgeMemory.Host.Security;
using Microsoft.Extensions.Hosting;

namespace KnowledgeMemory.Host.Mcp;

/// <summary>
/// mcp-stdio: an MCP server over stdin/stdout that forwards to the running serve process.
/// stdout carries protocol messages only; all logs go to stderr without knowledge content or secrets.
/// If serve is not running and Server.AutoStart is on, the adapter starts it in the background on the first tool call.
/// </summary>
public static class StdioAdapter
{
    public static async Task<int> RunAsync(AppOptions o)
    {
        var builder = Microsoft.Extensions.Hosting.Host.CreateEmptyApplicationBuilder(settings: null);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);

        builder.Services.AddSingleton(o);
        // The token is read lazily per call, so the adapter can start before serve and recover after serve restarts.
        builder.Services.AddSingleton<IKnowledgeApi>(sp => new LazyRestClient(o));
        builder.Services
            .AddMcpServer(m =>
            {
                m.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "knowledge-memory", Version = "1.2.0" };
                m.ServerInstructions = KnowledgeTools.ServerInstructions;
            })
            .WithStdioServerTransport()
            .WithTools<KnowledgeTools>();

        await builder.Build().RunAsync();
        return 0;
    }

    private sealed class LazyRestClient(AppOptions initial) : IKnowledgeApi
    {
        private AppOptions o = initial;
        private static readonly SemaphoreSlim StartLock = new(1, 1);

        private async Task<T> Call<T>(Func<RestKnowledgeClient, Task<T>> action, CancellationToken ct)
        {
            try
            {
                using var c = Client();
                return await action(c);
            }
            catch (Exception ex) when (IsNotRunning(ex))
            {
                // The port may have been changed in the tray settings: re-read the config before giving up.
                if (ReloadConfig())
                {
                    try
                    {
                        using var c2 = Client();
                        return await action(c2);
                    }
                    catch (Exception ex2) when (IsNotRunning(ex2)) { }
                }
                if (!o.Server.AutoStart) throw;
                await ServiceStarter.EnsureRunningAsync(o, StartLock, ct);
                using var c = Client();
                return await action(c);
            }
        }

        /// <summary>True when the config file now has a different address.</summary>
        private bool ReloadConfig()
        {
            if (o.ConfigPath is null) return false;
            try
            {
                var fresh = ConfigLoader.Load(o.ConfigPath);
                var changed = fresh.BaseUrl != o.BaseUrl;
                o = fresh;
                return changed;
            }
            catch (KnowledgeException)
            {
                return false;
            }
        }

        private static bool IsNotRunning(Exception ex) =>
            ex is HttpRequestException || ex is KnowledgeException { Code: ErrorCodes.ServiceUnavailable };

        private RestKnowledgeClient Client()
        {
            string token;
            try { token = TokenStore.ReadToken(o); }
            catch (IOException) { throw new KnowledgeException(ErrorCodes.ServiceUnavailable, "Cannot read the API token file."); }
            return new RestKnowledgeClient(o, token);
        }

        public Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct) => Call(c => c.SearchAsync(q, ct), ct);
        public Task<DocumentPage> GetAsync(string id, int? offset, int? maxChars, CancellationToken ct) => Call(c => c.GetAsync(id, offset, maxChars, ct), ct);
        public Task<SaveResult> SaveAsync(SaveRequest r, CancellationToken ct) => Call(c => c.SaveAsync(r, ct), ct);
        public Task<IReadOnlyList<ProjectInfo>> ProjectsAsync(CancellationToken ct) => Call(c => c.ProjectsAsync(ct), ct);
    }
}

/// <summary>Starts "serve" as a separate hidden background process and waits until it answers.</summary>
public static class ServiceStarter
{
    public static TimeSpan StartTimeout { get; set; } = TimeSpan.FromSeconds(20);

    public static async Task EnsureRunningAsync(AppOptions o, SemaphoreSlim startLock, CancellationToken ct)
    {
        await startLock.WaitAsync(ct);
        try
        {
            if (await IsRunningAsync(o, ct)) return;
            var exe = Environment.ProcessPath
                ?? throw new KnowledgeException(ErrorCodes.ServiceUnavailable, "Cannot locate KnowledgeMemory.exe to start the service.");
            // The tray hosts the service and shows an icon (open UI, settings, exit).
            var args = o.ConfigPath is null ? "tray" : $"tray --config \"{o.ConfigPath}\"";
            // Framework-dependent run (dotnet KnowledgeMemory.dll): pass the entry assembly to dotnet.
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                args = $"\"{System.Reflection.Assembly.GetEntryAssembly()!.Location}\" " + args;
            // Shell execute + hidden window: the tray does not inherit the MCP stdin/stdout pipes
            // and keeps running after this adapter exits. A second serve exits by itself (single-instance lock).
            Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = AppContext.BaseDirectory });

            var deadline = DateTime.UtcNow + StartTimeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(300, ct);
                if (await IsRunningAsync(o, ct)) return;
            }
            throw new KnowledgeException(ErrorCodes.ServiceUnavailable,
                $"Started the Knowledge Memory service, but it did not answer within {StartTimeout.TotalSeconds:0} s. Run 'KnowledgeMemory.exe check' to see why.");
        }
        finally
        {
            startLock.Release();
        }
    }

    private static async Task<bool> IsRunningAsync(AppOptions o, CancellationToken ct)
    {
        if (!File.Exists(o.TokenPath)) return false;
        try
        {
            using var c = new RestKnowledgeClient(o, TokenStore.ReadToken(o), TimeSpan.FromSeconds(2));
            await c.IdentityAsync(ct);
            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or KnowledgeException or IOException)
        {
            return false;
        }
    }
}

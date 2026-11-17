using System.Net;
using System.Threading.RateLimiting;
using KnowledgeMemory.Core;
using KnowledgeMemory.Host.Api;
using KnowledgeMemory.Host.Mcp;
using KnowledgeMemory.Host.Security;
using KnowledgeMemory.Storage;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace KnowledgeMemory.Host;

/// <summary>Single-instance lock bound to the normalized data directory.</summary>
public sealed class InstanceLock : IDisposable
{
    private readonly FileStream _stream;
    private InstanceLock(FileStream s) => _stream = s;

    public static InstanceLock Acquire(AppOptions o)
    {
        Directory.CreateDirectory(o.ResolvedDataDirectory);
        try
        {
            var fs = new FileStream(o.LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.None);
            return new InstanceLock(fs);
        }
        catch (IOException)
        {
            throw new KnowledgeException(ErrorCodes.Busy,
                $"Knowledge Memory is already running for data directory {o.ResolvedDataDirectory}. Use the running instance (open-ui) instead of starting a second one.");
        }
    }

    public void Dispose() => _stream.Dispose();
}

/// <summary>The serve process: owns the database, serves the UI, REST API and MCP over HTTP.</summary>
public sealed class ServerApp : IAsyncDisposable
{
    public WebApplication App { get; }
    public AppOptions Options { get; }
    public SqliteDatabase Database { get; }
    public KnowledgeStore Store { get; }
    public DiskManager Disk { get; }
    public MaintenanceScheduler Scheduler { get; }
    public ServiceSecrets Secrets { get; }
    private readonly InstanceLock _lock;

    private ServerApp(WebApplication app, AppOptions o, SqliteDatabase db, KnowledgeStore store, DiskManager disk, MaintenanceScheduler scheduler, ServiceSecrets secrets, InstanceLock l)
    {
        App = app; Options = o; Database = db; Store = store; Disk = disk; Scheduler = scheduler; Secrets = secrets; _lock = l;
    }

    public static ServerApp Create(AppOptions o, Action<ILoggingBuilder>? logging = null)
    {
        var instanceLock = InstanceLock.Acquire(o);
        SqliteDatabase? db = null;
        try
        {
            TokenStore.CreatePrivateDirectory(o.ResolvedDataDirectory);
            var secrets = new ServiceSecrets(TokenStore.EnsureToken(o), Guid.NewGuid().ToString("N"));
            db = SqliteDatabase.Open(o); // migrations run before any request is accepted
            var store = new KnowledgeStore(db);
            var disk = new DiskManager(db);
            var scheduler = new MaintenanceScheduler(disk, TimeSpan.FromSeconds(o.Storage.MaintenanceDelaySeconds));
            if (o.Storage.AutoReclaimAfterDelete) store.DocumentDeleted += scheduler.RequestReclaim;

            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions
            {
                ContentRootPath = AppContext.BaseDirectory,
                WebRootPath = Path.Combine(AppContext.BaseDirectory, "wwwroot"),
                Args = [],
            });
            builder.Logging.ClearProviders();
            if (logging is not null) logging(builder.Logging);
            else builder.Logging.AddSimpleConsole(c => c.SingleLine = true);
            builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning);

            builder.WebHost.ConfigureKestrel(k =>
            {
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = 2 * 1024 * 1024;
                k.Limits.MaxRequestHeadersTotalSize = 32 * 1024;
                var ip = o.Server.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(o.Server.Host.Trim('[', ']'));
                if (!IPAddress.IsLoopback(ip)) throw KnowledgeException.Invalid("Only loopback addresses can be bound.");
                k.Listen(ip, o.Server.Port, l => l.Protocols = HttpProtocols.Http1);
            });

            builder.Services.AddSingleton(o);
            builder.Services.AddSingleton(secrets);
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(store);
            builder.Services.AddSingleton<IKnowledgeApi>(store);
            builder.Services.AddSingleton(disk);
            builder.Services.AddSingleton(scheduler);
            builder.Services.AddSingleton(new BrowserSessions());
            builder.Services.ConfigureHttpJsonOptions(j =>
            {
                j.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
            });
            builder.Services.AddRateLimiter(r =>
            {
                r.RejectionStatusCode = 429;
                r.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(_ =>
                    RateLimitPartition.GetConcurrencyLimiter("all", _ => new ConcurrencyLimiterOptions { PermitLimit = 16, QueueLimit = 64 }));
            });
            builder.Services
                .AddMcpServer(m =>
                {
                    m.ServerInfo = new ModelContextProtocol.Protocol.Implementation { Name = "knowledge-memory", Version = "1.0.0" };
                    m.ServerInstructions = KnowledgeTools.ServerInstructions;
                })
                .WithHttpTransport(t => t.Stateless = true)
                .WithTools<KnowledgeTools>();

            var app = builder.Build();
            app.UseMiddleware<SecurityMiddleware>();
            app.UseRateLimiter();
            app.UseDefaultFiles();
            app.UseStaticFiles(new StaticFileOptions { ServeUnknownFileTypes = false });
            ApiEndpoints.Map(app);
            app.MapMcp("/mcp");
            return new ServerApp(app, o, db, store, disk, scheduler, secrets, instanceLock);
        }
        catch
        {
            db?.Dispose();
            instanceLock.Dispose();
            throw;
        }
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        try
        {
            await App.StartAsync(ct);
        }
        catch (IOException ex) when (ex.InnerException is System.Net.Sockets.SocketException or null || ex.Message.Contains("address", StringComparison.OrdinalIgnoreCase))
        {
            throw new KnowledgeException(ErrorCodes.ServiceUnavailable, await DescribePortConflictAsync());
        }
        Scheduler.Start();
    }

    /// <summary>A busy port is not proof that our service runs there: ask it with the token.</summary>
    private async Task<string> DescribePortConflictAsync()
    {
        try
        {
            using var client = new RestKnowledgeClient(Options, Secrets.ApiToken, TimeSpan.FromSeconds(3));
            var id = await client.IdentityAsync(CancellationToken.None);
            if (id.App == "KnowledgeMemory")
                return $"Port {Options.Server.Port} is already used by another Knowledge Memory instance (a different data directory). Change Server.Port in the config.";
        }
        catch (Exception ex) when (ex is HttpRequestException or KnowledgeException or TaskCanceledException or System.Text.Json.JsonException) { }
        return $"Port {Options.Server.Port} on {Options.Server.Host} is used by another program. Change Server.Port in the config (1024-65535) and restart.";
    }

    public async ValueTask DisposeAsync()
    {
        try { await App.StopAsync(); } catch (Exception) { /* not started */ }
        await App.DisposeAsync();
        await Scheduler.DisposeAsync();
        Database.Dispose();
        _lock.Dispose();
    }
}

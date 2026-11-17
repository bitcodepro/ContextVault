using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using KnowledgeMemory.Core;
using KnowledgeMemory.Host.Tray;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace KnowledgeMemory.Tests;

public sealed class ConfigWriterTests : IDisposable
{
    private readonly string _dir = TestPaths.NewDir();
    public void Dispose() => TestPaths.TryDelete(_dir);

    [Fact]
    public void Changes_only_the_given_settings_and_keeps_the_rest()
    {
        var path = Path.Combine(_dir, "appsettings.json");
        File.WriteAllText(path, """
            // comment is allowed
            { "server": { "host": "127.0.0.1", "port": 47831 },
              "Storage": { "DataDirectory": "data", "BusyTimeoutSeconds": 9 }, }
            """);
        ConfigWriter.Set(path, ("Server", "Port", JsonValue.Create(48000)), ("Server", "StartWithWindows", JsonValue.Create(true)));

        var o = ConfigLoader.Load(path);
        Assert.Equal(48000, o.Server.Port);
        Assert.True(o.Server.StartWithWindows);
        Assert.Equal(9, o.Storage.BusyTimeoutSeconds);
        Assert.Equal(Path.Combine(_dir, "data"), o.ResolvedDataDirectory);
        Assert.DoesNotContain("\"Server\"", File.ReadAllText(path)); // existing section name casing kept
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Fact]
    public void Creates_the_file_and_section_when_missing()
    {
        var path = Path.Combine(_dir, "new.json");
        ConfigWriter.Set(path, ("Server", "AutoStart", JsonValue.Create(false)));
        Assert.False(ConfigLoader.Load(path).Server.AutoStart);
    }
}

public sealed class PortCheckTests
{
    [Fact]
    public void Detects_a_busy_port_and_finds_a_free_one()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        try
        {
            var busy = ((IPEndPoint)l.LocalEndpoint).Port;
            Assert.False(PortCheck.IsFree("127.0.0.1", busy));
            var free = PortCheck.FindFree("127.0.0.1", busy);
            Assert.NotEqual(busy, free);
            Assert.True(PortCheck.IsFree("127.0.0.1", free));
        }
        finally
        {
            l.Stop();
        }
    }
}

public sealed class WindowsStartupTests
{
    [Fact]
    public void Apply_writes_and_removes_the_run_entry_for_the_current_user()
    {
        var startup = new WindowsStartup("KnowledgeMemory-Test-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var o = new AppOptions { ConfigPath = @"C:\Tools\KnowledgeMemory\appsettings.json" };
            o.Server.StartWithWindows = true;
            startup.Apply(o, @"C:\Tools\KnowledgeMemory\KnowledgeMemory.exe");
            Assert.Equal("\"C:\\Tools\\KnowledgeMemory\\KnowledgeMemory.exe\" tray --config \"C:\\Tools\\KnowledgeMemory\\appsettings.json\"", startup.Current());

            o.Server.StartWithWindows = false;
            startup.Apply(o, @"C:\Tools\KnowledgeMemory\KnowledgeMemory.exe");
            Assert.Null(startup.Current());
        }
        finally
        {
            startup.Disable();
        }
    }
}

public sealed class AdapterPortChangeTests : IAsyncLifetime
{
    private TempServer _s = null!;
    public async Task InitializeAsync() => _s = await TempServer.StartAsync();
    public async Task DisposeAsync() => await _s.DisposeAsync();

    [Fact]
    public async Task Adapter_follows_a_port_change_without_restarting_the_ai_client()
    {
        await using var client = await McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "port-change",
            Command = McpTests.AdapterExe,
            Arguments = ["mcp-stdio", "--config", _s.ConfigPath],
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        }));
        Assert.NotEqual(true, (await client.CallToolAsync("knowledge_projects", new Dictionary<string, object?>())).IsError);

        // The user changes the port in the tray settings: config rewritten, service restarted on the new port.
        var dir = _s.Dir;
        await _s.StopKeepDataAsync();
        var newPort = TestPaths.FreePort();
        ConfigWriter.Set(Path.Combine(dir, "appsettings.json"), ("Server", "Port", JsonValue.Create(newPort)));
        _s = await TempServer.StartWithExistingConfigAsync(dir);
        Assert.Equal(newPort, _s.Options.Server.Port);

        var r = await client.CallToolAsync("knowledge_projects", new Dictionary<string, object?>());
        Assert.True(r.IsError != true, ((TextContentBlock)r.Content[0]).Text);
    }
}

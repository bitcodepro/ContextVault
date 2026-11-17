using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using KnowledgeMemory.Core;
using KnowledgeMemory.Host;
using KnowledgeMemory.Host.Mcp;
using KnowledgeMemory.Host.Security;

return await Cli.RunAsync(args);

namespace KnowledgeMemory.Host
{
    public static class Cli
    {
        private const string Usage = """
            Knowledge Memory - local AI memory over MCP

            Usage:
              KnowledgeMemory.exe tray       [--config <appsettings.json>] Run the service with a tray icon (open UI, settings, exit)
              KnowledgeMemory.exe serve      --config <appsettings.json>   Run the service in this console (database owner, web UI, MCP over HTTP)
              KnowledgeMemory.exe mcp-stdio  --config <appsettings.json>   MCP stdio bridge for AI clients (needs a running serve)
              KnowledgeMemory.exe open-ui    --config <appsettings.json> [--print-url]
                                                                           Open the web UI with a one-time login link
              KnowledgeMemory.exe check      --config <appsettings.json>   Validate config and check that serve is reachable
              KnowledgeMemory.exe autostart  on|off [--config <appsettings.json>]
                                                                           Start the tray app when you sign in to Windows (or stop that)
              KnowledgeMemory.exe --version
            """;

        public static async Task<int> RunAsync(string[] args)
        {
            if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
            {
                Console.Error.WriteLine(Usage);
                return args.Length == 0 ? 2 : 0;
            }
            if (args[0] == "--version")
            {
                Console.WriteLine(typeof(Cli).Assembly.GetName().Version);
                return 0;
            }

            var command = args[0];
            string? configPath = null;
            var printUrl = false;
            string? autostartValue = null;
            var first = 1;
            if (command == "autostart")
            {
                if (args.Length < 2 || args[1] is not ("on" or "off"))
                {
                    Console.Error.WriteLine($"autostart needs 'on' or 'off'.\n\n{Usage}");
                    return 2;
                }
                autostartValue = args[1];
                first = 2;
            }
            for (var i = first; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
                else if (args[i] == "--print-url") printUrl = true;
                else
                {
                    Console.Error.WriteLine($"Unknown argument: {args[i]}\n\n{Usage}");
                    return 2;
                }
            }

            // Without --config every command uses appsettings.json next to the program (created from the example
            // if missing), so AI client settings can be as short as: command = "...KnowledgeMemory.exe", args = ["mcp-stdio"].
            if (configPath is null)
                configPath = DefaultConfigPath();

            AppOptions options;
            try
            {
                options = ConfigLoader.Load(configPath);
            }
            catch (KnowledgeException ex)
            {
                Console.Error.WriteLine("Configuration error: " + ex.Message);
                return 3;
            }

            try
            {
                return command switch
                {
                    "serve" => await ServeAsync(options),
                    "tray" => Tray.TrayApp.Run(options),
                    "autostart" => SetAutostart(options, autostartValue == "on"),
                    "mcp-stdio" => await StdioAdapter.RunAsync(options),
                    "open-ui" => await OpenUiAsync(options, printUrl),
                    "check" => await CheckAsync(options),
                    _ => Unknown(command),
                };
            }
            catch (KnowledgeException ex)
            {
                Console.Error.WriteLine("Error: " + ex.Message);
                return 4;
            }
        }

        private static string DefaultConfigPath()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
            if (!File.Exists(path))
            {
                var example = Path.Combine(AppContext.BaseDirectory, "appsettings.example.json");
                if (File.Exists(example)) File.Copy(example, path);
                else File.WriteAllText(path, "{}");
            }
            return path;
        }

        private static int SetAutostart(AppOptions o, bool on)
        {
            ConfigWriter.Set(o.ConfigPath!, ("Server", "StartWithWindows", System.Text.Json.Nodes.JsonValue.Create(on)));
            o.Server.StartWithWindows = on;
            var startup = new Tray.WindowsStartup();
            startup.Apply(o, Environment.ProcessPath!);
            Console.Error.WriteLine(on
                ? $"Knowledge Memory will start in the tray when you sign in to Windows:\n  {startup.Current()}"
                : "Knowledge Memory will no longer start with Windows.");
            return 0;
        }

        private static int Unknown(string command)
        {
            Console.Error.WriteLine($"Unknown command '{command}'.\n\n{Usage}");
            return 2;
        }

        private static async Task<int> ServeAsync(AppOptions o)
        {
            await using var server = ServerApp.Create(o);
            await server.StartAsync();
            Console.Error.WriteLine($"Knowledge Memory is running at {o.BaseUrl}");
            Console.Error.WriteLine($"Data directory: {o.ResolvedDataDirectory}");
            Console.Error.WriteLine("Open the UI with: KnowledgeMemory.exe open-ui --config <same config>. Press Ctrl+C to stop.");
            if (o.Server.OpenBrowserOnStart)
            {
                try { await OpenUiAsync(o, printUrl: false); } catch (Exception ex) { Console.Error.WriteLine("Could not open the browser: " + ex.Message); }
            }
            await server.App.WaitForShutdownAsync();
            return 0;
        }

        private static HttpClient MachineClient(AppOptions o)
        {
            var http = new HttpClient { BaseAddress = new Uri(o.BaseUrl), Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TokenStore.ReadToken(o));
            return http;
        }

        public static async Task<string> CreateLoginUrlAsync(AppOptions o)
        {
            using var http = MachineClient(o);
            HttpResponseMessage res;
            try
            {
                res = await http.PostAsync("api/auth/bootstrap", JsonContent.Create(new { }));
            }
            catch (HttpRequestException)
            {
                throw new KnowledgeException(ErrorCodes.ServiceUnavailable,
                    $"Knowledge Memory is not running at {o.BaseUrl}. Start it first: KnowledgeMemory.exe serve --config \"{o.ConfigPath ?? "<path>"}\"");
            }
            if (!res.IsSuccessStatusCode)
                throw new KnowledgeException(ErrorCodes.ServiceUnavailable, $"The service at {o.BaseUrl} refused the login request (HTTP {(int)res.StatusCode}). Is it the same data directory?");
            var body = await res.Content.ReadFromJsonAsync<BootstrapResponse>();
            // The one-time code goes in the URL fragment: it is not sent to the server in the page request and not logged.
            return $"{o.BaseUrl}/#bootstrap={body!.Code}";
        }

        private sealed record BootstrapResponse(string Code, int ExpiresInSeconds);

        private static async Task<int> OpenUiAsync(AppOptions o, bool printUrl)
        {
            var url = await CreateLoginUrlAsync(o);
            if (printUrl)
            {
                Console.WriteLine(url);
                Console.Error.WriteLine("This link works once and expires in 2 minutes.");
                return 0;
            }
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Console.Error.WriteLine($"Opened {o.BaseUrl} in the default browser.");
            return 0;
        }

        private static async Task<int> CheckAsync(AppOptions o)
        {
            Console.Error.WriteLine($"Config OK. Address: {o.BaseUrl}. Data directory: {o.ResolvedDataDirectory}");
            try
            {
                using var client = new RestKnowledgeClient(o, TokenStore.ReadToken(o), TimeSpan.FromSeconds(5));
                var id = await client.IdentityAsync(CancellationToken.None);
                Console.Error.WriteLine($"Service is running (instance {id.InstanceId[..8]}, version {id.Version}).");
                return 0;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or KnowledgeException)
            {
                Console.Error.WriteLine("Service is not reachable: " + ex.Message);
                return 5;
            }
        }
    }
}

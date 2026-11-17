using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace KnowledgeMemory.Core;

public sealed class ServerOptions
{
    public string Host { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 47831;
    public bool OpenBrowserOnStart { get; set; }
    /// <summary>mcp-stdio starts serve in the background when it is not running.</summary>
    public bool AutoStart { get; set; } = true;
    /// <summary>Start the tray app (and the service) when the user signs in to Windows.</summary>
    public bool StartWithWindows { get; set; }
}

public sealed class StorageOptions
{
    public const string PortableDefault = "data"; // next to appsettings.json, i.e. next to the program
    private string _dataDirectory = PortableDefault;

    public string DataDirectory
    {
        get => _dataDirectory;
        set { _dataDirectory = value; DataDirectorySet = true; }
    }

    /// <summary>True when the config file names a data folder; false when it relies on the default.</summary>
    [JsonIgnore] public bool DataDirectorySet { get; private set; }
    public int BusyTimeoutSeconds { get; set; } = 5;
    public bool AutoReclaimAfterDelete { get; set; } = true;
    public int MaintenanceDelaySeconds { get; set; } = 5;
    public int IncrementalVacuumPagesPerBatch { get; set; } = 256;
    public int MaintenanceBudgetSeconds { get; set; } = 2;
    /// <summary>Technical audit rows (time, operation, id; never content) are kept this many days.</summary>
    public int AuditRetentionDays { get; set; } = 30;
    /// <summary>Stored idempotency answers (ids and versions only) are kept this many days.</summary>
    public int RequestIdRetentionDays { get; set; } = 7;
}

public sealed class SearchOptions
{
    public int DefaultLimit { get; set; } = 8;
    public int MaxLimit { get; set; } = 20;
    public int SnippetCharacters { get; set; } = 320;
    /// <summary>Upper bound on summary+snippet characters in one search response.</summary>
    public int MaxResponseCharacters { get; set; } = 12000;
    public int MaxQueryCharacters { get; set; } = 500;
    public int MaxQueryTerms { get; set; } = 12;
}

public sealed class KnowledgeOptions
{
    public int MaxContentCharacters { get; set; } = 100_000;
    public int MaxTags { get; set; } = 20;
    public int MaxSources { get; set; } = 20;
    public int MaxGetCharacters { get; set; } = 30_000;
    /// <summary>Previous versions kept per card (0 disables history). Older ones are removed on save.</summary>
    public int MaxHistoryVersions { get; set; } = 20;
}

public sealed class AppOptions
{
    public ServerOptions Server { get; set; } = new();
    public StorageOptions Storage { get; set; } = new();
    public SearchOptions Search { get; set; } = new();
    public KnowledgeOptions Knowledge { get; set; } = new();

    /// <summary>Absolute, normalized data directory (set by <see cref="ConfigLoader"/>).</summary>
    [JsonIgnore] public string ResolvedDataDirectory { get; set; } = "";
    [JsonIgnore] public string? ConfigPath { get; set; }

    public string DatabasePath => Path.Combine(ResolvedDataDirectory, "knowledge.db");
    public string SecretsDirectory => Path.Combine(ResolvedDataDirectory, "secrets");
    public string TokenPath => Path.Combine(SecretsDirectory, "api-token");
    public string BackupDirectory => Path.Combine(ResolvedDataDirectory, "backups");
    public string ExportDirectory => Path.Combine(ResolvedDataDirectory, "exports");
    public string LockPath => Path.Combine(ResolvedDataDirectory, "instance.lock");
    public string BaseUrl => $"http://{(Server.Host.Contains(':') ? "[" + Server.Host + "]" : Server.Host)}:{Server.Port}";
}

public static class ConfigLoader
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Loads, expands and validates config. Throws <see cref="KnowledgeException"/> with a clear message.</summary>
    public static AppOptions Load(string? configPath)
    {
        AppOptions options;
        string baseDir;
        if (string.IsNullOrWhiteSpace(configPath))
        {
            options = new AppOptions();
            baseDir = AppContext.BaseDirectory;
        }
        else
        {
            var full = Path.GetFullPath(configPath);
            if (!File.Exists(full))
                throw KnowledgeException.Invalid($"Config file not found: {full}");
            try
            {
                options = JsonSerializer.Deserialize<AppOptions>(File.ReadAllText(full), Json) ?? new AppOptions();
            }
            catch (JsonException ex)
            {
                throw KnowledgeException.Invalid($"Config file is not valid JSON ({full}): {ex.Message}");
            }
            options.ConfigPath = full;
            baseDir = Path.GetDirectoryName(full)!;
        }

        Validate(options);
        options.ResolvedDataDirectory = ResolveDataDirectory(options.Storage.DataDirectory, baseDir);
        if (!options.Storage.DataDirectorySet)
            options.ResolvedDataDirectory = KeepLegacyData(options.ResolvedDataDirectory);
        return options;
    }

    /// <summary>Default data folder of versions up to 1.2.0 (before the data moved next to the program).</summary>
    public static string LegacyDataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KnowledgeMemory");

    /// <summary>
    /// A config written for 1.2.0 or older that does not name a data folder used %LOCALAPPDATA%\KnowledgeMemory.
    /// Keep using that memory after an update instead of opening a new empty database next to the program.
    /// </summary>
    private static string KeepLegacyData(string portable)
    {
        const string db = "knowledge.db";
        if (File.Exists(Path.Combine(portable, db))) return portable;
        var legacy = Path.GetFullPath(LegacyDataDirectory);
        return File.Exists(Path.Combine(legacy, db)) ? legacy : portable;
    }

    public static void Validate(AppOptions o)
    {
        if (!IsLoopback(o.Server.Host))
            throw KnowledgeException.Invalid(
                $"Server.Host '{o.Server.Host}' is not allowed. Only loopback (127.0.0.1, localhost or ::1) is supported in this version.");
        if (o.Server.Port is < 1024 or > 65535)
            throw KnowledgeException.Invalid($"Server.Port must be between 1024 and 65535 (got {o.Server.Port}).");
        if (string.IsNullOrWhiteSpace(o.Storage.DataDirectory))
            throw KnowledgeException.Invalid("Storage.DataDirectory is required.");
        if (o.Storage.DataDirectory.StartsWith(@"\\", StringComparison.Ordinal))
            throw KnowledgeException.Invalid("Storage.DataDirectory on a network (UNC) share is not supported: SQLite WAL needs a local disk.");
        Range(o.Storage.BusyTimeoutSeconds, 1, 120, "Storage.BusyTimeoutSeconds");
        Range(o.Storage.MaintenanceDelaySeconds, 0, 3600, "Storage.MaintenanceDelaySeconds");
        Range(o.Storage.IncrementalVacuumPagesPerBatch, 1, 100_000, "Storage.IncrementalVacuumPagesPerBatch");
        Range(o.Storage.MaintenanceBudgetSeconds, 1, 600, "Storage.MaintenanceBudgetSeconds");
        Range(o.Search.DefaultLimit, 1, 100, "Search.DefaultLimit");
        Range(o.Search.MaxLimit, o.Search.DefaultLimit, 100, "Search.MaxLimit");
        Range(o.Search.SnippetCharacters, 40, 4000, "Search.SnippetCharacters");
        Range(o.Knowledge.MaxContentCharacters, 1000, 2_000_000, "Knowledge.MaxContentCharacters");
        Range(o.Knowledge.MaxTags, 0, 200, "Knowledge.MaxTags");
        Range(o.Knowledge.MaxSources, 0, 200, "Knowledge.MaxSources");
        Range(o.Knowledge.MaxGetCharacters, 1000, 2_000_000, "Knowledge.MaxGetCharacters");
    }

    private static void Range(int value, int min, int max, string name)
    {
        if (value < min || value > max)
            throw KnowledgeException.Invalid($"{name} must be between {min} and {max} (got {value}).");
    }

    public static bool IsLoopback(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return true;
        return IPAddress.TryParse(host.Trim('[', ']'), out var ip)
               && (ip.Equals(IPAddress.Loopback) || ip.Equals(IPAddress.IPv6Loopback));
    }

    /// <summary>Expands %VAR% explicitly and resolves relative paths against the config file folder.</summary>
    public static string ResolveDataDirectory(string raw, string baseDir)
    {
        var expanded = System.Text.RegularExpressions.Regex.Replace(raw, "%([A-Za-z0-9_]+)%", m =>
        {
            var v = Environment.GetEnvironmentVariable(m.Groups[1].Value);
            if (string.IsNullOrEmpty(v))
                throw KnowledgeException.Invalid($"Environment variable {m.Value} used in Storage.DataDirectory is not set.");
            return v;
        });
        var full = Path.IsPathRooted(expanded) ? Path.GetFullPath(expanded) : Path.GetFullPath(Path.Combine(baseDir, expanded));
        if (full.StartsWith(@"\\", StringComparison.Ordinal))
            throw KnowledgeException.Invalid("Storage.DataDirectory on a network (UNC) share is not supported.");
        return full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}

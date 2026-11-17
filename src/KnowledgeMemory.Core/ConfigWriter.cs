using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace KnowledgeMemory.Core;

/// <summary>Changes single settings in appsettings.json and keeps every other setting as it is.</summary>
public static class ConfigWriter
{
    private static readonly JsonSerializerOptions Write = new() { WriteIndented = true };

    /// <summary>Sets Section.Key in the config file (creating the file or section if needed). Writes atomically.</summary>
    public static void Set(string configPath, params (string Section, string Key, JsonNode? Value)[] changes)
    {
        JsonObject root;
        if (File.Exists(configPath))
        {
            var text = File.ReadAllText(configPath);
            var parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            root = parsed as JsonObject ?? throw KnowledgeException.Invalid($"Config file is not a JSON object: {configPath}");
        }
        else
        {
            root = new JsonObject();
        }

        foreach (var (section, key, value) in changes)
        {
            // Match existing names case-insensitively, like the loader does.
            var sectionName = root.Select(p => p.Key).FirstOrDefault(k => k.Equals(section, StringComparison.OrdinalIgnoreCase)) ?? section;
            if (root[sectionName] is not JsonObject obj)
            {
                obj = new JsonObject();
                root[sectionName] = obj;
            }
            var keyName = obj.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key;
            obj[keyName] = value;
        }

        var tmp = configPath + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Write));
        File.Move(tmp, configPath, overwrite: true);
    }
}

public static class PortCheck
{
    /// <summary>True when nothing listens on the loopback port (we can bind it).</summary>
    public static bool IsFree(string host, int port)
    {
        var ip = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ? IPAddress.Loopback : IPAddress.Parse(host.Trim('[', ']'));
        try
        {
            var l = new TcpListener(ip, port) { ExclusiveAddressUse = true };
            l.Start();
            l.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>First free port starting from <paramref name="start"/> (for the "find a free port" button).</summary>
    public static int FindFree(string host, int start)
    {
        for (var p = Math.Max(1024, start); p <= 65535; p++)
            if (IsFree(host, p)) return p;
        throw KnowledgeException.Invalid("No free port found.");
    }
}

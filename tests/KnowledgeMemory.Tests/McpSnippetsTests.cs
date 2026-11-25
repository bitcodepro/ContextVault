using System.Text.Json;
using KnowledgeMemory.Host.Tray;

namespace KnowledgeMemory.Tests;

public sealed class McpSnippetsTests
{
    private const string Exe = @"C:\Program Files\Knowledge Memory\KnowledgeMemory.exe";
    private const string Config = @"C:\Program Files\Knowledge Memory\appsettings.json";
    private static McpSnippets.Snippet Get(string menu) => McpSnippets.All(Exe, Config).Single(s => s.Menu == menu);

    [Fact]
    public void Desktop_json_is_valid_and_has_the_real_paths()
    {
        using var doc = JsonDocument.Parse(Get("Claude Desktop / Cursor").Text);
        var server = doc.RootElement.GetProperty("mcpServers").GetProperty("knowledge-memory");
        Assert.Equal(Exe, server.GetProperty("command").GetString());
        Assert.Equal(new[] { "mcp-stdio" }, server.GetProperty("args").EnumerateArray().Select(a => a.GetString()));
    }

    [Fact]
    public void Codex_block_uses_literal_paths_and_auto_approval()
    {
        var t = Get("Codex").Text;
        Assert.Contains("[mcp_servers.knowledge-memory]", t);
        Assert.Contains(@"command = ""C:\\Program Files\\Knowledge Memory\\KnowledgeMemory.exe""", t);
        Assert.Contains("args = [\"mcp-stdio\"]", t);
        Assert.Contains("default_tools_approval_mode = \"approve\"", t);
    }

    [Fact]
    public void Claude_code_command_and_permissions()
    {
        Assert.Equal($"claude mcp add -s user knowledge-memory -- \"{Exe}\" mcp-stdio", Get("Claude Code — command").Text);
        using var doc = JsonDocument.Parse(Get("Claude Code — permissions").Text);
        Assert.Equal("mcp__knowledge-memory", doc.RootElement.GetProperty("permissions").GetProperty("allow")[0].GetString());
    }

    [Fact]
    public void Agent_instructions_match_the_doc_block()
    {
        var text = Get("Instructions for the AI").Text;
        Assert.Contains("knowledge_search", text);
        var doc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "agent-instructions.md"));
        Assert.Contains("rewrite the card as one consistent current state", doc);
    }

    [Fact]
    public void Instructions_are_one_paste_with_the_setup_request_the_rules_and_the_real_exe_path()
    {
        var text = Get("Instructions for the AI").Text;
        Assert.StartsWith("This is an MCP server for global memory.", text);
        Assert.Contains("whenever we switch between tasks", text);
        Assert.Contains("## Knowledge Memory", text);
        Assert.Contains("### After saving", text);
        Assert.EndsWith(Get("Codex").Text, text);
        Assert.Contains(@"command = ""C:\\Program Files\\Knowledge Memory\\KnowledgeMemory.exe""", text);
        Assert.DoesNotContain("{{", text);
    }

    [Fact]
    public void A_config_outside_the_program_folder_is_passed_explicitly()
    {
        var other = McpSnippets.All(Exe, @"D:\km\appsettings.json").Single(s => s.Menu == "Codex").Text;
        Assert.Contains(@"args = [""mcp-stdio"", ""--config"", ""D:\\km\\appsettings.json""]", other);
    }

    [Fact]
    public void No_snippet_contains_a_token()
    {
        foreach (var s in McpSnippets.All(Exe, Config))
        {
            Assert.DoesNotContain("Bearer", s.Text);
            Assert.DoesNotContain("api-token", s.Text);
        }
    }
}

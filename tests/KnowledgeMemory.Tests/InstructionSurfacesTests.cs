using System.ComponentModel;
using System.Reflection;
using KnowledgeMemory.Host.Mcp;
using KnowledgeMemory.Host.Tray;

namespace KnowledgeMemory.Tests;

/// <summary>
/// The AI gets the save rules from several places: MCP server instructions, the knowledge_save description,
/// the tray fallback text and docs/agent-instructions.md. The limits on its own saves must be in all of them.
/// </summary>
public sealed class InstructionSurfacesTests
{
    private static string SaveDescription() =>
        typeof(KnowledgeTools).GetMethod("Save")!.GetCustomAttribute<DescriptionAttribute>()!.Description;

    public static TheoryData<string> Surfaces() => new() { "server", "save", "fallback" };

    private static string Text(string surface) => surface switch
    {
        "server" => KnowledgeTools.ServerInstructions,
        "save" => SaveDescription(),
        _ => McpSnippets.FallbackInstructions,
    };

    [Theory]
    [MemberData(nameof(Surfaces))]
    public void Every_surface_carries_the_auto_save_limits_and_the_compaction_rule(string surface)
    {
        var text = Text(surface);
        Assert.Contains(KnowledgeTools.AutoSaveLimits, text);
        if (surface != "save") Assert.Contains(KnowledgeTools.CompactionRule, text);
        Assert.DoesNotContain("trust the card", text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Limits_name_every_boundary()
    {
        var l = KnowledgeTools.AutoSaveLimits;
        foreach (var part in new[] { "other projects or tasks", "superseded", "someone else's conclusion", "stop when the user says not to save", "secrets" })
            Assert.Contains(part, l);
    }

    [Fact]
    public void The_doc_block_has_the_same_boundaries_and_no_blind_trust_in_the_card()
    {
        var doc = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "docs", "agent-instructions.md"));
        foreach (var part in new[] { "Never change cards of other projects or tasks", "never mark a card `superseded`", "someone else's conclusion",
                     "If the user says not to save", "newer confirmed user decision or verified result", "ask the user instead of picking" })
            Assert.Contains(part, doc);
        Assert.DoesNotContain("trust the card", doc, StringComparison.OrdinalIgnoreCase);
    }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using KnowledgeMemory.Core;

namespace KnowledgeMemory.Host.Api;

/// <summary>Exports current cards and their sources. Never includes tokens, sessions or settings.</summary>
public static class ExportWriter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static string JsonExport(IEnumerable<KnowledgeDocument> docs) =>
        JsonSerializer.Serialize(new
        {
            format = "knowledge-memory-export",
            formatVersion = 1,
            exportedAtUtc = DateTime.UtcNow,
            documents = docs.ToList(),
        }, Json);

    public static string Markdown(IEnumerable<KnowledgeDocument> docs)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Knowledge Memory export");
        sb.AppendLine();
        sb.AppendLine("Exported (UTC): " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture));
        foreach (var d in docs)
        {
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.Append("# ").Append(d.ProjectDisplay);
            if (d.ExternalKey is not null) sb.Append(" / #").Append(d.ExternalKey);
            sb.Append(" — ").AppendLine(d.Title);
            sb.AppendLine();
            sb.AppendLine($"- id: `{d.Id}`");
            sb.AppendLine($"- kind: {d.Kind}; status: {d.Status}; version: {d.Version}");
            if (d.SupersededBy is not null) sb.AppendLine($"- superseded by: `{d.SupersededBy}`");
            sb.AppendLine($"- created: {d.CreatedAtUtc:yyyy-MM-dd HH:mm} UTC; updated: {d.UpdatedAtUtc:yyyy-MM-dd HH:mm} UTC (time of saving, not of the decision)");
            if (d.Tags.Count > 0) sb.AppendLine("- tags: " + string.Join(", ", d.Tags));
            sb.AppendLine();
            sb.AppendLine("**Summary:** " + d.Summary);
            sb.AppendLine();
            sb.AppendLine(d.Content);
            sb.AppendLine();
            sb.AppendLine("Sources:");
            if (d.Sources.Count == 0) sb.AppendLine("- unknown");
            foreach (var s in d.Sources)
                sb.AppendLine("- " + string.Join(" — ", new[] { s.Label, s.Url, s.Note }.Where(x => !string.IsNullOrEmpty(x))));
        }
        return sb.ToString();
    }
}

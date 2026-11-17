using System.Text;
using System.Text.RegularExpressions;

namespace KnowledgeMemory.Core;

/// <summary>
/// Turns free user text into a safe FTS5 MATCH expression. Raw FTS syntax (quotes, OR, NEAR, column
/// filters, parentheses, '*', '^') is never passed through: every term is re-quoted as a string token.
/// </summary>
public static partial class QueryParser
{
    [GeneratedRegex(@"[\p{L}\p{N}_]+")] private static partial Regex WordRegex();
    // Key-like tokens: 245, #245, ET-6449, PR-71, v2.1
    [GeneratedRegex(@"#?[\p{L}\p{N}][\p{L}\p{N}_\-.]*")] private static partial Regex KeyCandidateRegex();

    public sealed record Parsed(IReadOnlyList<string> Terms, IReadOnlyList<string> KeyCandidates)
    {
        public bool IsEmpty => Terms.Count == 0;
    }

    public static Parsed Parse(string? text, int maxChars, int maxTerms)
    {
        var q = (text ?? "").Trim();
        if (q.Length > maxChars) q = q[..maxChars];

        var terms = new List<string>();
        foreach (Match m in WordRegex().Matches(q))
        {
            var t = m.Value.ToLowerInvariant();
            if (!terms.Contains(t)) terms.Add(t);
            if (terms.Count >= maxTerms) break;
        }

        var keys = new List<string>();
        foreach (Match m in KeyCandidateRegex().Matches(q))
        {
            var k = m.Value.TrimStart('#').TrimEnd('.', '-');
            // A key candidate is short and contains a digit (245, ET-6449) - plain words are not treated as keys.
            if (k.Length is 0 or > KnowledgeValidator.MaxKeyLength || !k.Any(char.IsDigit)) continue;
            var norm = k.ToUpperInvariant();
            if (!keys.Contains(norm)) keys.Add(norm);
            if (keys.Count >= maxTerms) break;
        }
        return new Parsed(terms, keys);
    }

    /// <summary>All terms must match (AND). The last term gets a prefix match if it is at least 3 chars long.</summary>
    public static string ToMatchAll(Parsed p) => Build(p, " AND ");

    /// <summary>Any term may match (OR), used as a weaker fallback and reported as partialText.</summary>
    public static string ToMatchAny(Parsed p) => Build(p, " OR ");

    private static string Build(Parsed p, string op)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < p.Terms.Count; i++)
        {
            if (i > 0) sb.Append(op);
            var term = p.Terms[i].Replace("\"", "\"\"");
            var prefix = i == p.Terms.Count - 1 && term.Length >= 3 ? "*" : "";
            // Each word matches as typed, or by its stem in the "stems" column (other word forms).
            var stem = Stemmer.Stem(p.Terms[i]).Replace("\"", "\"\"");
            if (stem.Length >= 3 && stem != term)
                sb.Append("(\"").Append(term).Append('"').Append(prefix).Append(" OR stems : \"").Append(stem).Append('"').Append(prefix).Append(')');
            else
                sb.Append('"').Append(term).Append('"').Append(prefix);
        }
        return sb.ToString();
    }
}

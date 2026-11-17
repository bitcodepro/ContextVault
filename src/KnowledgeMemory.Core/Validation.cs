using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace KnowledgeMemory.Core;

/// <summary>A save request after server-side validation and normalization.</summary>
public sealed record NormalizedSave
{
    public string? Id { get; init; }
    public long? ExpectedVersion { get; init; }
    public required string RequestId { get; init; }
    public required string Project { get; init; }
    public required string ProjectDisplay { get; init; }
    public string? ExternalKey { get; init; }
    public string? ExternalKeyNorm { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public required string Content { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required string Status { get; init; }
    public string? SupersededBy { get; init; }
    public required IReadOnlyList<KnowledgeSource> Sources { get; init; }

    /// <summary>SHA-256 of the normalized payload without requestId. Used for idempotency checks; never stores content.</summary>
    public required string PayloadHash { get; init; }
}

public static partial class KnowledgeValidator
{
    public const int MaxProjectLength = 80;
    public const int MaxKeyLength = 80;
    public const int MaxTitleLength = 300;
    public const int MaxSummaryLength = 2000;
    public const int MaxTagLength = 64;
    public const int MaxSourceFieldLength = 1000;

    [GeneratedRegex(@"^[A-Za-z0-9_\-:.]{8,100}$")] private static partial Regex RequestIdRegex();
    [GeneratedRegex(@"\s+")] private static partial Regex Whitespace();
    [GeneratedRegex(@"^[\p{L}\p{N}_\-.]+$")] private static partial Regex ProjectKeyRegex();

    public static NormalizedSave Normalize(SaveRequest r, KnowledgeOptions limits)
    {
        var requestId = (r.RequestId ?? "").Trim();
        if (!RequestIdRegex().IsMatch(requestId))
            throw KnowledgeException.Invalid("requestId is required: 8-100 characters of letters, digits, '-', '_', ':' or '.'. Use a new value for every distinct save.");

        string? id = null;
        if (!string.IsNullOrWhiteSpace(r.Id))
        {
            id = NormalizeId(r.Id, "id");
            if (r.ExpectedVersion is null or < 1)
                throw KnowledgeException.Invalid("expectedVersion is required when updating an existing card (id is set). Read the card first with knowledge_get.");
        }
        else if (r.ExpectedVersion is not null)
        {
            throw KnowledgeException.Invalid("expectedVersion is only valid together with id.");
        }

        var (project, projectDisplay) = NormalizeProject(r.Project);
        var (key, keyNorm) = NormalizeExternalKey(r.ExternalKey);

        var title = CleanText(r.Title, "title", MaxTitleLength, required: true, singleLine: true);
        var summary = CleanText(r.Summary, "summary", MaxSummaryLength, required: true, singleLine: false);
        var content = CleanText(r.Content, "content", limits.MaxContentCharacters, required: true, singleLine: false);

        var kind = (r.Kind ?? KnowledgeKinds.Task).Trim().ToLowerInvariant();
        if (!KnowledgeKinds.All.Contains(kind))
            throw KnowledgeException.Invalid($"kind must be one of: {string.Join(", ", KnowledgeKinds.All)}.");
        var status = (r.Status ?? KnowledgeStatuses.Active).Trim().ToLowerInvariant();
        if (!KnowledgeStatuses.All.Contains(status))
            throw KnowledgeException.Invalid($"status must be one of: {string.Join(", ", KnowledgeStatuses.All)}.");

        string? supersededBy = string.IsNullOrWhiteSpace(r.SupersededBy) ? null : NormalizeId(r.SupersededBy, "supersededBy");
        if (supersededBy is not null && supersededBy == id)
            throw KnowledgeException.Invalid("supersededBy cannot point to the card itself.");

        var tags = NormalizeTags(r.Tags, limits.MaxTags);
        var sources = NormalizeSources(r.Sources, limits.MaxSources);

        var normalized = new NormalizedSave
        {
            Id = id,
            ExpectedVersion = id is null ? null : r.ExpectedVersion,
            RequestId = requestId,
            Project = project,
            ProjectDisplay = projectDisplay,
            ExternalKey = key,
            ExternalKeyNorm = keyNorm,
            Title = title,
            Kind = kind,
            Summary = summary,
            Content = content,
            Tags = tags,
            Status = status,
            SupersededBy = supersededBy,
            Sources = sources,
            PayloadHash = "",
        };
        return normalized with { PayloadHash = HashPayload(normalized) };
    }

    public static string NormalizeId(string raw, string field)
    {
        if (!Guid.TryParse(raw.Trim(), out var g))
            throw KnowledgeException.Invalid($"{field} must be a card id (UUID) returned by the server.");
        return g.ToString("D");
    }

    /// <summary>Project key: trimmed, lower-case (invariant), whitespace replaced by '-'. Display value keeps the original casing.</summary>
    public static (string Key, string Display) NormalizeProject(string? raw)
    {
        var display = Whitespace().Replace((raw ?? "").Trim(), " ");
        if (display.Length == 0)
            throw KnowledgeException.Invalid("project is required (for example \"revqora\").");
        if (display.Length > MaxProjectLength)
            throw KnowledgeException.Invalid($"project is longer than {MaxProjectLength} characters.");
        var key = display.ToLowerInvariant().Replace(' ', '-');
        if (!ProjectKeyRegex().IsMatch(key))
            throw KnowledgeException.Invalid("project may contain letters, digits, spaces, '-', '_' and '.' only.");
        return (key, display);
    }

    public static string? TryNormalizeProjectKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Whitespace().Replace(raw.Trim(), " ").ToLowerInvariant().Replace(' ', '-');
    }

    /// <summary>
    /// External key is kept as a string (leading zeros and prefixes preserved). A leading '#' is dropped.
    /// Comparison key is upper-case invariant, so "et-6449" equals "ET-6449".
    /// </summary>
    public static (string? Key, string? Norm) NormalizeExternalKey(string? raw)
    {
        var key = (raw ?? "").Trim().TrimStart('#').Trim();
        if (key.Length == 0) return (null, null);
        if (key.Length > MaxKeyLength)
            throw KnowledgeException.Invalid($"externalKey is longer than {MaxKeyLength} characters.");
        if (key.Any(char.IsControl) || key.Any(char.IsWhiteSpace))
            throw KnowledgeException.Invalid("externalKey cannot contain spaces or control characters (examples: 245, ET-6449, PR-71).");
        return (key, key.ToUpperInvariant());
    }

    private static string CleanText(string? raw, string field, int max, bool required, bool singleLine)
    {
        var text = (raw ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
        if (text.Contains('\0'))
            throw KnowledgeException.Invalid($"{field} contains a NUL character.");
        text = singleLine ? Whitespace().Replace(text, " ").Trim() : text.Trim();
        if (required && text.Length == 0)
            throw KnowledgeException.Invalid($"{field} is required.");
        if (text.Length > max)
            throw KnowledgeException.Invalid($"{field} is longer than {max} characters ({text.Length}).");
        return text;
    }

    private static IReadOnlyList<string> NormalizeTags(IReadOnlyList<string>? raw, int max)
    {
        var result = new List<string>();
        foreach (var t in raw ?? [])
        {
            var tag = Whitespace().Replace((t ?? "").Trim().TrimStart('#'), "-").ToLowerInvariant();
            if (tag.Length == 0) continue;
            if (tag.Length > MaxTagLength)
                throw KnowledgeException.Invalid($"tag '{tag[..20]}…' is longer than {MaxTagLength} characters.");
            if (tag.Any(char.IsControl))
                throw KnowledgeException.Invalid("tags cannot contain control characters.");
            if (!result.Contains(tag, StringComparer.Ordinal)) result.Add(tag);
        }
        if (result.Count > max)
            throw KnowledgeException.Invalid($"At most {max} tags are allowed (got {result.Count}).");
        return result;
    }

    private static IReadOnlyList<KnowledgeSource> NormalizeSources(IReadOnlyList<KnowledgeSource>? raw, int max)
    {
        var result = new List<KnowledgeSource>();
        foreach (var s in raw ?? [])
        {
            if (s is null) continue;
            var label = Short(s.Label, "sources.label");
            var note = Short(s.Note, "sources.note");
            var url = Short(s.Url, "sources.url");
            if (url is not null && !IsSafeUrl(url))
                throw KnowledgeException.Invalid("sources.url must be an absolute http:// or https:// link. Put chat ids or other identifiers into sources.label or sources.note.");
            if (label is null && note is null && url is null) continue;
            result.Add(new KnowledgeSource { Label = label, Url = url, Note = note });
        }
        if (result.Count > max)
            throw KnowledgeException.Invalid($"At most {max} sources are allowed (got {result.Count}).");
        return result;
    }

    private static string? Short(string? raw, string field)
    {
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.Length > MaxSourceFieldLength)
            throw KnowledgeException.Invalid($"{field} is longer than {MaxSourceFieldLength} characters.");
        if (v.Any(c => char.IsControl(c) && c != '\n'))
            throw KnowledgeException.Invalid($"{field} contains control characters.");
        return v;
    }

    public static bool IsSafeUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps);

    private static string HashPayload(NormalizedSave n)
    {
        var canonical = JsonSerializer.Serialize(new object?[]
        {
            n.Id, n.ExpectedVersion, n.Project, n.ProjectDisplay, n.ExternalKey, n.Title, n.Kind, n.Summary, n.Content,
            n.Tags, n.Status, n.SupersededBy, n.Sources.Select(s => new[] { s.Label, s.Url, s.Note }),
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}

/// <summary>Best-effort warnings for common secret formats. Not a guarantee.</summary>
public static partial class SecretScanner
{
    private static readonly (string Name, Regex Pattern)[] Patterns =
    [
        ("private key block", new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----", RegexOptions.Compiled)),
        ("GitHub token", new Regex(@"\b(gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b", RegexOptions.Compiled)),
        ("AWS access key", new Regex(@"\bAKIA[0-9A-Z]{16}\b", RegexOptions.Compiled)),
        ("Slack token", new Regex(@"\bxox[abprs]-[A-Za-z0-9-]{10,}\b", RegexOptions.Compiled)),
        ("API key (sk-…)", new Regex(@"\bsk-[A-Za-z0-9_\-]{20,}\b", RegexOptions.Compiled)),
        ("Google API key", new Regex(@"\bAIza[0-9A-Za-z_\-]{35}\b", RegexOptions.Compiled)),
        ("JWT", new Regex(@"\beyJ[A-Za-z0-9_\-]{10,}\.eyJ[A-Za-z0-9_\-]{10,}\.[A-Za-z0-9_\-]{10,}\b", RegexOptions.Compiled)),
        ("password assignment", new Regex(@"(?i)\b(password|passwd|pwd|пароль)\s*[:=]\s*\S{6,}", RegexOptions.Compiled)),
    ];

    public static IReadOnlyList<string> Scan(NormalizedSave n)
    {
        var text = string.Join("\n", n.Title, n.Summary, n.Content, string.Join(" ", n.Sources.Select(s => $"{s.Label} {s.Url} {s.Note}")));
        var found = new List<string>();
        foreach (var (name, pattern) in Patterns)
            if (pattern.IsMatch(text)) found.Add(name);
        return found.Count == 0
            ? []
            : [$"Possible secret detected ({string.Join(", ", found)}). Knowledge Memory must not store passwords, tokens or keys: edit the card to remove it."];
    }
}

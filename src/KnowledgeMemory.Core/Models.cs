using System.Text.Json.Serialization;

namespace KnowledgeMemory.Core;

public static class KnowledgeKinds
{
    public const string Task = "task";
    public const string Decision = "decision";
    public const string Reference = "reference";
    public const string Preference = "preference";
    public static readonly string[] All = [Task, Decision, Reference, Preference];
}

public static class KnowledgeStatuses
{
    public const string Active = "active";
    public const string Completed = "completed";
    public const string Superseded = "superseded";
    public static readonly string[] All = [Active, Completed, Superseded];
}

/// <summary>Origin of a knowledge card. Every field is optional; unknown origin is allowed.</summary>
public sealed record KnowledgeSource
{
    public string? Label { get; init; }
    public string? Url { get; init; }
    public string? Note { get; init; }
}

public sealed record SaveRequest
{
    public string? Id { get; init; }
    public long? ExpectedVersion { get; init; }
    public string? RequestId { get; init; }
    public string? Project { get; init; }
    public string? ExternalKey { get; init; }
    public string? Title { get; init; }
    public string? Kind { get; init; }
    public string? Summary { get; init; }
    public string? Content { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public string? Status { get; init; }
    public string? SupersededBy { get; init; }
    public IReadOnlyList<KnowledgeSource>? Sources { get; init; }
}

public sealed record SaveResult
{
    public required string Id { get; init; }
    public required long Version { get; init; }
    /// <summary>created | updated | unchanged</summary>
    public required string Operation { get; init; }
    public required DateTime SavedAtUtc { get; init; }
    /// <summary>True when this is a stored answer for a repeated requestId.</summary>
    public bool Replayed { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record KnowledgeDocument
{
    public required string Id { get; init; }
    public required string Project { get; init; }
    public required string ProjectDisplay { get; init; }
    public string? ExternalKey { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public required string Content { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required string Status { get; init; }
    public string? SupersededBy { get; init; }
    public required IReadOnlyList<KnowledgeSource> Sources { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
    public required long Version { get; init; }
}

/// <summary>Full card with a bounded content window (no silent truncation).</summary>
public sealed record DocumentPage
{
    public required KnowledgeDocument Document { get; init; }
    public required int ContentOffset { get; init; }
    public required int ContentTotalLength { get; init; }
    public required bool Truncated { get; init; }
    public int? NextOffset { get; init; }
}

public sealed record SearchQuery
{
    public string? Query { get; init; }
    public string? Project { get; init; }
    public string? ExternalKey { get; init; }
    public string? Kind { get; init; }
    public string? Status { get; init; }
    /// <summary>Only cards updated within this many days.</summary>
    public int? SinceDays { get; init; }
    public int? Limit { get; init; }
    public string? Cursor { get; init; }
}

public sealed record SearchHit
{
    public required string Id { get; init; }
    public required string Project { get; init; }
    public required string ProjectDisplay { get; init; }
    public string? ExternalKey { get; init; }
    public required string Title { get; init; }
    public required string Kind { get; init; }
    public required string Summary { get; init; }
    public required string Snippet { get; init; }
    public required IReadOnlyList<string> Tags { get; init; }
    public required string Status { get; init; }
    public string? SupersededBy { get; init; }
    public required DateTime UpdatedAtUtc { get; init; }
    public required long Version { get; init; }
    /// <summary>exactKey | fullText | partialText | filter</summary>
    public required string MatchType { get; init; }
}

public sealed record SearchResult
{
    public required IReadOnlyList<SearchHit> Items { get; init; }
    public required bool HasMore { get; init; }
    public string? NextCursor { get; init; }
    /// <summary>Human-readable note, e.g. that an empty result does not prove absence.</summary>
    public string? Note { get; init; }
}

public sealed record ProjectInfo(string Project, string Display, int Documents);

/// <summary>A previous version of a card (the current version is the card itself).</summary>
public sealed record VersionInfo
{
    public required long Version { get; init; }
    /// <summary>When this version was saved.</summary>
    public required DateTime SavedAtUtc { get; init; }
    /// <summary>When the next version replaced it.</summary>
    public required DateTime ReplacedAtUtc { get; init; }
    public required string Title { get; init; }
    public required string Status { get; init; }
    public required int ContentLength { get; init; }
}

public sealed record DeleteResult
{
    public required string Id { get; init; }
    public required DateTime DeletedAtUtc { get; init; }
}

public sealed record MaintenanceStatus
{
    /// <summary>idle | pending | running | busy | error</summary>
    public required string State { get; init; }
    public DateTime? LastRunAtUtc { get; init; }
    public string? LastResult { get; init; }
    /// <summary>Machine-readable result: compact_ok | compact_failed | reclaim_done | reclaim_partial | checkpoint_busy | maintenance_error</summary>
    public string? LastResultCode { get; init; }
    public string? LastError { get; init; }
    public long? LastBytesBefore { get; init; }
    public long? LastBytesAfter { get; init; }
    public DateTime? NextAttemptAtUtc { get; init; }
}

public sealed record DiskStats
{
    public required int Documents { get; init; }
    public required long MainBytes { get; init; }
    public required long WalBytes { get; init; }
    public required long ShmBytes { get; init; }
    public required long BackupBytes { get; init; }
    public required int BackupCount { get; init; }
    public required long ExportBytes { get; init; }
    public required long TotalBytes { get; init; }
    public required long ReclaimableEstimateBytes { get; init; }
    public required long PageSize { get; init; }
    public required long FreelistPages { get; init; }
    public required string AutoVacuum { get; init; }
    public required MaintenanceStatus Maintenance { get; init; }
}

public sealed record CompactResult
{
    /// <summary>ok | busy | error</summary>
    public required string Status { get; init; }
    public required long BytesBefore { get; init; }
    public required long BytesAfter { get; init; }
    public required long DurationMs { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
    public string? Error { get; init; }
}

public sealed record BackupResult
{
    public required string FileName { get; init; }
    public required long Bytes { get; init; }
    public required bool IntegrityOk { get; init; }
    public required DateTime CreatedAtUtc { get; init; }
}

public sealed record IdentityInfo
{
    [JsonPropertyName("app")] public string App { get; init; } = "KnowledgeMemory";
    public required string InstanceId { get; init; }
    public required string DataDirectoryHash { get; init; }
    public required string Version { get; init; }
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using KnowledgeMemory.Core;
using Microsoft.Data.Sqlite;

namespace KnowledgeMemory.Storage;

/// <summary>Knowledge use cases on top of SQLite: save (idempotent, optimistic), get, search, delete, list.</summary>
public sealed class KnowledgeStore : IKnowledgeApi
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private const string DocColumns =
        "d.id, d.project, d.project_display, d.external_key, d.title, d.kind, d.summary, d.content, d.tags_json, d.status, d.superseded_by, d.sources_json, d.created_at, d.updated_at, d.version";

    private readonly SqliteDatabase _db;
    private readonly AppOptions _o;
    private readonly TimeProvider _time;

    /// <summary>Raised after a successful delete commit (used to schedule disk maintenance).</summary>
    public event Action? DocumentDeleted;

    public KnowledgeStore(SqliteDatabase db, TimeProvider? time = null)
    {
        _db = db;
        _o = db.Options;
        _time = time ?? TimeProvider.System;
    }

    private DateTime Now()
    {
        // Millisecond precision keeps round-trips through JSON/strings exact.
        var t = _time.GetUtcNow().UtcDateTime;
        return new DateTime(t.Ticks - t.Ticks % TimeSpan.TicksPerMillisecond, DateTimeKind.Utc);
    }

    private static string Iso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
    private static DateTime ParseIso(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    // ---------------------------------------------------------------- save

    public async Task<SaveResult> SaveAsync(SaveRequest request, CancellationToken ct)
    {
        var n = KnowledgeValidator.Normalize(request, _o.Knowledge);
        var warnings = SecretScanner.Scan(n);

        using var gate = await _db.EnterWriteAsync(_db.WriteTimeout, ct);
        ct.ThrowIfCancellationRequested();
        using var c = _db.OpenConnection();
        using var tx = c.BeginTransaction(); // BEGIN IMMEDIATE

        var replay = LoadReplay(c, tx, n);
        if (replay is not null) return replay;

        if (n.SupersededBy is not null && !Exists(c, tx, n.SupersededBy))
            throw KnowledgeException.Invalid("supersededBy points to a card that does not exist.");

        var now = Now();
        SaveResult result;
        if (n.Id is null)
        {
            EnsureKeyFree(c, tx, n, exceptId: null);
            var id = Guid.NewGuid().ToString("D");
            using var cmd = Cmd(c, tx, """
                INSERT INTO documents(id, project, project_display, external_key, external_key_norm, title, kind, summary, content,
                    tags_json, tags_text, status, superseded_by, sources_json, created_at, updated_at, version, stems)
                VALUES ($id, $project, $display, $key, $keyNorm, $title, $kind, $summary, $content,
                    $tags, $tagsText, $status, $sup, $sources, $now, $now, 1, $stems);
                """);
            BindDoc(cmd, n);
            cmd.Parameters.AddWithValue("$id", id);
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.ExecuteNonQuery();
            result = new SaveResult { Id = id, Version = 1, Operation = "created", SavedAtUtc = now, Warnings = warnings };
        }
        else
        {
            var current = LoadDocument(c, tx, n.Id)
                ?? throw new KnowledgeException(ErrorCodes.NotFound, "Card not found. It may have been deleted in the web UI.",
                    new Dictionary<string, object?> { ["id"] = n.Id });
            if (current.Version != n.ExpectedVersion)
                throw new KnowledgeException(ErrorCodes.VersionConflict,
                    $"Card was changed by another client (expected version {n.ExpectedVersion}, current {current.Version}). Re-read it with knowledge_get and merge the changes; do not overwrite.",
                    new Dictionary<string, object?> { ["id"] = n.Id, ["currentVersion"] = current.Version, ["updatedAtUtc"] = current.UpdatedAtUtc });
            if (n.SupersededBy == n.Id)
                throw KnowledgeException.Invalid("supersededBy cannot point to the card itself.");

            if (SameContent(current, n))
            {
                result = new SaveResult { Id = n.Id, Version = current.Version, Operation = "unchanged", SavedAtUtc = now, Warnings = warnings };
            }
            else
            {
                EnsureKeyFree(c, tx, n, exceptId: n.Id);
                ArchiveVersion(c, tx, current, now);
                using var cmd = Cmd(c, tx, """
                    UPDATE documents SET project=$project, project_display=$display, external_key=$key, external_key_norm=$keyNorm,
                        title=$title, kind=$kind, summary=$summary, content=$content, tags_json=$tags, tags_text=$tagsText,
                        status=$status, superseded_by=$sup, sources_json=$sources, stems=$stems, updated_at=$now, version=version+1
                    WHERE id=$id AND version=$expected;
                    """);
                BindDoc(cmd, n);
                cmd.Parameters.AddWithValue("$id", n.Id);
                cmd.Parameters.AddWithValue("$now", Iso(now));
                cmd.Parameters.AddWithValue("$expected", n.ExpectedVersion!.Value);
                if (cmd.ExecuteNonQuery() != 1)
                    throw new KnowledgeException(ErrorCodes.VersionConflict, "Card was changed concurrently. Re-read and merge.");
                result = new SaveResult { Id = n.Id, Version = current.Version + 1, Operation = "updated", SavedAtUtc = now, Warnings = warnings };
            }
        }

        using (var log = Cmd(c, tx, """
            INSERT INTO request_log(request_id, payload_hash, document_id, version, operation, saved_at, warnings_json)
            VALUES ($rid, $hash, $doc, $ver, $op, $at, $warn);
            """))
        {
            log.Parameters.AddWithValue("$rid", n.RequestId);
            log.Parameters.AddWithValue("$hash", n.PayloadHash);
            log.Parameters.AddWithValue("$doc", result.Id);
            log.Parameters.AddWithValue("$ver", result.Version);
            log.Parameters.AddWithValue("$op", result.Operation);
            log.Parameters.AddWithValue("$at", Iso(now));
            log.Parameters.AddWithValue("$warn", JsonSerializer.Serialize(warnings));
            log.ExecuteNonQuery();
        }
        Audit(c, tx, result.Operation == "created" ? "create" : result.Operation == "updated" ? "update" : "save-unchanged", result.Id, now);
        PurgeLogs(c, tx, now);
        tx.Commit();
        return result;
    }

    private SaveResult? LoadReplay(SqliteConnection c, SqliteTransaction tx, NormalizedSave n)
    {
        using var cmd = Cmd(c, tx, "SELECT payload_hash, document_id, version, operation, saved_at, warnings_json FROM request_log WHERE request_id=$rid;");
        cmd.Parameters.AddWithValue("$rid", n.RequestId);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        if (r.GetString(0) != n.PayloadHash)
            throw new KnowledgeException(ErrorCodes.IdempotencyMismatch,
                "This requestId was already used for a different save. Use a new requestId for changed content; reuse a requestId only to retry exactly the same command.",
                new Dictionary<string, object?> { ["requestId"] = n.RequestId });
        return new SaveResult
        {
            Id = r.GetString(1),
            Version = r.GetInt64(2),
            Operation = r.GetString(3),
            SavedAtUtc = ParseIso(r.GetString(4)),
            Replayed = true,
            Warnings = JsonSerializer.Deserialize<string[]>(r.GetString(5)) ?? [],
        };
    }

    private static void EnsureKeyFree(SqliteConnection c, SqliteTransaction tx, NormalizedSave n, string? exceptId)
    {
        if (n.ExternalKeyNorm is null) return;
        using var cmd = Cmd(c, tx, "SELECT id, version, title FROM documents WHERE project=$p AND external_key_norm=$k AND ($except IS NULL OR id<>$except);");
        cmd.Parameters.AddWithValue("$p", n.Project);
        cmd.Parameters.AddWithValue("$k", n.ExternalKeyNorm);
        cmd.Parameters.AddWithValue("$except", (object?)exceptId ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        if (r.Read())
            throw new KnowledgeException(ErrorCodes.DuplicateKey,
                $"A card for project '{n.Project}' and key '{n.ExternalKey}' already exists. Read it with knowledge_get and update it with expectedVersion instead of creating a new one.",
                new Dictionary<string, object?> { ["existingId"] = r.GetString(0), ["existingVersion"] = r.GetInt64(1), ["existingTitle"] = r.GetString(2) });
    }

    private static bool SameContent(KnowledgeDocument d, NormalizedSave n) =>
        d.Project == n.Project && d.ProjectDisplay == n.ProjectDisplay && d.ExternalKey == n.ExternalKey && d.Title == n.Title
        && d.Kind == n.Kind && d.Summary == n.Summary && d.Content == n.Content && d.Tags.SequenceEqual(n.Tags)
        && d.Status == n.Status && d.SupersededBy == n.SupersededBy && d.Sources.SequenceEqual(n.Sources);

    private static void BindDoc(SqliteCommand cmd, NormalizedSave n)
    {
        cmd.Parameters.AddWithValue("$project", n.Project);
        cmd.Parameters.AddWithValue("$display", n.ProjectDisplay);
        cmd.Parameters.AddWithValue("$key", (object?)n.ExternalKey ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$keyNorm", (object?)n.ExternalKeyNorm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$title", n.Title);
        cmd.Parameters.AddWithValue("$kind", n.Kind);
        cmd.Parameters.AddWithValue("$summary", n.Summary);
        cmd.Parameters.AddWithValue("$content", n.Content);
        cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(n.Tags, Json));
        cmd.Parameters.AddWithValue("$tagsText", string.Join(' ', n.Tags));
        cmd.Parameters.AddWithValue("$status", n.Status);
        cmd.Parameters.AddWithValue("$sup", (object?)n.SupersededBy ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(n.Sources, Json));
        cmd.Parameters.AddWithValue("$stems", Stemmer.StemText(n.Title, n.Summary, string.Join(' ', n.Tags), n.Content));
    }

    private void Audit(SqliteConnection c, SqliteTransaction tx, string op, string id, DateTime now)
    {
        using var cmd = Cmd(c, tx, "INSERT INTO audit_log(at, operation, document_id) VALUES ($at, $op, $id);");
        cmd.Parameters.AddWithValue("$at", Iso(now));
        cmd.Parameters.AddWithValue("$op", op);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    private void PurgeLogs(SqliteConnection c, SqliteTransaction tx, DateTime now)
    {
        using var a = Cmd(c, tx, "DELETE FROM audit_log WHERE at < $cut;");
        a.Parameters.AddWithValue("$cut", Iso(now.AddDays(-_o.Storage.AuditRetentionDays)));
        a.ExecuteNonQuery();
        using var r = Cmd(c, tx, "DELETE FROM request_log WHERE saved_at < $cut;");
        r.Parameters.AddWithValue("$cut", Iso(now.AddDays(-_o.Storage.RequestIdRetentionDays)));
        r.ExecuteNonQuery();
    }

    // ---------------------------------------------------------------- get

    public Task<DocumentPage> GetAsync(string id, int? offset, int? maxChars, CancellationToken ct)
    {
        var norm = KnowledgeValidator.NormalizeId(id, "id");
        using var c = _db.OpenConnection();
        var doc = LoadDocument(c, null, norm)
            ?? throw new KnowledgeException(ErrorCodes.NotFound, "Card not found. It may have been deleted.", new Dictionary<string, object?> { ["id"] = norm });

        var total = doc.Content.Length;
        var start = Math.Clamp(offset ?? 0, 0, total);
        var window = Math.Clamp(maxChars ?? _o.Knowledge.MaxGetCharacters, 1, _o.Knowledge.MaxGetCharacters);
        var end = Math.Min(total, start + window);
        // Do not split a UTF-16 surrogate pair.
        if (end < total && end > start && char.IsHighSurrogate(doc.Content[end - 1])) end--;
        var truncated = start > 0 || end < total;
        return Task.FromResult(new DocumentPage
        {
            Document = truncated ? doc with { Content = doc.Content[start..end] } : doc,
            ContentOffset = start,
            ContentTotalLength = total,
            Truncated = truncated,
            NextOffset = end < total ? end : null,
        });
    }

    public KnowledgeDocument? Find(string id)
    {
        using var c = _db.OpenConnection();
        return LoadDocument(c, null, KnowledgeValidator.NormalizeId(id, "id"));
    }

    private static KnowledgeDocument? LoadDocument(SqliteConnection c, SqliteTransaction? tx, string id)
    {
        using var cmd = Cmd(c, tx, $"SELECT {DocColumns} FROM documents d WHERE d.id=$id;");
        cmd.Parameters.AddWithValue("$id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadDoc(r) : null;
    }

    private static bool Exists(SqliteConnection c, SqliteTransaction tx, string id)
    {
        using var cmd = Cmd(c, tx, "SELECT 1 FROM documents WHERE id=$id;");
        cmd.Parameters.AddWithValue("$id", id);
        return cmd.ExecuteScalar() is not null;
    }

    private static KnowledgeDocument ReadDoc(SqliteDataReader r) => new()
    {
        Id = r.GetString(0),
        Project = r.GetString(1),
        ProjectDisplay = r.GetString(2),
        ExternalKey = r.IsDBNull(3) ? null : r.GetString(3),
        Title = r.GetString(4),
        Kind = r.GetString(5),
        Summary = r.GetString(6),
        Content = r.GetString(7),
        Tags = JsonSerializer.Deserialize<string[]>(r.GetString(8), Json) ?? [],
        Status = r.GetString(9),
        SupersededBy = r.IsDBNull(10) ? null : r.GetString(10),
        Sources = JsonSerializer.Deserialize<KnowledgeSource[]>(r.GetString(11), Json) ?? [],
        CreatedAtUtc = ParseIso(r.GetString(12)),
        UpdatedAtUtc = ParseIso(r.GetString(13)),
        Version = r.GetInt64(14),
    };

    // ---------------------------------------------------------------- search

    private sealed record Filters(string? Project, string? KeyNorm, string? Kind, string? Status, string? Since = null)
    {
        public bool Any => Project is not null || KeyNorm is not null || Kind is not null || Status is not null || Since is not null;
    }

    public Task<SearchResult> SearchAsync(SearchQuery q, CancellationToken ct)
    {
        var filters = new Filters(
            KnowledgeValidator.TryNormalizeProjectKey(q.Project),
            KnowledgeValidator.NormalizeExternalKey(q.ExternalKey).Norm,
            NormalizeEnum(q.Kind, KnowledgeKinds.All, "kind"),
            NormalizeEnum(q.Status, KnowledgeStatuses.All, "status"),
            q.SinceDays is int days
                ? days is >= 1 and <= 3650 ? Iso(Now().AddDays(-days)) : throw KnowledgeException.Invalid("sinceDays must be between 1 and 3650.")
                : null);
        var limit = Math.Clamp(q.Limit ?? _o.Search.DefaultLimit, 1, _o.Search.MaxLimit);
        var offset = DecodeCursor(q.Cursor);
        var parsed = QueryParser.Parse(q.Query, _o.Search.MaxQueryCharacters, _o.Search.MaxQueryTerms);
        var want = offset + limit + 1;

        using var c = _db.OpenConnection();
        var hits = new List<SearchHit>();
        if (parsed.IsEmpty)
        {
            hits.AddRange(FilterList(c, filters, want));
        }
        else
        {
            var seen = new HashSet<string>();
            void AddRange(IEnumerable<SearchHit> items)
            {
                foreach (var h in items)
                    if (seen.Add(h.Id)) hits.Add(h);
            }
            AddRange(ExactKey(c, filters, parsed.KeyCandidates));
            AddRange(FullText(c, filters, QueryParser.ToMatchAll(parsed), want, "fullText"));
            if (hits.Count == 0 && parsed.Terms.Count > 1)
                AddRange(FullText(c, filters, QueryParser.ToMatchAny(parsed), want, "partialText"));
        }

        // Page and bound the total response size, not only the item count.
        var page = new List<SearchHit>();
        var budget = _o.Search.MaxResponseCharacters;
        var index = offset;
        for (; index < hits.Count && page.Count < limit; index++)
        {
            var cost = hits[index].Summary.Length + hits[index].Snippet.Length + hits[index].Title.Length;
            if (page.Count > 0 && cost > budget) break;
            budget -= cost;
            page.Add(hits[index]);
        }
        var hasMore = index < hits.Count;
        return Task.FromResult(new SearchResult
        {
            Items = page,
            HasMore = hasMore,
            NextCursor = hasMore ? EncodeCursor(index) : null,
            Note = page.Count == 0 && offset == 0
                ? "No matching cards. This only means nothing matching was saved (or it was worded differently); it does not prove the topic was never discussed."
                : page.Any(h => h.Status == KnowledgeStatuses.Superseded)
                    ? "Some results are superseded: they are not the current decision. Check supersededBy."
                    : null,
        });
    }

    private static string? NormalizeEnum(string? raw, string[] allowed, string name)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var v = raw.Trim().ToLowerInvariant();
        if (!allowed.Contains(v)) throw KnowledgeException.Invalid($"{name} must be one of: {string.Join(", ", allowed)}.");
        return v;
    }

    private static int DecodeCursor(string? cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor)) return 0;
        try
        {
            var s = Encoding.UTF8.GetString(Convert.FromBase64String(cursor));
            if (s.StartsWith("o:", StringComparison.Ordinal) && int.TryParse(s[2..], NumberStyles.None, CultureInfo.InvariantCulture, out var o) && o is >= 0 and <= 10_000)
                return o;
        }
        catch (FormatException) { }
        throw KnowledgeException.Invalid("cursor is invalid. Pass nextCursor from the previous response unchanged.");
    }

    private static string EncodeCursor(int offset) => Convert.ToBase64String(Encoding.UTF8.GetBytes("o:" + offset.ToString(CultureInfo.InvariantCulture)));

    private static string FilterSql(Filters f) =>
        " AND ($fp IS NULL OR d.project=$fp) AND ($fk IS NULL OR d.external_key_norm=$fk) AND ($fkind IS NULL OR d.kind=$fkind) AND ($fs IS NULL OR d.status=$fs) AND ($fsince IS NULL OR d.updated_at >= $fsince)";

    private static void BindFilters(SqliteCommand cmd, Filters f)
    {
        cmd.Parameters.AddWithValue("$fp", (object?)f.Project ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fk", (object?)f.KeyNorm ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fkind", (object?)f.Kind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fs", (object?)f.Status ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$fsince", (object?)f.Since ?? DBNull.Value);
    }

    private IEnumerable<SearchHit> FilterList(SqliteConnection c, Filters f, int want)
    {
        using var cmd = Cmd(c, null, $"SELECT {DocColumns} FROM documents d WHERE 1=1{FilterSql(f)} ORDER BY d.updated_at DESC, d.id LIMIT $lim;");
        BindFilters(cmd, f);
        cmd.Parameters.AddWithValue("$lim", want);
        return ReadHits(cmd, "filter", snippetOrdinal: null);
    }

    private IEnumerable<SearchHit> ExactKey(SqliteConnection c, Filters f, IReadOnlyList<string> keys)
    {
        if (keys.Count == 0) return [];
        var names = keys.Select((_, i) => "$k" + i).ToArray();
        using var cmd = Cmd(c, null,
            $"SELECT {DocColumns} FROM documents d WHERE d.external_key_norm IN ({string.Join(',', names)}){FilterSql(f)} ORDER BY d.project, d.id LIMIT 50;");
        for (var i = 0; i < keys.Count; i++) cmd.Parameters.AddWithValue(names[i], keys[i]);
        BindFilters(cmd, f);
        return ReadHits(cmd, "exactKey", snippetOrdinal: null);
    }

    private IEnumerable<SearchHit> FullText(SqliteConnection c, Filters f, string match, int want, string matchType)
    {
        if (match.Length == 0) return [];
        // Title and summary weigh more than tags and body. Ties are broken by id for stable order.
        using var cmd = Cmd(c, null, $"""
            SELECT {DocColumns}, snippet(documents_fts, 3, '', '', '…', 40)
            FROM documents_fts JOIN documents d ON d.doc_rowid = documents_fts.rowid
            WHERE documents_fts MATCH $match{FilterSql(f)}
            ORDER BY bm25(documents_fts, 10.0, 5.0, 3.0, 1.0, 1.0), d.id
            LIMIT $lim;
            """);
        cmd.Parameters.AddWithValue("$match", match);
        cmd.Parameters.AddWithValue("$lim", want);
        BindFilters(cmd, f);
        try
        {
            return ReadHits(cmd, matchType, snippetOrdinal: 15);
        }
        catch (SqliteException)
        {
            throw KnowledgeException.Invalid("The search text could not be processed. Use plain words or a task key.");
        }
    }

    private List<SearchHit> ReadHits(SqliteCommand cmd, string matchType, int? snippetOrdinal)
    {
        var list = new List<SearchHit>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var d = ReadDoc(r);
            var snippet = snippetOrdinal is int o && !r.IsDBNull(o) ? r.GetString(o) : "";
            if (string.IsNullOrWhiteSpace(snippet)) snippet = d.Content;
            list.Add(new SearchHit
            {
                Id = d.Id,
                Project = d.Project,
                ProjectDisplay = d.ProjectDisplay,
                ExternalKey = d.ExternalKey,
                Title = d.Title,
                Kind = d.Kind,
                Summary = Clip(d.Summary),
                Snippet = Clip(snippet),
                Tags = d.Tags,
                Status = d.Status,
                SupersededBy = d.SupersededBy,
                UpdatedAtUtc = d.UpdatedAtUtc,
                Version = d.Version,
                MatchType = matchType,
            });
        }
        return list;
    }

    private string Clip(string text)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var max = _o.Search.SnippetCharacters;
        if (flat.Length <= max) return flat;
        var cut = max - 1;
        if (char.IsHighSurrogate(flat[cut - 1])) cut--;
        return flat[..cut] + "…";
    }

    // ---------------------------------------------------------------- projects, delete, export

    public Task<IReadOnlyList<ProjectInfo>> ProjectsAsync(CancellationToken ct)
    {
        using var c = _db.OpenConnection();
        using var cmd = Cmd(c, null, """
            SELECT project,
                   (SELECT project_display FROM documents x WHERE x.project = d.project ORDER BY updated_at DESC LIMIT 1),
                   count(*)
            FROM documents d GROUP BY project ORDER BY project;
            """);
        var list = new List<ProjectInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(new ProjectInfo(r.GetString(0), r.GetString(1), r.GetInt32(2)));
        return Task.FromResult<IReadOnlyList<ProjectInfo>>(list);
    }

    /// <summary>Deletes the card (and its FTS entry via trigger) only if the version still matches.</summary>
    public async Task<DeleteResult> DeleteAsync(string id, long expectedVersion, CancellationToken ct)
    {
        var norm = KnowledgeValidator.NormalizeId(id, "id");
        using (var gate = await _db.EnterWriteAsync(_db.WriteTimeout, ct))
        {
            using var c = _db.OpenConnection();
            using var tx = c.BeginTransaction();
            var current = LoadDocument(c, tx, norm)
                ?? throw new KnowledgeException(ErrorCodes.NotFound, "Card not found. It was probably deleted already.", new Dictionary<string, object?> { ["id"] = norm });
            if (current.Version != expectedVersion)
                throw new KnowledgeException(ErrorCodes.VersionConflict,
                    $"Card was changed after you opened it (version {expectedVersion} → {current.Version}). Review the new version before deleting.",
                    new Dictionary<string, object?> { ["id"] = norm, ["currentVersion"] = current.Version });
            using (var hist = Cmd(c, tx, "DELETE FROM document_versions WHERE document_id=$id;"))
            {
                hist.Parameters.AddWithValue("$id", norm);
                hist.ExecuteNonQuery();
            }
            using (var del = Cmd(c, tx, "DELETE FROM documents WHERE id=$id AND version=$v;"))
            {
                del.Parameters.AddWithValue("$id", norm);
                del.Parameters.AddWithValue("$v", expectedVersion);
                del.ExecuteNonQuery();
            }
            var now = Now();
            Audit(c, tx, "delete", norm, now);
            tx.Commit();
            ct = CancellationToken.None;
            DocumentDeletedSafe();
            return new DeleteResult { Id = norm, DeletedAtUtc = now };
        }
    }

    // ---------------------------------------------------------------- version history

    /// <summary>Stores the current card as a previous version and keeps only the newest MaxHistoryVersions.</summary>
    private void ArchiveVersion(SqliteConnection c, SqliteTransaction tx, KnowledgeDocument d, DateTime now)
    {
        var keep = _o.Knowledge.MaxHistoryVersions;
        if (keep <= 0) return;
        using (var cmd = Cmd(c, tx, """
            INSERT OR REPLACE INTO document_versions(document_id, version, saved_at, replaced_at, project_display, external_key, title, kind,
                summary, content, tags_json, status, superseded_by, sources_json)
            VALUES ($id, $v, $saved, $now, $display, $key, $title, $kind, $summary, $content, $tags, $status, $sup, $sources);
            """))
        {
            cmd.Parameters.AddWithValue("$id", d.Id);
            cmd.Parameters.AddWithValue("$v", d.Version);
            cmd.Parameters.AddWithValue("$saved", Iso(d.UpdatedAtUtc));
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$display", d.ProjectDisplay);
            cmd.Parameters.AddWithValue("$key", (object?)d.ExternalKey ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$title", d.Title);
            cmd.Parameters.AddWithValue("$kind", d.Kind);
            cmd.Parameters.AddWithValue("$summary", d.Summary);
            cmd.Parameters.AddWithValue("$content", d.Content);
            cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(d.Tags, Json));
            cmd.Parameters.AddWithValue("$status", d.Status);
            cmd.Parameters.AddWithValue("$sup", (object?)d.SupersededBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(d.Sources, Json));
            cmd.ExecuteNonQuery();
        }
        using var prune = Cmd(c, tx, """
            DELETE FROM document_versions WHERE document_id=$id AND version NOT IN
                (SELECT version FROM document_versions WHERE document_id=$id ORDER BY version DESC LIMIT $keep);
            """);
        prune.Parameters.AddWithValue("$id", d.Id);
        prune.Parameters.AddWithValue("$keep", keep);
        prune.ExecuteNonQuery();
    }

    /// <summary>Previous versions of a card, newest first. The current version is not included.</summary>
    public IReadOnlyList<VersionInfo> ListVersions(string id)
    {
        var norm = KnowledgeValidator.NormalizeId(id, "id");
        using var c = _db.OpenConnection();
        if (LoadDocument(c, null, norm) is null)
            throw new KnowledgeException(ErrorCodes.NotFound, "Card not found. It may have been deleted.", new Dictionary<string, object?> { ["id"] = norm });
        using var cmd = Cmd(c, null, "SELECT version, saved_at, replaced_at, title, status, length(content) FROM document_versions WHERE document_id=$id ORDER BY version DESC;");
        cmd.Parameters.AddWithValue("$id", norm);
        var list = new List<VersionInfo>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new VersionInfo
            {
                Version = r.GetInt64(0), SavedAtUtc = ParseIso(r.GetString(1)), ReplacedAtUtc = ParseIso(r.GetString(2)),
                Title = r.GetString(3), Status = r.GetString(4), ContentLength = r.GetInt32(5),
            });
        return list;
    }

    /// <summary>A full previous version (project and key are those of the current card).</summary>
    public KnowledgeDocument GetVersion(string id, long version)
    {
        var norm = KnowledgeValidator.NormalizeId(id, "id");
        using var c = _db.OpenConnection();
        return LoadVersion(c, null, norm, version)
            ?? throw new KnowledgeException(ErrorCodes.NotFound, $"Version {version} of this card is not stored.", new Dictionary<string, object?> { ["id"] = norm, ["version"] = version });
    }

    private static KnowledgeDocument? LoadVersion(SqliteConnection c, SqliteTransaction? tx, string id, long version)
    {
        var current = LoadDocument(c, tx, id);
        if (current is null) return null;
        using var cmd = Cmd(c, tx, """
            SELECT title, kind, summary, content, tags_json, status, superseded_by, sources_json, saved_at
            FROM document_versions WHERE document_id=$id AND version=$v;
            """);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$v", version);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return current with
        {
            Title = r.GetString(0),
            Kind = r.GetString(1),
            Summary = r.GetString(2),
            Content = r.GetString(3),
            Tags = JsonSerializer.Deserialize<string[]>(r.GetString(4), Json) ?? [],
            Status = r.GetString(5),
            SupersededBy = r.IsDBNull(6) ? null : r.GetString(6),
            Sources = JsonSerializer.Deserialize<KnowledgeSource[]>(r.GetString(7), Json) ?? [],
            UpdatedAtUtc = ParseIso(r.GetString(8)),
            Version = version,
        };
    }

    /// <summary>
    /// Makes an old version current again as a new version. Nothing is lost: the replaced current version goes to history.
    /// Project and key stay as they are now.
    /// </summary>
    public async Task<SaveResult> RestoreAsync(string id, long version, long expectedVersion, CancellationToken ct)
    {
        var norm = KnowledgeValidator.NormalizeId(id, "id");
        using var gate = await _db.EnterWriteAsync(_db.WriteTimeout, ct);
        using var c = _db.OpenConnection();
        using var tx = c.BeginTransaction();
        var current = LoadDocument(c, tx, norm)
            ?? throw new KnowledgeException(ErrorCodes.NotFound, "Card not found. It may have been deleted.", new Dictionary<string, object?> { ["id"] = norm });
        if (current.Version != expectedVersion)
            throw new KnowledgeException(ErrorCodes.VersionConflict,
                $"Card was changed after you opened it (version {expectedVersion} → {current.Version}). Review the new version first.",
                new Dictionary<string, object?> { ["id"] = norm, ["currentVersion"] = current.Version });
        var old = LoadVersion(c, tx, norm, version)
            ?? throw new KnowledgeException(ErrorCodes.NotFound, $"Version {version} of this card is not stored.", new Dictionary<string, object?> { ["id"] = norm, ["version"] = version });
        if (old.SupersededBy is not null && (old.SupersededBy == norm || !Exists(c, tx, old.SupersededBy)))
            old = old with { SupersededBy = null };

        var now = Now();
        ArchiveVersion(c, tx, current, now);
        using (var cmd = Cmd(c, tx, """
            UPDATE documents SET title=$title, kind=$kind, summary=$summary, content=$content, tags_json=$tags, tags_text=$tagsText,
                status=$status, superseded_by=$sup, sources_json=$sources, stems=$stems, updated_at=$now, version=version+1
            WHERE id=$id AND version=$expected;
            """))
        {
            cmd.Parameters.AddWithValue("$title", old.Title);
            cmd.Parameters.AddWithValue("$kind", old.Kind);
            cmd.Parameters.AddWithValue("$summary", old.Summary);
            cmd.Parameters.AddWithValue("$content", old.Content);
            cmd.Parameters.AddWithValue("$tags", JsonSerializer.Serialize(old.Tags, Json));
            cmd.Parameters.AddWithValue("$tagsText", string.Join(' ', old.Tags));
            cmd.Parameters.AddWithValue("$status", old.Status);
            cmd.Parameters.AddWithValue("$sup", (object?)old.SupersededBy ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$sources", JsonSerializer.Serialize(old.Sources, Json));
            cmd.Parameters.AddWithValue("$stems", Stemmer.StemText(old.Title, old.Summary, string.Join(' ', old.Tags), old.Content));
            cmd.Parameters.AddWithValue("$now", Iso(now));
            cmd.Parameters.AddWithValue("$id", norm);
            cmd.Parameters.AddWithValue("$expected", expectedVersion);
            if (cmd.ExecuteNonQuery() != 1)
                throw new KnowledgeException(ErrorCodes.VersionConflict, "Card was changed concurrently. Re-read it.");
        }
        Audit(c, tx, "restore", norm, now);
        tx.Commit();
        return new SaveResult { Id = norm, Version = current.Version + 1, Operation = "restored", SavedAtUtc = now };
    }

    private void DocumentDeletedSafe()
    {
        try { DocumentDeleted?.Invoke(); } catch { /* scheduling must never fail a committed delete */ }
    }

    public IEnumerable<KnowledgeDocument> ExportAll()
    {
        using var c = _db.OpenConnection();
        using var cmd = Cmd(c, null, $"SELECT {DocColumns} FROM documents d ORDER BY d.project, d.external_key_norm, d.id;");
        using var r = cmd.ExecuteReader();
        while (r.Read()) yield return ReadDoc(r);
    }

    public int Count()
    {
        using var c = _db.OpenConnection();
        return (int)SqliteDatabase.Scalar<long>(c, "SELECT count(*) FROM documents;");
    }

    /// <summary>FTS5 integrity check against the content table.</summary>
    public bool CheckIndex()
    {
        using var c = _db.OpenConnection();
        try
        {
            SqliteDatabase.Exec(c, "INSERT INTO documents_fts(documents_fts, rank) VALUES ('integrity-check', 1);");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public async Task RebuildIndexAsync(CancellationToken ct)
    {
        using var gate = await _db.EnterWriteAsync(_db.WriteTimeout, ct);
        using var c = _db.OpenConnection();
        SqliteDatabase.Exec(c, "INSERT INTO documents_fts(documents_fts) VALUES ('rebuild');");
    }

    private static SqliteCommand Cmd(SqliteConnection c, SqliteTransaction? tx, string sql)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return cmd;
    }
}

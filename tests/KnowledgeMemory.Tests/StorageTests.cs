using KnowledgeMemory.Core;
using KnowledgeMemory.Storage;
using Microsoft.Data.Sqlite;

namespace KnowledgeMemory.Tests;

public sealed class StorageTests : IDisposable
{
    private readonly TempStore _t = new();
    private KnowledgeStore S => _t.Store;
    private static readonly CancellationToken Ct = CancellationToken.None;

    public void Dispose() => _t.Dispose();

    [Fact]
    public async Task Create_read_update_roundtrip_keeps_unicode_and_large_text()
    {
        var big = string.Concat(Enumerable.Repeat("Строка українською та English 😀 — ", 2500)); // ~90k chars
        var created = await S.SaveAsync(Samples.Card(content: big, title: "Задача 😀 «кавычки» ї ґ"), Ct);
        Assert.Equal("created", created.Operation);
        Assert.Equal(1, created.Version);

        var page = await S.GetAsync(created.Id, null, null, Ct);
        var text = page.Document.Content;
        for (var next = page.NextOffset; next is int n; )
        {
            var p = await S.GetAsync(created.Id, n, null, Ct);
            text += p.Document.Content;
            next = p.NextOffset;
        }
        Assert.Equal(big.Trim(), text);
        Assert.Equal("Задача 😀 «кавычки» ї ґ", page.Document.Title);
        Assert.Equal("revqora", page.Document.Project);
        Assert.Equal("Revqora", page.Document.ProjectDisplay);

        var upd = Samples.Card(title: "Новое название") with { Id = created.Id, ExpectedVersion = 1 };
        var updated = await S.SaveAsync(upd, Ct);
        Assert.Equal("updated", updated.Operation);
        Assert.Equal(2, updated.Version);
        Assert.Equal("Новое название", (await S.GetAsync(created.Id, null, null, Ct)).Document.Title);
    }

    [Fact]
    public async Task Too_large_content_is_rejected_by_server()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() =>
            S.SaveAsync(Samples.Card(content: new string('x', _t.Options.Knowledge.MaxContentCharacters + 1)), Ct));
        Assert.Equal(ErrorCodes.Validation, ex.Code);
        Assert.Equal(0, S.Count());
    }

    [Fact]
    public async Task Keys_are_strings_with_leading_zeros_and_prefixes_and_hash_is_ignored()
    {
        var a = await S.SaveAsync(Samples.Card(key: "#0245"), Ct);
        var d = (await S.GetAsync(a.Id, null, null, Ct)).Document;
        Assert.Equal("0245", d.ExternalKey);
        var dup = await Assert.ThrowsAsync<KnowledgeException>(() => S.SaveAsync(Samples.Card(key: "0245"), Ct));
        Assert.Equal(ErrorCodes.DuplicateKey, dup.Code);
        // "245" is a different key than "0245".
        await S.SaveAsync(Samples.Card(key: "245"), Ct);
        // Prefix keys compare case-insensitively.
        await S.SaveAsync(Samples.Card(key: "ET-6449"), Ct);
        var dup2 = await Assert.ThrowsAsync<KnowledgeException>(() => S.SaveAsync(Samples.Card(key: "et-6449"), Ct));
        Assert.Equal(ErrorCodes.DuplicateKey, dup2.Code);
    }

    [Fact]
    public async Task Duplicate_project_key_returns_existing_id_and_does_not_overwrite()
    {
        var first = await S.SaveAsync(Samples.Card(title: "Первая"), Ct);
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => S.SaveAsync(Samples.Card(title: "Вторая"), Ct));
        Assert.Equal(ErrorCodes.DuplicateKey, ex.Code);
        Assert.Equal(first.Id, ex.Details["existingId"]);
        Assert.Equal(1, S.Count());
        Assert.Equal("Первая", (await S.GetAsync(first.Id, null, null, Ct)).Document.Title);
    }

    [Fact]
    public async Task Cards_without_key_are_allowed_many_times()
    {
        await S.SaveAsync(Samples.Card(key: null), Ct);
        await S.SaveAsync(Samples.Card(key: null), Ct);
        await S.SaveAsync(Samples.Card(key: "  "), Ct);
        Assert.Equal(3, S.Count());
    }

    [Fact]
    public async Task Same_key_in_two_projects_returns_both_cards_without_mixing()
    {
        var a = await S.SaveAsync(Samples.Card(project: "Revqora", key: "245", title: "Импорт Revqora"), Ct);
        var b = await S.SaveAsync(Samples.Card(project: "Typlo", key: "245", title: "Раскладка Typlo"), Ct);
        var all = await S.SearchAsync(new SearchQuery { Query = "245" }, Ct);
        Assert.Equal(2, all.Items.Count(h => h.MatchType == "exactKey"));
        Assert.Contains(all.Items, h => h.Id == a.Id && h.Project == "revqora");
        Assert.Contains(all.Items, h => h.Id == b.Id && h.Project == "typlo");

        var scoped = await S.SearchAsync(new SearchQuery { Project = "typlo", ExternalKey = "245" }, Ct);
        Assert.Single(scoped.Items);
        Assert.Equal(b.Id, scoped.Items[0].Id);
    }

    [Fact]
    public async Task Concurrent_updates_one_succeeds_other_conflicts_and_first_data_kept()
    {
        var c = await S.SaveAsync(Samples.Card(), Ct);
        var u1 = Samples.Card(title: "Клиент A") with { Id = c.Id, ExpectedVersion = 1 };
        var u2 = Samples.Card(title: "Клиент B") with { Id = c.Id, ExpectedVersion = 1 };
        var results = await Task.WhenAll(Try(u1), Try(u2));
        Assert.Single(results, r => r is SaveResult);
        var conflict = Assert.Single(results.OfType<KnowledgeException>());
        Assert.Equal(ErrorCodes.VersionConflict, conflict.Code);
        Assert.Equal(2L, conflict.Details["currentVersion"]);
        var winner = results.OfType<SaveResult>().Single();
        var doc = (await S.GetAsync(c.Id, null, null, Ct)).Document;
        Assert.Equal(2, doc.Version);
        Assert.Equal(winner.Id, doc.Id);
        Assert.Contains(doc.Title, new[] { "Клиент A", "Клиент B" });

        async Task<object> Try(SaveRequest r)
        {
            try { return await S.SaveAsync(r, Ct); }
            catch (KnowledgeException ex) { return ex; }
        }
    }

    [Fact]
    public async Task Update_requires_expected_version()
    {
        var c = await S.SaveAsync(Samples.Card(), Ct);
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => S.SaveAsync(Samples.Card() with { Id = c.Id }, Ct));
        Assert.Equal(ErrorCodes.Validation, ex.Code);
    }

    [Fact]
    public async Task Repeated_request_id_is_idempotent_and_changed_payload_is_rejected()
    {
        var req = Samples.Card();
        var first = await S.SaveAsync(req, Ct);
        var again = await S.SaveAsync(req, Ct);
        Assert.Equal(first.Id, again.Id);
        Assert.Equal(first.Version, again.Version);
        Assert.True(again.Replayed);
        Assert.Equal(1, S.Count());

        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => S.SaveAsync(req with { Title = "Другое" }, Ct));
        Assert.Equal(ErrorCodes.IdempotencyMismatch, ex.Code);
        Assert.Equal(1, S.Count());
    }

    [Fact]
    public async Task Identical_update_reports_unchanged_without_version_bump()
    {
        var req = Samples.Card();
        var c = await S.SaveAsync(req, Ct);
        var same = await S.SaveAsync(req with { RequestId = "req-same-000001", Id = c.Id, ExpectedVersion = 1 }, Ct);
        Assert.Equal("unchanged", same.Operation);
        Assert.Equal(1, same.Version);
    }

    [Fact]
    public async Task Update_to_colliding_key_is_rejected()
    {
        await S.SaveAsync(Samples.Card(key: "1"), Ct);
        var b = await S.SaveAsync(Samples.Card(key: "2"), Ct);
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => S.SaveAsync(Samples.Card(key: "1") with { Id = b.Id, ExpectedVersion = 1 }, Ct));
        Assert.Equal(ErrorCodes.DuplicateKey, ex.Code);
    }

    [Fact]
    public async Task Update_and_delete_are_visible_in_search_immediately()
    {
        var c = await S.SaveAsync(Samples.Card(content: "Уникальноеслово первое"), Ct);
        Assert.Single((await S.SearchAsync(new SearchQuery { Query = "уникальноеслово" }, Ct)).Items);

        await S.SaveAsync(Samples.Card(content: "Совсем другой текст") with { Id = c.Id, ExpectedVersion = 1 }, Ct);
        Assert.Empty((await S.SearchAsync(new SearchQuery { Query = "уникальноеслово" }, Ct)).Items);
        Assert.Single((await S.SearchAsync(new SearchQuery { Query = "совсем другой" }, Ct)).Items);

        await S.DeleteAsync(c.Id, 2, Ct);
        Assert.Empty((await S.SearchAsync(new SearchQuery { Query = "совсем другой" }, Ct)).Items);
        Assert.Empty((await S.SearchAsync(new SearchQuery { Query = "245" }, Ct)).Items);
        Assert.True(S.CheckIndex());
    }

    [Fact]
    public async Task Delete_with_stale_version_conflicts_and_keeps_card()
    {
        var c = await S.SaveAsync(Samples.Card(), Ct);
        await S.SaveAsync(Samples.Card(title: "Изменено") with { Id = c.Id, ExpectedVersion = 1 }, Ct);
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => S.DeleteAsync(c.Id, 1, Ct));
        Assert.Equal(ErrorCodes.VersionConflict, ex.Code);
        Assert.Equal(1, S.Count());
        var missing = await Assert.ThrowsAsync<KnowledgeException>(() => S.DeleteAsync(Guid.NewGuid().ToString(), 1, Ct));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
    }

    [Theory]
    [InlineData("\"")]
    [InlineData("\"unbalanced")]
    [InlineData("a OR b NOT c NEAR(d e)")]
    [InlineData("title:x")]
    [InlineData("((()))")]
    [InlineData("x* ^y -z +w")]
    [InlineData("'; DROP TABLE documents; --")]
    [InlineData("ET-6449 \" OR 1=1")]
    [InlineData("{a b}:c")]
    public async Task Hostile_queries_are_safe_and_do_not_bypass_project_filter(string q)
    {
        await S.SaveAsync(Samples.Card(project: "secret", key: "1", content: "a b c d e x y z w title OR NOT"), Ct);
        await S.SaveAsync(Samples.Card(project: "public", key: "1", content: "nothing"), Ct);
        var r = await S.SearchAsync(new SearchQuery { Query = q, Project = "public" }, Ct);
        Assert.All(r.Items, h => Assert.Equal("public", h.Project));
        Assert.Equal(2, S.Count());
    }

    [Fact]
    public async Task Very_long_query_is_truncated_not_failed()
    {
        await S.SaveAsync(Samples.Card(), Ct);
        var r = await S.SearchAsync(new SearchQuery { Query = string.Concat(Enumerable.Repeat("импорт ", 5000)) }, Ct);
        Assert.NotNull(r);
    }

    [Fact]
    public async Task Search_response_has_no_full_content_and_is_bounded()
    {
        var huge = string.Concat(Enumerable.Repeat("импорт данных подробно ", 4000));
        for (var i = 0; i < 30; i++)
            await S.SaveAsync(Samples.Card(key: "K" + i, content: huge, summary: new string('с', 1900)), Ct);
        var r = await S.SearchAsync(new SearchQuery { Query = "импорт", Limit = 100 }, Ct);
        Assert.True(r.Items.Count <= _t.Options.Search.MaxLimit);
        var total = r.Items.Sum(h => h.Summary.Length + h.Snippet.Length + h.Title.Length);
        Assert.True(total <= _t.Options.Search.MaxResponseCharacters + 1000, $"response chars {total}");
        Assert.All(r.Items, h => Assert.True(h.Snippet.Length <= _t.Options.Search.SnippetCharacters));
        Assert.True(r.HasMore);
        Assert.NotNull(r.NextCursor);

        var next = await S.SearchAsync(new SearchQuery { Query = "импорт", Limit = 100, Cursor = r.NextCursor }, Ct);
        Assert.NotEmpty(next.Items);
        Assert.Empty(next.Items.Select(h => h.Id).Intersect(r.Items.Select(h => h.Id)));
    }

    [Fact]
    public async Task Get_pages_large_content_explicitly()
    {
        var text = new string('а', 70_000);
        var c = await S.SaveAsync(Samples.Card(content: text), Ct);
        var p1 = await S.GetAsync(c.Id, null, null, Ct);
        Assert.True(p1.Truncated);
        Assert.Equal(70_000, p1.ContentTotalLength);
        Assert.Equal(_t.Options.Knowledge.MaxGetCharacters, p1.Document.Content.Length);
        var all = p1.Document.Content;
        var next = p1.NextOffset;
        while (next is int n)
        {
            var p = await S.GetAsync(c.Id, n, null, Ct);
            all += p.Document.Content;
            next = p.NextOffset;
        }
        Assert.Equal(text, all);
    }

    [Fact]
    public async Task Superseded_cards_are_marked_and_completed_cards_are_found()
    {
        var newer = await S.SaveAsync(Samples.Card(key: "D-2", kind: "decision", content: "Храним в SQLite"), Ct);
        await S.SaveAsync(Samples.Card(key: "D-1", kind: "decision", status: "superseded", content: "Храним в JSON файлах") with { SupersededBy = newer.Id }, Ct);
        await S.SaveAsync(Samples.Card(key: "T-1", status: "completed", content: "Завершённая задача про хранение"), Ct);

        var r = await S.SearchAsync(new SearchQuery { Query = "храним" }, Ct);
        var old = Assert.Single(r.Items, h => h.ExternalKey == "D-1");
        Assert.Equal("superseded", old.Status);
        Assert.Equal(newer.Id, old.SupersededBy);
        Assert.NotNull(r.Note);

        var done = await S.SearchAsync(new SearchQuery { Query = "хранение" }, Ct);
        Assert.Contains(done.Items, h => h.ExternalKey == "T-1" && h.Status == "completed");
    }

    [Fact]
    public async Task Superseded_by_must_exist()
    {
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => S.SaveAsync(Samples.Card() with { SupersededBy = Guid.NewGuid().ToString() }, Ct));
        Assert.Equal(ErrorCodes.Validation, ex.Code);
    }

    [Fact]
    public async Task Failed_transaction_leaves_data_and_index_consistent()
    {
        var c = await S.SaveAsync(Samples.Card(content: "стабильный текст"), Ct);
        // A transaction that updates the card (and its FTS rows via triggers) and then fails must leave no trace.
        using (var conn = _t.Db.OpenConnection())
        {
            using var tx = conn.BeginTransaction();
            SqliteDatabase.Exec(conn, "UPDATE documents SET content='сломано', version=version+1;", tx);
            SqliteDatabase.Exec(conn, "INSERT INTO request_log VALUES('x','y','z',1,'updated','2020','[]');", tx);
            Assert.Throws<SqliteException>(() => SqliteDatabase.Exec(conn, "INSERT INTO request_log VALUES('x','y','z',1,'updated','2020','[]');", tx));
            tx.Rollback();
        }
        var d = (await S.GetAsync(c.Id, null, null, Ct)).Document;
        Assert.Equal("стабильный текст", d.Content);
        Assert.Equal(1, d.Version);
        Assert.True(S.CheckIndex());
        Assert.Single((await S.SearchAsync(new SearchQuery { Query = "стабильный" }, Ct)).Items);
        Assert.Empty((await S.SearchAsync(new SearchQuery { Query = "сломано" }, Ct)).Items);
    }

    [Fact]
    public async Task Index_can_be_rebuilt_from_main_table()
    {
        await S.SaveAsync(Samples.Card(content: "восстановимый индекс"), Ct);
        using (var c = _t.Db.OpenConnection())
            SqliteDatabase.Exec(c, "INSERT INTO documents_fts(documents_fts) VALUES('delete-all');");
        Assert.Empty((await S.SearchAsync(new SearchQuery { Query = "восстановимый" }, Ct)).Items);
        await S.RebuildIndexAsync(Ct);
        Assert.Single((await S.SearchAsync(new SearchQuery { Query = "восстановимый" }, Ct)).Items);
        Assert.True(S.CheckIndex());
    }

    [Fact]
    public async Task Secret_like_content_produces_a_warning()
    {
        var r = await S.SaveAsync(Samples.Card(content: "token ghp_abcdefghijklmnopqrstuvwxyz0123456789AB"), Ct);
        Assert.NotEmpty(r.Warnings);
    }

    [Fact]
    public async Task Dangerous_source_url_schemes_are_rejected()
    {
        foreach (var url in new[] { "javascript:alert(1)", "file:///C:/Windows", "data:text/html,x", "vbscript:x" })
        {
            var req = Samples.Card() with { Sources = [new KnowledgeSource { Url = url }] };
            var ex = await Assert.ThrowsAsync<KnowledgeException>(() => S.SaveAsync(req, Ct));
            Assert.Equal(ErrorCodes.Validation, ex.Code);
        }
    }

    [Fact]
    public async Task New_database_uses_incremental_auto_vacuum_wal_and_fts5()
    {
        using var c = _t.Db.OpenConnection();
        Assert.Equal(2L, SqliteDatabase.Scalar<long>(c, "PRAGMA auto_vacuum;"));
        Assert.Equal("wal", SqliteDatabase.Scalar<string>(c, "PRAGMA journal_mode;"));
        Assert.Equal(2L, SqliteDatabase.Scalar<long>(c, "PRAGMA synchronous;")); // FULL
        Assert.True(SqliteDatabase.HasFts5(c));
        await Task.CompletedTask;
    }

    [Fact]
    public void Foreign_file_is_not_replaced_by_a_new_database()
    {
        var dir = TestPaths.NewDir();
        try
        {
            var o = TestPaths.Options(dir);
            Directory.CreateDirectory(o.ResolvedDataDirectory);
            File.WriteAllText(o.DatabasePath, "this is not sqlite");
            Assert.ThrowsAny<Exception>(() => SqliteDatabase.Open(o));
            Assert.Equal("this is not sqlite", File.ReadAllText(o.DatabasePath));
        }
        finally { TestPaths.TryDelete(dir); }
    }

    [Fact]
    public async Task Audit_and_request_logs_contain_no_content()
    {
        var c = await S.SaveAsync(Samples.Card(content: "секретное содержимое карточки"), Ct);
        await S.DeleteAsync(c.Id, 1, Ct);
        using var conn = _t.Db.OpenConnection();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT group_concat(request_id||payload_hash||document_id||operation||warnings_json) FROM request_log UNION ALL SELECT group_concat(operation||document_id) FROM audit_log;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (!r.IsDBNull(0)) Assert.DoesNotContain("секретное", r.GetString(0));
    }
}

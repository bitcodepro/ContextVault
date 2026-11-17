using System.Net;
using System.Net.Http.Json;
using KnowledgeMemory.Core;
using KnowledgeMemory.Storage;
using Microsoft.Data.Sqlite;

namespace KnowledgeMemory.Tests;

public sealed class StemmerTests
{
    [Theory]
    [InlineData("клавиатурой", "клавиатуры")]
    [InlineData("импорта", "импортом")]
    [InlineData("дубликатов", "дубликаты")]
    [InlineData("решения", "решение")]
    [InlineData("сохранили", "сохранить")]
    [InlineData("перезапуска", "перезапуск")]
    [InlineData("імпорту", "імпорт")]
    [InlineData("рішення", "рішенням")]
    [InlineData("decisions", "decision")]
    [InlineData("deployed", "deploying")]
    [InlineData("duplicates", "duplicate")]
    public void Word_forms_share_a_stem(string a, string b) => Assert.Equal(Stemmer.Stem(a), Stemmer.Stem(b));

    [Theory]
    [InlineData("245")]
    [InlineData("ET-6449")]
    [InlineData("api")]
    public void Keys_and_short_words_are_kept(string word) => Assert.Equal(word.ToLowerInvariant(), Stemmer.Stem(word));

    [Fact]
    public void Stem_text_is_unique_and_space_separated() =>
        Assert.Equal(Stemmer.Stem("дубликаты"), Stemmer.StemText("дубликаты", "дубликатов"));
}

public sealed class HistoryTests : IDisposable
{
    private readonly TempStore _t = new(o => o.Knowledge.MaxHistoryVersions = 3);
    private KnowledgeStore S => _t.Store;
    private static readonly CancellationToken Ct = CancellationToken.None;

    public void Dispose() => _t.Dispose();

    private async Task<string> CreateWithVersionsAsync(int versions)
    {
        var created = await S.SaveAsync(Samples.Card(content: "## Задача\nверсия 1"), Ct);
        for (var v = 2; v <= versions; v++)
            await S.SaveAsync(Samples.Card(content: $"## Задача\nверсия {v}") with { Id = created.Id, ExpectedVersion = v - 1 }, Ct);
        return created.Id;
    }

    [Fact]
    public async Task Update_keeps_previous_versions_newest_first()
    {
        var id = await CreateWithVersionsAsync(3);
        var list = S.ListVersions(id);
        Assert.Equal(new long[] { 2, 1 }, list.Select(v => v.Version));
        Assert.Equal("## Задача\nверсия 1", S.GetVersion(id, 1).Content);
        Assert.Equal("## Задача\nверсия 3", (await S.GetAsync(id, null, null, Ct)).Document.Content);
    }

    [Fact]
    public async Task Unchanged_save_does_not_add_a_version()
    {
        var created = await S.SaveAsync(Samples.Card(title: "Одинаковая"), Ct);
        var same = Samples.Card(title: "Одинаковая") with { Id = created.Id, ExpectedVersion = 1 };
        Assert.Equal("unchanged", (await S.SaveAsync(same, Ct)).Operation);
        Assert.Empty(S.ListVersions(created.Id));
    }

    [Fact]
    public async Task History_is_capped()
    {
        var id = await CreateWithVersionsAsync(6);
        Assert.Equal(new long[] { 5, 4, 3 }, S.ListVersions(id).Select(v => v.Version));
    }

    [Fact]
    public async Task Restore_makes_an_old_version_current_without_losing_the_replaced_one()
    {
        var id = await CreateWithVersionsAsync(3);
        var r = await S.RestoreAsync(id, 1, expectedVersion: 3, Ct);
        Assert.Equal("restored", r.Operation);
        Assert.Equal(4, r.Version);
        var doc = (await S.GetAsync(id, null, null, Ct)).Document;
        Assert.Equal("## Задача\nверсия 1", doc.Content);
        Assert.Equal(4, doc.Version);
        Assert.Equal("## Задача\nверсия 3", S.GetVersion(id, 3).Content);
        // The restored text is searchable again (index updated through the trigger).
        Assert.NotEmpty((await S.SearchAsync(new SearchQuery { Query = "версия 1" }, Ct)).Items);
        Assert.True(S.CheckIndex());
    }

    [Fact]
    public async Task Restore_checks_the_expected_version()
    {
        var id = await CreateWithVersionsAsync(2);
        var ex = await Assert.ThrowsAsync<KnowledgeException>(() => S.RestoreAsync(id, 1, expectedVersion: 1, Ct));
        Assert.Equal(ErrorCodes.VersionConflict, ex.Code);
        var missing = await Assert.ThrowsAsync<KnowledgeException>(() => S.RestoreAsync(id, 9, expectedVersion: 2, Ct));
        Assert.Equal(ErrorCodes.NotFound, missing.Code);
    }

    [Fact]
    public async Task Delete_removes_the_history_too()
    {
        var id = await CreateWithVersionsAsync(3);
        await S.DeleteAsync(id, 3, Ct);
        using var c = _t.Db.OpenConnection();
        Assert.Equal(0L, SqliteDatabase.Scalar<long>(c, "SELECT count(*) FROM document_versions;"));
    }

    [Fact]
    public async Task Since_filter_returns_only_recent_cards()
    {
        await S.SaveAsync(Samples.Card(key: "1"), Ct);
        var r = await S.SearchAsync(new SearchQuery { SinceDays = 1 }, Ct);
        Assert.Single(r.Items);
        await Assert.ThrowsAsync<KnowledgeException>(() => S.SearchAsync(new SearchQuery { SinceDays = 0 }, Ct));
    }
}

public sealed class MigrationTests
{
    [Fact]
    public async Task Version1_database_is_upgraded_with_stems_and_history()
    {
        var dir = TestPaths.NewDir();
        try
        {
            var o = TestPaths.Options(dir);
            string id;
            using (var db = SqliteDatabase.Open(o))
            {
                id = (await new KnowledgeStore(db).SaveAsync(Samples.Card(title: "Раскладка клавиатуры", content: "Переключение раскладки клавиатуры."), CancellationToken.None)).Id;
                // Turn the database back into the version 1 layout.
                using var c = db.OpenConnection();
                foreach (var sql in new[]
                {
                    "DROP TRIGGER documents_ai;", "DROP TRIGGER documents_ad;", "DROP TRIGGER documents_au;", "DROP TABLE documents_fts;",
                    "DROP TABLE document_versions;", "ALTER TABLE documents DROP COLUMN stems;",
                    "CREATE VIRTUAL TABLE documents_fts USING fts5(title, summary, tags_text, content, content='documents', content_rowid='doc_rowid', tokenize='unicode61 remove_diacritics 2');",
                    "INSERT INTO documents_fts(documents_fts) VALUES ('rebuild');",
                    "CREATE TRIGGER documents_ai AFTER INSERT ON documents BEGIN INSERT INTO documents_fts(rowid, title, summary, tags_text, content) VALUES (new.doc_rowid, new.title, new.summary, new.tags_text, new.content); END;",
                    "CREATE TRIGGER documents_ad AFTER DELETE ON documents BEGIN INSERT INTO documents_fts(documents_fts, rowid, title, summary, tags_text, content) VALUES ('delete', old.doc_rowid, old.title, old.summary, old.tags_text, old.content); END;",
                    "CREATE TRIGGER documents_au AFTER UPDATE ON documents BEGIN INSERT INTO documents_fts(documents_fts, rowid, title, summary, tags_text, content) VALUES ('delete', old.doc_rowid, old.title, old.summary, old.tags_text, old.content); INSERT INTO documents_fts(rowid, title, summary, tags_text, content) VALUES (new.doc_rowid, new.title, new.summary, new.tags_text, new.content); END;",
                    "UPDATE schema_info SET version = 1;",
                })
                    SqliteDatabase.Exec(c, sql);
            }

            using var upgraded = SqliteDatabase.Open(o);
            var store = new KnowledgeStore(upgraded);
            using (var c = upgraded.OpenConnection())
                Assert.Equal(2L, SqliteDatabase.Scalar<long>(c, "SELECT version FROM schema_info;"));
            var hits = (await store.SearchAsync(new SearchQuery { Query = "клавиатурой" }, CancellationToken.None)).Items;
            Assert.Equal(id, Assert.Single(hits).Id);
            Assert.True(store.CheckIndex());
            await store.SaveAsync(Samples.Card(title: "Новое") with { Id = id, ExpectedVersion = 1, RequestId = "req-migr-1" }, CancellationToken.None);
            Assert.Single(store.ListVersions(id));
        }
        finally
        {
            TestPaths.TryDelete(dir);
        }
    }
}

public sealed class HistoryApiTests : IAsyncLifetime
{
    private TempServer _s = null!;
    public async Task InitializeAsync() => _s = await TempServer.StartAsync();
    public async Task DisposeAsync() => await _s.DisposeAsync();

    [Fact]
    public async Task Versions_can_be_listed_read_and_restored_over_the_api()
    {
        using var http = _s.MachineClient();
        var created = await (await http.PostAsJsonAsync("/api/documents", Samples.Card(content: "старый текст"))).Content.ReadFromJsonAsync<SaveResult>();
        await http.PostAsJsonAsync("/api/documents", Samples.Card(content: "новый текст") with { Id = created!.Id, ExpectedVersion = 1 });

        var list = await http.GetFromJsonAsync<VersionInfo[]>($"/api/documents/{created.Id}/versions");
        Assert.Equal(1, Assert.Single(list!).Version);
        var old = await http.GetFromJsonAsync<KnowledgeDocument>($"/api/documents/{created.Id}/versions/1");
        Assert.Equal("старый текст", old!.Content);

        var conflict = await http.PostAsJsonAsync($"/api/documents/{created.Id}/versions/1/restore", new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        var ok = await http.PostAsJsonAsync($"/api/documents/{created.Id}/versions/1/restore", new { expectedVersion = 2 });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        Assert.Equal(3, (await ok.Content.ReadFromJsonAsync<SaveResult>())!.Version);
    }

    [Fact]
    public async Task Browser_restore_without_csrf_is_rejected()
    {
        using var http = _s.MachineClient();
        var created = await (await http.PostAsJsonAsync("/api/documents", Samples.Card())).Content.ReadFromJsonAsync<SaveResult>();
        using var anon = _s.AnonymousClient();
        var res = await anon.PostAsJsonAsync($"/api/documents/{created!.Id}/versions/1/restore", new { expectedVersion = 1 });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }
}

using KnowledgeMemory.Core;
using KnowledgeMemory.Storage;
using Microsoft.Data.Sqlite;
using Xunit.Abstractions;

namespace KnowledgeMemory.Tests;

public sealed class DiskTests(ITestOutputHelper output) : IDisposable
{
    private readonly TempStore _t = new();
    private static readonly CancellationToken Ct = CancellationToken.None;

    public void Dispose() => _t.Dispose();

    private async Task<List<SaveResult>> FillAsync(int count, int contentChars = 6000)
    {
        var rnd = new Random(42);
        var words = new[] { "импорт", "решение", "ограничение", "проверка", "storage", "vacuum", "індекс", "задача", "результат", "кэш" };
        var list = new List<SaveResult>();
        for (var i = 0; i < count; i++)
        {
            var sb = new System.Text.StringBuilder();
            while (sb.Length < contentChars) sb.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(100000)).Append(' ');
            list.Add(await _t.Store.SaveAsync(Samples.Card(key: "K-" + i, content: sb.ToString()), Ct));
        }
        return list;
    }

    [Fact]
    public async Task Mass_delete_then_background_reclaim_measurably_shrinks_working_files()
    {
        var docs = await FillAsync(1500);
        await _t.Disk.CheckpointTruncateAsync(Ct);
        var full = _t.Disk.WorkingBytes();
        output.WriteLine($"after insert: {full:N0} bytes");

        foreach (var d in docs.Take(1200)) await _t.Store.DeleteAsync(d.Id, d.Version, Ct);
        var beforeReclaim = _t.Disk.GetStats();
        output.WriteLine($"after delete: main={beforeReclaim.MainBytes:N0} wal={beforeReclaim.WalBytes:N0} freelist={beforeReclaim.FreelistPages}");
        Assert.True(beforeReclaim.FreelistPages > 0);

        await using var scheduler = new MaintenanceScheduler(_t.Disk, TimeSpan.Zero);
        var done = new TaskCompletionSource<DiskManager.ReclaimReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RunCompleted += r => { if (r.Outcome == DiskManager.ReclaimOutcome.Done) done.TrySetResult(r); };
        scheduler.Start();
        scheduler.RequestReclaim();
        var report = await done.Task.WaitAsync(TimeSpan.FromSeconds(120));

        var after = _t.Disk.GetStats();
        output.WriteLine($"after reclaim: main={after.MainBytes:N0} wal={after.WalBytes:N0} freelist={after.FreelistPages} total={_t.Disk.WorkingBytes():N0}");
        Assert.Equal(0, after.FreelistPages);
        Assert.Equal(0, after.WalBytes);
        Assert.True(_t.Disk.WorkingBytes() < full * 0.6, "working files should shrink substantially");
        Assert.Equal("idle", _t.Disk.Status.State);

        // Remaining data is intact.
        Assert.Equal(300, _t.Store.Count());
        foreach (var d in docs.Skip(1200).Take(20)) Assert.NotNull(_t.Store.Find(d.Id));
        using var c = _t.Db.OpenConnection();
        Assert.Equal("ok", SqliteDatabase.Scalar<string>(c, "PRAGMA integrity_check;"));
        Assert.True(_t.Store.CheckIndex());
        // Deleted documents are gone from search.
        var deletedKey = await _t.Store.SearchAsync(new SearchQuery { ExternalKey = "K-5" }, Ct);
        Assert.Empty(deletedKey.Items);
    }

    [Fact]
    public async Task Delete_all_and_full_compact_makes_total_size_much_smaller()
    {
        var docs = await FillAsync(800);
        await _t.Disk.CheckpointTruncateAsync(Ct);
        var initial = _t.Disk.WorkingBytes();
        foreach (var d in docs) await _t.Store.DeleteAsync(d.Id, d.Version, Ct);
        var r = await _t.Disk.CompactAsync(Ct);
        output.WriteLine($"initial={initial:N0} before={r.BytesBefore:N0} after={r.BytesAfter:N0} {r.DurationMs}ms");
        Assert.Equal("ok", r.Status);
        Assert.True(r.BytesAfter < initial / 10, $"{r.BytesAfter} should be far below {initial}");
        Assert.Equal(r.BytesAfter, _t.Disk.WorkingBytes());
        Assert.Equal("incremental", _t.Disk.GetStats().AutoVacuum); // VACUUM keeps the mode
    }

    [Fact]
    public async Task Held_reader_blocks_checkpoint_shows_busy_then_completes_after_release()
    {
        var docs = await FillAsync(200, 3000);
        foreach (var d in docs.Take(150)) await _t.Store.DeleteAsync(d.Id, d.Version, Ct);

        var reader = _t.Db.OpenConnection();
        var tx = reader.BeginTransaction(deferred: true);
        using (var cmd = reader.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT count(*) FROM documents;";
            cmd.ExecuteScalar(); // read snapshot held open
        }
        // Writes after the snapshot keep WAL frames the reader may need.
        await _t.Store.SaveAsync(Samples.Card(key: "after-reader"), Ct);

        await using var scheduler = new MaintenanceScheduler(_t.Disk, TimeSpan.Zero);
        var busySeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scheduler.RunCompleted += r =>
        {
            if (r.Outcome == DiskManager.ReclaimOutcome.CheckpointBusy) busySeen.TrySetResult();
            if (r.Outcome == DiskManager.ReclaimOutcome.Done) done.TrySetResult();
        };
        scheduler.Start();
        scheduler.RequestReclaim();
        await busySeen.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("busy", _t.Disk.Status.State);
        Assert.False(done.Task.IsCompleted);

        tx.Rollback();
        tx.Dispose();
        reader.Dispose();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("idle", _t.Disk.Status.State);
        Assert.Equal(0, _t.Disk.GetStats().WalBytes);
    }

    [Fact]
    public async Task Not_enough_disk_space_gives_clear_error_without_data_loss()
    {
        var docs = await FillAsync(50, 2000);
        _t.Disk.AvailableFreeSpace = _ => 1024;
        var r = await _t.Disk.CompactAsync(Ct);
        Assert.Equal("error", r.Status);
        Assert.Contains("Not enough free disk space", r.Error);
        Assert.Equal(50, _t.Store.Count());
        // Writers resume after the failed maintenance.
        await _t.Store.SaveAsync(Samples.Card(key: "after-fail"), Ct);
        await Assert.ThrowsAsync<KnowledgeException>(() => _t.Disk.BackupAsync(Ct));
        Assert.Equal("error", _t.Disk.Status.State);
    }

    [Fact]
    public async Task Interrupted_maintenance_leaves_database_readable_and_next_run_works()
    {
        await FillAsync(30, 2000);
        _t.Disk.BeforeVacuumForTests = () => throw new IOException("simulated interruption");
        var r = await _t.Disk.CompactAsync(Ct);
        Assert.Equal("error", r.Status);
        Assert.Equal(30, _t.Store.Count());
        await _t.Store.SaveAsync(Samples.Card(key: "after-interrupt"), Ct); // gate released

        _t.Disk.BeforeVacuumForTests = null;
        var ok = await _t.Disk.CompactAsync(Ct);
        Assert.Equal("ok", ok.Status);
        Assert.Equal(31, _t.Store.Count());
    }

    [Fact]
    public async Task Parallel_save_search_delete_and_compact_do_not_corrupt_or_hang()
    {
        var seed = await FillAsync(100, 1500);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < 40; i++)
                await _t.Store.SaveAsync(Samples.Card(key: $"P{w}-{i}", content: "параллельная запись " + i), cts.Token);
        }));
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            for (var i = 0; i < 60; i++)
                await _t.Store.SearchAsync(new SearchQuery { Query = "параллельная" }, cts.Token);
        }));
        var deleter = Task.Run(async () =>
        {
            foreach (var d in seed.Take(60)) await _t.Store.DeleteAsync(d.Id, d.Version, cts.Token);
        });
        var compactor = Task.Run(async () =>
        {
            for (var i = 0; i < 3; i++)
            {
                var r = await _t.Disk.CompactAsync(cts.Token);
                Assert.Contains(r.Status, new[] { "ok", "busy" });
            }
        });
        await Task.WhenAll(writers.Concat(readers).Append(deleter).Append(compactor)).WaitAsync(cts.Token);

        Assert.Equal(100 - 60 + 160, _t.Store.Count());
        using var c = _t.Db.OpenConnection();
        Assert.Equal("ok", SqliteDatabase.Scalar<string>(c, "PRAGMA integrity_check;"));
        Assert.True(_t.Store.CheckIndex());
    }

    [Fact]
    public async Task Backup_opens_independently_and_survives_deletes_in_working_db()
    {
        var docs = await FillAsync(20, 1000);
        var b = await _t.Disk.BackupAsync(Ct);
        Assert.True(b.IntegrityOk);
        var path = Path.Combine(_t.Options.BackupDirectory, b.FileName);
        foreach (var d in docs) await _t.Store.DeleteAsync(d.Id, d.Version, Ct);
        await _t.Disk.CompactAsync(Ct);

        Assert.True(File.Exists(path));
        using var conn = new SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
        conn.Open();
        Assert.Equal(20L, SqliteDatabase.Scalar<long>(conn, "SELECT count(*) FROM documents;"));
        Assert.Equal("ok", SqliteDatabase.Scalar<string>(conn, "PRAGMA integrity_check;"));
        var stats = _t.Disk.GetStats();
        Assert.Equal(1, stats.BackupCount);
        Assert.True(stats.BackupBytes > 0);
        Assert.Equal(stats.MainBytes + stats.WalBytes + stats.ShmBytes + stats.BackupBytes + stats.ExportBytes, stats.TotalBytes);
    }

    [Fact]
    public async Task Status_change_without_delete_does_not_shrink_or_lose_data()
    {
        var docs = await FillAsync(30, 2000);
        foreach (var d in docs)
            await _t.Store.SaveAsync(Samples.Card(key: (await _t.Store.GetAsync(d.Id, 0, 10, Ct)).Document.ExternalKey, status: "completed") with { Id = d.Id, ExpectedVersion = d.Version }, Ct);
        Assert.Equal(30, _t.Store.Count());
        Assert.Equal(30, (await _t.Store.SearchAsync(new SearchQuery { Status = "completed", Limit = 20 }, Ct)).Items.Count + 10);
    }
}

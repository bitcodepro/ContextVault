using System.Diagnostics;
using System.Globalization;
using KnowledgeMemory.Core;
using Microsoft.Data.Sqlite;

namespace KnowledgeMemory.Storage;

/// <summary>
/// Disk statistics, background incremental reclaim, full compaction and verified backups.
/// Only files inside DataDirectory are measured or touched.
/// </summary>
public sealed class DiskManager
{
    private readonly SqliteDatabase _db;
    private readonly object _statusLock = new();
    private MaintenanceStatus _status = new() { State = "idle" };

    public DiskManager(SqliteDatabase db) => _db = db;

    public MaintenanceStatus Status
    {
        get { lock (_statusLock) return _status; }
        private set { lock (_statusLock) _status = value; }
    }

    internal void SetStatus(Func<MaintenanceStatus, MaintenanceStatus> change)
    {
        lock (_statusLock) _status = change(_status);
    }

    // ---------------------------------------------------------------- stats

    public DiskStats GetStats()
    {
        var o = _db.Options;
        using var c = _db.OpenConnection();
        var pageSize = SqliteDatabase.Scalar<long>(c, "PRAGMA page_size;");
        var freelist = SqliteDatabase.Scalar<long>(c, "PRAGMA freelist_count;");
        var autoVacuum = SqliteDatabase.Scalar<long>(c, "PRAGMA auto_vacuum;") switch { 0 => "none", 1 => "full", 2 => "incremental", _ => "unknown" };
        var docs = (int)SqliteDatabase.Scalar<long>(c, "SELECT count(*) FROM documents;");

        var main = FileSize(_db.Path);
        var wal = FileSize(_db.Path + "-wal");
        var shm = FileSize(_db.Path + "-shm");
        var (backupBytes, backupCount) = DirSize(o.BackupDirectory, "*.db");
        var (exportBytes, _) = DirSize(o.ExportDirectory, "*");
        return new DiskStats
        {
            Documents = docs,
            MainBytes = main,
            WalBytes = wal,
            ShmBytes = shm,
            BackupBytes = backupBytes,
            BackupCount = backupCount,
            ExportBytes = exportBytes,
            TotalBytes = main + wal + shm + backupBytes + exportBytes,
            ReclaimableEstimateBytes = freelist * pageSize,
            PageSize = pageSize,
            FreelistPages = freelist,
            AutoVacuum = autoVacuum,
            Maintenance = Status,
        };
    }

    /// <summary>main + WAL + SHM: the working database files (backups excluded).</summary>
    public long WorkingBytes() => FileSize(_db.Path) + FileSize(_db.Path + "-wal") + FileSize(_db.Path + "-shm");

    private static long FileSize(string path)
    {
        try { return new FileInfo(path).Exists ? new FileInfo(path).Length : 0; }
        catch (IOException) { return 0; }
    }

    private static (long Bytes, int Count) DirSize(string dir, string pattern)
    {
        if (!Directory.Exists(dir)) return (0, 0);
        long total = 0;
        var count = 0;
        foreach (var f in Directory.EnumerateFiles(dir, pattern, SearchOption.TopDirectoryOnly))
        {
            total += FileSize(f);
            count++;
        }
        return (total, count);
    }

    // ---------------------------------------------------------------- incremental reclaim

    public enum ReclaimOutcome { Done, MorePending, CheckpointBusy }

    public sealed record ReclaimReport(ReclaimOutcome Outcome, long BytesBefore, long BytesAfter, long FreelistLeft, string Message);

    /// <summary>
    /// Returns free pages in small batches within a time budget, then checkpoints and truncates the WAL.
    /// Never claims success when a reader blocks the checkpoint.
    /// </summary>
    public async Task<ReclaimReport> ReclaimIncrementalAsync(CancellationToken ct)
    {
        var o = _db.Options.Storage;
        var before = WorkingBytes();
        var deadline = Stopwatch.StartNew();
        // FTS5 keeps deleted entries inside index segments; merge segments in small steps within the budget
        // so their pages become free pages that incremental_vacuum can return.
        var mergesDone = 0;
        while (deadline.Elapsed.TotalSeconds < o.MaintenanceBudgetSeconds / 2.0)
        {
            ct.ThrowIfCancellationRequested();
            using (await _db.EnterWriteAsync(_db.WriteTimeout, ct))
            {
                using var c = _db.OpenConnection();
                var changesBefore = SqliteDatabase.Scalar<long>(c, "SELECT total_changes();");
                SqliteDatabase.Exec(c, "INSERT INTO documents_fts(documents_fts, rank) VALUES ('merge', -500);");
                if (SqliteDatabase.Scalar<long>(c, "SELECT total_changes();") == changesBefore) break;
            }
            mergesDone++;
            await Task.Yield();
        }
        long freelist;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            using (await _db.EnterWriteAsync(_db.WriteTimeout, ct))
            {
                using var c = _db.OpenConnection();
                freelist = SqliteDatabase.Scalar<long>(c, "PRAGMA freelist_count;");
                if (freelist == 0) break;
                SqliteDatabase.Exec(c, $"PRAGMA incremental_vacuum({o.IncrementalVacuumPagesPerBatch});");
                freelist = SqliteDatabase.Scalar<long>(c, "PRAGMA freelist_count;");
            }
            if (freelist == 0 || deadline.Elapsed.TotalSeconds >= o.MaintenanceBudgetSeconds) break;
            await Task.Yield();
        }

        var checkpoint = await CheckpointTruncateAsync(ct);
        var after = WorkingBytes();
        if (!checkpoint.Ok)
            return new ReclaimReport(ReclaimOutcome.CheckpointBusy, before, after, freelist,
                "WAL checkpoint was blocked by an active reader; will retry.");
        if (freelist > 0)
            return new ReclaimReport(ReclaimOutcome.MorePending, before, after, freelist,
                $"{freelist} free pages left; continuing in the next batch.");
        return new ReclaimReport(ReclaimOutcome.Done, before, after, 0, "Free pages returned to the file system.");
    }

    public sealed record CheckpointResult(bool Ok, long Busy, long LogFrames, long CheckpointedFrames);

    public async Task<CheckpointResult> CheckpointTruncateAsync(CancellationToken ct)
    {
        using (await _db.EnterWriteAsync(_db.WriteTimeout, ct))
        {
            using var c = _db.OpenConnection();
            // Do not wait long on readers: report busy and retry later with backoff.
            SqliteDatabase.Exec(c, "PRAGMA busy_timeout=200;");
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            using var r = cmd.ExecuteReader();
            r.Read();
            var busy = r.GetInt64(0);
            var log = r.GetInt64(1);
            var done = r.GetInt64(2);
            return new CheckpointResult(busy == 0 && (log == -1 || log == done), busy, log, done);
        }
    }

    // ---------------------------------------------------------------- full compaction

    /// <summary>Extra free space required on top of twice the database size before running VACUUM.</summary>
    public const long VacuumSafetyMarginBytes = 64L * 1024 * 1024;

    /// <summary>Overridable for tests (simulated low disk space).</summary>
    public Func<string, long> AvailableFreeSpace { get; set; } = path => new DriveInfo(Path.GetPathRoot(path)!).AvailableFreeSpace;

    /// <summary>Hook for tests: runs inside the maintenance window, after the gate is taken.</summary>
    public Action? BeforeVacuumForTests { get; set; }

    public async Task<CompactResult> CompactAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var warnings = new List<string>();
        var before = WorkingBytes();
        IDisposable gate;
        try
        {
            // New mutations wait (bounded) on the same gate; they are never queued forever.
            gate = await _db.EnterWriteAsync(TimeSpan.FromSeconds(30), ct);
        }
        catch (KnowledgeException)
        {
            return new CompactResult { Status = "busy", BytesBefore = before, BytesAfter = before, DurationMs = sw.ElapsedMilliseconds, Warnings = warnings, Error = "Another write or maintenance operation did not finish in 30 seconds." };
        }

        SetStatus(s => s with { State = "running" });
        try
        {
            var main = new FileInfo(_db.Path).Length;
            var free = AvailableFreeSpace(_db.Path);
            var needed = main * 2 + VacuumSafetyMarginBytes;
            if (free < needed)
                return Finish("error", $"Not enough free disk space for a safe compaction: {Mb(free)} available, about {Mb(needed)} needed. No data was changed.");

            BeforeVacuumForTests?.Invoke();
            using var c = _db.OpenConnection();
            // FTS5 keeps segments and delete markers; merge them before VACUUM.
            SqliteDatabase.Exec(c, "INSERT INTO documents_fts(documents_fts) VALUES ('optimize');");
            SqliteDatabase.Exec(c, "VACUUM;");
            SqliteDatabase.Exec(c, "PRAGMA busy_timeout=500;");
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                using var r = cmd.ExecuteReader();
                r.Read();
                if (r.GetInt64(0) != 0) warnings.Add("WAL could not be truncated because a reader was active; the WAL will shrink on a later maintenance run.");
            }
            var integrity = SqliteDatabase.Scalar<string>(c, "PRAGMA integrity_check;");
            if (integrity != "ok")
                return Finish("error", "Integrity check after compaction reported a problem: " + integrity);
            try
            {
                SqliteDatabase.Exec(c, "INSERT INTO documents_fts(documents_fts, rank) VALUES ('integrity-check', 1);");
            }
            catch (SqliteException)
            {
                warnings.Add("Search index did not match the data and was rebuilt.");
                SqliteDatabase.Exec(c, "INSERT INTO documents_fts(documents_fts) VALUES ('rebuild');");
            }
            return Finish("ok", null);
        }
        catch (OperationCanceledException)
        {
            return Finish("error", "Compaction was cancelled. The database is unchanged or fully compacted (VACUUM is atomic).");
        }
        catch (Exception ex) when (ex is SqliteException or IOException)
        {
            return Finish("error", "Compaction failed: " + ex.Message + ". The database remains usable.");
        }
        finally
        {
            gate.Dispose(); // always resume writers
        }

        CompactResult Finish(string status, string? error)
        {
            var after = WorkingBytes();
            var now = DateTime.UtcNow;
            SetStatus(s => s with
            {
                State = status == "ok" ? (s.State == "pending" ? "pending" : "idle") : "error",
                LastRunAtUtc = now,
                LastResult = status == "ok" ? $"Compaction finished: {Mb(before)} → {Mb(after)}" : "Compaction failed",
                LastResultCode = status == "ok" ? "compact_ok" : "compact_failed",
                LastError = error,
                LastBytesBefore = before,
                LastBytesAfter = after,
            });
            if (status == "ok") SetStatus(s => s with { State = "idle" });
            return new CompactResult { Status = status, BytesBefore = before, BytesAfter = after, DurationMs = sw.ElapsedMilliseconds, Warnings = warnings, Error = error };
        }
    }

    // ---------------------------------------------------------------- backup

    /// <summary>Consistent backup via the SQLite backup API; kept only after it opens and passes integrity_check.</summary>
    public async Task<BackupResult> BackupAsync(CancellationToken ct)
    {
        var dir = _db.Options.BackupDirectory;
        Directory.CreateDirectory(dir);
        var now = DateTime.UtcNow;
        var name = $"knowledge-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}.db";
        for (var i = 2; File.Exists(Path.Combine(dir, name)); i++)
            name = $"knowledge-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{i}.db";
        var final = Path.Combine(dir, name);
        var temp = final + ".partial";

        var free = AvailableFreeSpace(dir);
        var main = new FileInfo(_db.Path).Length;
        if (free < main + VacuumSafetyMarginBytes)
            throw new KnowledgeException(ErrorCodes.Internal, $"Not enough free disk space for a backup ({Mb(free)} available).");

        try
        {
            using (await _db.EnterWriteAsync(TimeSpan.FromSeconds(30), ct))
            {
                using var src = _db.OpenConnection();
                using var dst = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
                dst.Open();
                src.BackupDatabase(dst);
                SqliteDatabase.Exec(dst, "PRAGMA journal_mode=DELETE;");
            }

            bool ok;
            using (var check = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = temp, Pooling = false, Mode = SqliteOpenMode.ReadOnly }.ToString()))
            {
                check.Open();
                ok = SqliteDatabase.Scalar<string>(check, "PRAGMA integrity_check;") == "ok"
                     && SqliteDatabase.Scalar<long>(check, "SELECT count(*) FROM schema_info;") == 1;
            }
            if (!ok)
                throw new KnowledgeException(ErrorCodes.Internal, "The backup copy failed its integrity check and was discarded.");
            File.Move(temp, final);
            return new BackupResult { FileName = name, Bytes = new FileInfo(final).Length, IntegrityOk = true, CreatedAtUtc = now };
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp); // only our own partial file
        }
    }

    internal static string Mb(long bytes) => (bytes / 1024d / 1024d).ToString("0.00", CultureInfo.InvariantCulture) + " MB";
}

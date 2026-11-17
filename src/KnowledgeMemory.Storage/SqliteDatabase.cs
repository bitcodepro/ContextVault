using KnowledgeMemory.Core;
using Microsoft.Data.Sqlite;

namespace KnowledgeMemory.Storage;

/// <summary>
/// Owns the SQLite file: connection settings, schema migrations and the single write gate.
/// Only the serve process creates this object; adapters talk to the REST API instead.
/// </summary>
public sealed class SqliteDatabase : IDisposable
{
    public const int SchemaVersion = 2;

    private readonly string _connectionString;
    private readonly int _busyTimeoutMs;

    /// <summary>Serializes all writers and maintenance (single local user, one writer at a time).</summary>
    public SemaphoreSlim WriteGate { get; } = new(1, 1);

    public string Path { get; }
    public AppOptions Options { get; }
    public string SqliteVersion { get; private set; } = "";

    private SqliteDatabase(AppOptions options)
    {
        Options = options;
        Path = options.DatabasePath;
        _busyTimeoutMs = options.Storage.BusyTimeoutSeconds * 1000;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = true,
            DefaultTimeout = options.Storage.BusyTimeoutSeconds,
            ForeignKeys = true,
        }.ToString();
    }

    /// <summary>Opens (or creates on first start) the database and runs migrations before any request is served.</summary>
    public static SqliteDatabase Open(AppOptions options)
    {
        Directory.CreateDirectory(options.ResolvedDataDirectory);
        var db = new SqliteDatabase(options);
        var isNew = !File.Exists(db.Path);
        if (isNew)
        {
            if (File.Exists(db.Path + "-wal"))
                throw new KnowledgeException(ErrorCodes.Internal,
                    $"Database file is missing but a WAL file exists at {db.Path}-wal. Refusing to create a new empty database; restore the main file from a backup.");
            try
            {
                db.CreateNew();
            }
            catch
            {
                SqliteConnection.ClearAllPools();
                if (File.Exists(db.Path)) File.Delete(db.Path); // only the file we just created
                throw;
            }
        }
        try
        {
            db.Migrate();
        }
        catch
        {
            // Release pooled handles so the file is not left locked; never delete or replace an existing file.
            db.Dispose();
            throw;
        }
        return db;
    }

    private void CreateNew()
    {
        var cs = new SqliteConnectionStringBuilder(_connectionString) { Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString();
        using var c = new SqliteConnection(cs);
        c.Open();
        // auto_vacuum must be set before the first table is created.
        Exec(c, "PRAGMA auto_vacuum=INCREMENTAL;");
        Exec(c, "PRAGMA journal_mode=WAL;");
        Exec(c, "CREATE TABLE schema_info(version INTEGER NOT NULL);");
        Exec(c, "INSERT INTO schema_info(version) VALUES (0);");
    }

    private void Migrate()
    {
        using var c = OpenConnection();
        SqliteVersion = Scalar<string>(c, "SELECT sqlite_version();");

        var hasSchema = Scalar<long>(c, "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='schema_info';") == 1;
        if (!hasSchema)
            throw new KnowledgeException(ErrorCodes.Internal,
                $"{Path} exists but is not a Knowledge Memory database (no schema_info table). Refusing to modify it.");

        var check = Scalar<string>(c, "PRAGMA quick_check;");
        if (check != "ok")
            throw new KnowledgeException(ErrorCodes.Internal,
                $"Database integrity check failed ({check}). The service will not start on a damaged database; restore a backup.");

        if (!HasFts5(c))
            throw new KnowledgeException(ErrorCodes.Internal, "The bundled SQLite library does not support FTS5.");

        Exec(c, "PRAGMA journal_mode=WAL;");
        var version = Scalar<long>(c, "SELECT version FROM schema_info;");
        if (version > SchemaVersion)
            throw new KnowledgeException(ErrorCodes.Internal,
                $"Database schema version {version} is newer than this program supports ({SchemaVersion}). Use a newer Knowledge Memory build.");

        if (version < 1)
        {
            using var tx = c.BeginTransaction();
            foreach (var sql in Schema.V1) Exec(c, sql, tx);
            Exec(c, "UPDATE schema_info SET version = 1;", tx);
            tx.Commit();
        }

        if (version < 2)
        {
            // V2: stem column for word-form search and card version history.
            using var tx = c.BeginTransaction();
            foreach (var sql in Schema.V2Before) Exec(c, sql, tx);
            var rows = new List<(long Row, string Stems)>();
            using (var read = c.CreateCommand())
            {
                read.Transaction = tx;
                read.CommandText = "SELECT doc_rowid, title, summary, tags_text, content FROM documents;";
                using var r = read.ExecuteReader();
                while (r.Read())
                    rows.Add((r.GetInt64(0), Stemmer.StemText(r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4))));
            }
            using (var upd = c.CreateCommand())
            {
                upd.Transaction = tx;
                upd.CommandText = "UPDATE documents SET stems=$s WHERE doc_rowid=$r;";
                var ps = upd.Parameters.Add("$s", SqliteType.Text);
                var pr = upd.Parameters.Add("$r", SqliteType.Integer);
                foreach (var (row, stems) in rows)
                {
                    ps.Value = stems;
                    pr.Value = row;
                    upd.ExecuteNonQuery();
                }
            }
            foreach (var sql in Schema.V2After) Exec(c, sql, tx);
            Exec(c, "UPDATE schema_info SET version = 2;", tx);
            tx.Commit();
        }
    }

    public static bool HasFts5(SqliteConnection c)
    {
        try
        {
            Exec(c, "CREATE VIRTUAL TABLE temp.fts5_probe USING fts5(x); DROP TABLE temp.fts5_probe;");
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    public SqliteConnection OpenConnection()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        Exec(c, $"PRAGMA busy_timeout={_busyTimeoutMs}; PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON;");
        return c;
    }

    /// <summary>Waits for the write gate; reports busy instead of hanging forever.</summary>
    public async Task<IDisposable> EnterWriteAsync(TimeSpan timeout, CancellationToken ct)
    {
        if (!await WriteGate.WaitAsync(timeout, ct))
            throw new KnowledgeException(ErrorCodes.Busy, "The knowledge base is busy (maintenance in progress). Retry in a few seconds.");
        return new Releaser(WriteGate);
    }

    public TimeSpan WriteTimeout => TimeSpan.FromSeconds(Math.Max(10, Options.Storage.BusyTimeoutSeconds * 4));

    public static void Exec(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        cmd.ExecuteNonQuery();
    }

    public static T Scalar<T>(SqliteConnection c, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        return (T)Convert.ChangeType(cmd.ExecuteScalar()!, typeof(T));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        WriteGate.Dispose();
    }

    private sealed class Releaser(SemaphoreSlim gate) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0) gate.Release();
        }
    }
}

internal static class Schema
{
    public static readonly string[] V1 =
    [
        """
        CREATE TABLE documents(
            doc_rowid INTEGER PRIMARY KEY,
            id TEXT NOT NULL UNIQUE,
            project TEXT NOT NULL,
            project_display TEXT NOT NULL,
            external_key TEXT NULL,
            external_key_norm TEXT NULL,
            title TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('task','decision','reference','preference')),
            summary TEXT NOT NULL,
            content TEXT NOT NULL,
            tags_json TEXT NOT NULL,
            tags_text TEXT NOT NULL,
            status TEXT NOT NULL CHECK (status IN ('active','completed','superseded')),
            superseded_by TEXT NULL REFERENCES documents(id) ON DELETE SET NULL,
            sources_json TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            version INTEGER NOT NULL CHECK (version >= 1),
            UNIQUE (project, external_key_norm)
        );
        """,
        "CREATE INDEX ix_documents_project_updated ON documents(project, updated_at DESC);",
        "CREATE INDEX ix_documents_key ON documents(external_key_norm);",
        "CREATE INDEX ix_documents_updated ON documents(updated_at DESC);",
        """
        CREATE VIRTUAL TABLE documents_fts USING fts5(
            title, summary, tags_text, content,
            content='documents', content_rowid='doc_rowid',
            tokenize='unicode61 remove_diacritics 2'
        );
        """,
        """
        CREATE TRIGGER documents_ai AFTER INSERT ON documents BEGIN
            INSERT INTO documents_fts(rowid, title, summary, tags_text, content)
            VALUES (new.doc_rowid, new.title, new.summary, new.tags_text, new.content);
        END;
        """,
        """
        CREATE TRIGGER documents_ad AFTER DELETE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, title, summary, tags_text, content)
            VALUES ('delete', old.doc_rowid, old.title, old.summary, old.tags_text, old.content);
        END;
        """,
        """
        CREATE TRIGGER documents_au AFTER UPDATE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, title, summary, tags_text, content)
            VALUES ('delete', old.doc_rowid, old.title, old.summary, old.tags_text, old.content);
            INSERT INTO documents_fts(rowid, title, summary, tags_text, content)
            VALUES (new.doc_rowid, new.title, new.summary, new.tags_text, new.content);
        END;
        """,
        // Idempotency answers: ids, versions and a hash only - never content.
        """
        CREATE TABLE request_log(
            request_id TEXT PRIMARY KEY,
            payload_hash TEXT NOT NULL,
            document_id TEXT NOT NULL,
            version INTEGER NOT NULL,
            operation TEXT NOT NULL,
            saved_at TEXT NOT NULL,
            warnings_json TEXT NOT NULL
        );
        """,
        "CREATE INDEX ix_request_log_saved ON request_log(saved_at);",
        // Minimal technical audit: time, operation, id. No content, bounded retention.
        "CREATE TABLE audit_log(id INTEGER PRIMARY KEY, at TEXT NOT NULL, operation TEXT NOT NULL, document_id TEXT NOT NULL);",
        "CREATE INDEX ix_audit_log_at ON audit_log(at);",
        "CREATE TABLE settings(key TEXT PRIMARY KEY, value TEXT NOT NULL);",
    ];

    public static readonly string[] V2Before =
    [
        "DROP TRIGGER documents_ai;",
        "DROP TRIGGER documents_ad;",
        "DROP TRIGGER documents_au;",
        "DROP TABLE documents_fts;",
        "ALTER TABLE documents ADD COLUMN stems TEXT NOT NULL DEFAULT '';",
        // Previous versions of a card (the current one stays in documents). Removed together with the card.
        """
        CREATE TABLE document_versions(
            document_id TEXT NOT NULL,
            version INTEGER NOT NULL,
            saved_at TEXT NOT NULL,
            replaced_at TEXT NOT NULL,
            project_display TEXT NOT NULL,
            external_key TEXT NULL,
            title TEXT NOT NULL,
            kind TEXT NOT NULL,
            summary TEXT NOT NULL,
            content TEXT NOT NULL,
            tags_json TEXT NOT NULL,
            status TEXT NOT NULL,
            superseded_by TEXT NULL,
            sources_json TEXT NOT NULL,
            PRIMARY KEY (document_id, version)
        );
        """,
    ];

    public static readonly string[] V2After =
    [
        """
        CREATE VIRTUAL TABLE documents_fts USING fts5(
            title, summary, tags_text, content, stems,
            content='documents', content_rowid='doc_rowid',
            tokenize='unicode61 remove_diacritics 2'
        );
        """,
        // External content: FTS reads every column by the same name from documents (including stems).
        "INSERT INTO documents_fts(documents_fts) VALUES ('rebuild');",
        """
        CREATE TRIGGER documents_ai AFTER INSERT ON documents BEGIN
            INSERT INTO documents_fts(rowid, title, summary, tags_text, content, stems)
            VALUES (new.doc_rowid, new.title, new.summary, new.tags_text, new.content, new.stems);
        END;
        """,
        """
        CREATE TRIGGER documents_ad AFTER DELETE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, title, summary, tags_text, content, stems)
            VALUES ('delete', old.doc_rowid, old.title, old.summary, old.tags_text, old.content, old.stems);
        END;
        """,
        """
        CREATE TRIGGER documents_au AFTER UPDATE ON documents BEGIN
            INSERT INTO documents_fts(documents_fts, rowid, title, summary, tags_text, content, stems)
            VALUES ('delete', old.doc_rowid, old.title, old.summary, old.tags_text, old.content, old.stems);
            INSERT INTO documents_fts(rowid, title, summary, tags_text, content, stems)
            VALUES (new.doc_rowid, new.title, new.summary, new.tags_text, new.content, new.stems);
        END;
        """,
    ];
}

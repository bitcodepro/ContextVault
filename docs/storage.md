# Storage, search and disk management

## SQLite

- Package `Microsoft.Data.Sqlite` 10.0.15 with `SQLitePCLRaw.bundle_e_sqlite3` (includes FTS5). FTS5 availability
  is checked at every start; without it the service does not start.
- New database: `PRAGMA auto_vacuum=INCREMENTAL` is set **before** tables are created, then `journal_mode=WAL`.
  On every connection: `synchronous=FULL`, `foreign_keys=ON`, `busy_timeout`.
- Migrations are versioned in the `schema_info` table and run before requests are accepted. A database newer than
  the program, a foreign file or a failed `quick_check` stops startup without creating a new database.
- All writes go through one write gate (semaphore) and `BEGIN IMMEDIATE` transactions; waiting is bounded,
  then a `busy` error is returned — requests never hang forever.

## Schema

`documents` — a card (UUID `id`, `project`, `project_display`, `external_key`, `external_key_norm`, `title`,
`kind`, `summary`, `content`, tags as JSON plus search text, `status`, `superseded_by`, `sources_json`,
UTC created/updated times, `version`). `(project, external_key_norm)` is unique; cards without a key
store `NULL` and do not conflict.

Normalization:
- `project`: trimmed, lower-cased (invariant), spaces → `-`. The original spelling is kept in `project_display`.
- `externalKey`: kept as is (leading zeros and prefixes preserved), a leading `#` is dropped; compared upper-cased
  (`et-6449` = `ET-6449`, but `0245` ≠ `245`).
- Tags: lower-cased, spaces → `-`, no duplicates.

`documents_fts` — an FTS5 external-content table (`title`, `summary`, `tags_text`, `content`,
`tokenize='unicode61 remove_diacritics 2'`), kept in sync by insert/update/delete triggers in the same transaction.
Check — `integrity-check`; repair — `rebuild` from the main table (`KnowledgeStore.RebuildIndexAsync`).
Compaction checks the index and rebuilds it if it is out of sync.

`documents.stems` — space-separated word stems of title, summary, tags and content (computed in C# on save, column
`stems` of the FTS table). See "Word forms" below.

`document_versions` — previous versions of a card (`document_id`, `version`, `saved_at`, `replaced_at`, title, kind,
summary, content, tags, status, superseded_by, sources). On every real update the current row is copied here in
the same transaction; only the newest `Knowledge.MaxHistoryVersions` (20) are kept. An `unchanged` save adds nothing.
Restore (web UI only) copies the current row to history and writes the old fields as a new version; project and key stay as they are.
Deleting a card deletes its history in the same transaction.

Schema version 2 adds `stems` and `document_versions`. A version 1 database is upgraded at startup in one transaction:
the FTS table is recreated with the new column, stems are computed for existing cards and the index is rebuilt.

`request_log` (idempotency), `audit_log` (time/operation/ID), `settings`.

## Search

Order:
1. Exact key match: query tokens containing a digit (`245`, `#245`, `ET-6449`) are compared with
   `external_key_norm`. Matches from all projects are returned (`matchType: exactKey`).
2. FTS5 over all words (AND), the last word of ≥ 3 characters as a prefix. Each word matches either as typed or by its
   stem in the `stems` column. `bm25` ranking with weights title 10, summary 5, tags 3, content 1, stems 1; ties broken by `id` (stable order). `matchType: fullText`.
3. If nothing is found and there are several words, a relaxed any-word search (`matchType: partialText`).
4. An empty result comes with a note that it does not prove the topic was never discussed.

The `project`, `externalKey`, `kind`, `status`, `sinceDays` filters are applied in SQL. `knowledge_recent` is a filter-only
search (`sinceDays`, default 7) ordered by `updated_at` descending. An empty query without filters is allowed
only in the UI (the "All knowledge" list); the MCP tool requires a query or a filter.

Response limits: at most `MaxLimit` cards, `summary`/`snippet` truncated to `SnippetCharacters`
(with `…`), total response text ≤ `MaxResponseCharacters`; continuation via `nextCursor`. The full `content`
is not part of search results.

**Word forms**: `Stemmer` (KnowledgeMemory.Core) reduces Russian words with the Snowball algorithm, Ukrainian words
(detected by і/ї/є/ґ) by stripping common endings, and English words by removing plural/-ed/-ing. Words with digits and
words shorter than 4 letters are kept as they are. It is a heuristic: some forms still differ (Ukrainian `-ість`/`-ості`,
Russian verb aspects) and occasionally unrelated words share a stem.

**Known limitations**: no typo correction, no synonyms, no understanding of paraphrases.
`remove_diacritics` folds letters with diacritics into their base letters.
Embeddings/vector search are postponed until real misses are measured.

## Performance measurement

The `PerformanceTests` test (run: `$env:KM_PERF=1; dotnet test --filter PerformanceTests`), 10,000 cards
of 2–10 KB, 200 queries (one word, two words, key, word + project). Measured 2026-11-17, Intel Core i7-10750H,
32 GB RAM, Windows 11, Debug build:

| Metric | Value |
|---|---|
| Inserting 10,000 cards | 50.9 s (each a separate transaction with `synchronous=FULL`) |
| Database size | 132 MB |
| Cold search (new connections, first 20) | p50 26.9 ms, p95 55.8 ms |
| Warm search (200) | p50 29.0 ms, p95 95.7 ms |

This is a measurement on one machine, not a guarantee.

## Disk management

### What the Disk manager shows

Card count; sizes of `knowledge.db`, `-wal`, `-shm`; backups and exports; total size of the application's files;
the reclaimable estimate `freelist_count × page_size` (an estimate, not a promise); maintenance state
(`idle`, `pending`, `running`, `busy`, `error`), time and result of the last run, working file size
(main+WAL+SHM) before and after. Statistics count only files inside `DataDirectory`.

### After a delete (automatic)

1. Deleting a card and its FTS entries is one transaction; `expectedVersion` is checked.
2. After commit, maintenance is marked `pending`; deletes within `MaintenanceDelaySeconds` are merged.
3. A background run within `MaintenanceBudgetSeconds`: FTS segment merge (`merge`, frees
   pages holding deleted index entries), then `incremental_vacuum(N)` in batches while free pages remain
   or until the budget runs out (the rest goes to the next run, state `pending`).
4. `wal_checkpoint(TRUNCATE)`. If an active reader blocks it, the state is `busy` and it retries with exponential
   backoff (2 s … 5 min); the UI is not blocked.
5. Sizes are recalculated and shown as "before → after".

Measured in a test: 1,500 cards (≈22 MB) → delete 1,200 → after background maintenance the working files are < 60 %
of the original size, WAL = 0, free pages 0, `integrity_check = ok`.

### "Reclaim space" (manual)

One serialized operation: takes the write gate (new changes wait at most 30 s, otherwise `busy`),
checks free disk space (needs ≥ 2 × database size + 64 MB, otherwise a clear error with no changes),
`FTS optimize`, `VACUUM`, `wal_checkpoint(TRUNCATE)`, `integrity_check` and an FTS check, size recalculation.
The gate is released in `finally` even on error. Response: `status`, `bytesBefore`, `bytesAfter`, duration, warnings.
The `main/wal/shm` files are never deleted or copied by hand.

Measured in a test: 800 cards → delete all → compaction: result < 10 % of the original size.

### Honest guarantees

- Deleting one short entry does not necessarily shrink the file right away (page-based storage).
- Changing the status/archiving does not delete data or shrink the file.
- Maintenance can be deferred by readers, running operations or lack of disk space — this shows in the state.
- This is not secure erasure: data may remain on SSDs, in OS snapshots and in backups.

## Backups and export

- "Create backup": SQLite backup API into `backups\knowledge-<UTC time>.db.partial`, then the copy is opened,
  `integrity_check` and a schema check run; only then is the file renamed to `.db`. A failed copy is deleted.
- There are no automatic backups; the program never deletes old backups, including when cards are deleted.
- JSON export (`knowledge-memory-export`, `formatVersion: 1`) and Markdown export: current cards with sources,
  without tokens, sessions or settings. Downloaded by the browser.
- Import is not implemented.
- Restoring from a backup: stop `serve`, move the current `knowledge.db*` aside, copy
  the chosen `backups\knowledge-….db` as `knowledge.db`, start `serve`.

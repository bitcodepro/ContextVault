# Configuration

The file is passed explicitly: `KnowledgeMemory.exe <command> --config "C:\Path\appsettings.json"`.
An example, `appsettings.example.json`, ships next to the program. Changes take effect after restarting the service;
they cannot be changed through the web UI or the API. The tray Settings window changes the port, `StartWithWindows` and `AutoStart`
in this file and keeps everything else (comments in the file are not preserved).

| Setting | Default | Meaning |
|---|---|---|
| `Server.Host` | `127.0.0.1` | Listen address. Only `127.0.0.1`, `localhost`, `::1` are allowed. `0.0.0.0` and external IPs are rejected at startup. |
| `Server.Port` | `47831` | Port 1024–65535. If the port is busy, `serve` says whether another Knowledge Memory instance or an unrelated program holds it. |
| `Server.OpenBrowserOnStart` | `false` | Open the UI (with a one-time link) after startup. |
| `Server.AutoStart` | `true` | `mcp-stdio` starts the tray app (which runs the service) in the background when the service does not answer, waits up to 20 s and retries the call once. It keeps running after the adapter exits. Before that, the adapter re-reads the config, so a port changed in the tray settings is picked up without restarting the AI client. |
| `Server.StartWithWindows` | `false` | Start the tray app when the user signs in to Windows. Applied when the tray starts, when Settings are saved and by `autostart on|off` (entry `KnowledgeMemory` in HKCU\...\CurrentVersion\Run). |
| `Storage.DataDirectory` | `data` (next to the program) | Folder for the database, token, backups and exports. `%VAR%` is expanded explicitly; a relative path is resolved against the config file's folder. UNC paths (`\\server\share`) are not allowed. |
| `Storage.BusyTimeoutSeconds` | `5` | How long to wait for a busy database (SQLite busy timeout). A write waits at most `max(10, 4×value)` seconds, then returns `busy`. |
| `Storage.AutoReclaimAfterDelete` | `true` | Run background space reclaim after deletes. |
| `Storage.MaintenanceDelaySeconds` | `5` | Delay before background maintenance: deletes close together are merged into one run. |
| `Storage.IncrementalVacuumPagesPerBatch` | `256` | Pages returned per `incremental_vacuum` batch. |
| `Storage.MaintenanceBudgetSeconds` | `2` | Time budget of one background run (half of it for FTS segment merging). The rest continues in the next run. |
| `Storage.AuditRetentionDays` | `30` | Retention of the technical audit (time, operation, ID — no content). |
| `Storage.RequestIdRetentionDays` | `7` | How many days the response for a repeated `requestId` is kept (ID, version and hash — no content). |
| `Search.DefaultLimit` / `Search.MaxLimit` | `8` / `20` | Default and maximum number of search results. |
| `Search.SnippetCharacters` | `320` | Length of `summary`/`snippet` in search results. |
| `Search.MaxResponseCharacters` | `12000` | Limit on the total text in one search response (not just the card count). |
| `Knowledge.MaxContentCharacters` | `100000` | Maximum length of a card's `content`. |
| `Knowledge.MaxTags` / `Knowledge.MaxSources` | `20` / `20` | Tag and source limits. |
| `Knowledge.MaxHistoryVersions` | `20` | Previous versions kept per card; older ones are removed on the next update. `0` turns history off. |
| `Knowledge.MaxGetCharacters` | `30000` | Read window of `knowledge_get`; longer content is read in parts via `offset`/`nextOffset`, with an explicit `truncated`. |

## Data location constraints

- The data folder must be on a local disk. SQLite WAL is not supported on network folders.
- Do not put it in a cloud-sync folder (OneDrive, Dropbox, Google Drive): sync may copy
  `knowledge.db` without a matching `-wal` file and corrupt the copy.
- Updating the program does not touch the data folder: it lives outside the install directory.
- If the database file is corrupt or is not a Knowledge Memory database, `serve` does not start and does **not**
  create an empty database in its place. Restore the file from a backup (`backups\`).

## Files in the data folder

| File | Purpose |
|---|---|
| `knowledge.db`, `knowledge.db-wal`, `knowledge.db-shm` | SQLite database (WAL mode). Do not delete or copy them by hand while the service runs. |
| `secrets\api-token` | Local API token, readable only by the current Windows user (ACL). |
| `instance.lock` | Single-instance lock of `serve` for this folder. |
| `backups\knowledge-*.db` | Backups created with the UI button. Never deleted automatically. |
| `exports\` | Reserved for exports; exports from the UI are downloaded by the browser. |

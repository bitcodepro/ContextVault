# ContextVault

A local knowledge base for AI clients over MCP. On your command, the AI saves compact cards for tasks
and decisions (goal, decisions and reasons, constraints, rejected options, result, open questions),
and in another chat finds them by project, task number or text. In the browser you see all cards,
search, read, restore earlier versions, delete them and keep track of disk space.

- Windows x64, no WSL or Docker. A single `serve` process owns the SQLite (FTS5) database.
- MCP: HTTP endpoint `/mcp` and the `mcp-stdio` adapter for stdio clients. The adapter starts the service by itself when it is not running.
- Tray icon: open the web UI in one click, change the port, turn "start with Windows" on or off.
- Tools: `knowledge_search`, `knowledge_get`, `knowledge_save`, `knowledge_recent`, `knowledge_projects`.
  There is no MCP delete — only in the UI.
- Web UI at `http://127.0.0.1:<port>`, loopback only, sign-in with a one-time link.

> **Privacy note.** The database is stored locally, but everything an AI client reads from it is sent
> to the model provider together with the chat (if the client is cloud-based). Do not save passwords, tokens or keys.

## Installation

1. Unzip `contextvault-win-x64.zip` into `C:\Tools\KnowledgeMemory`.
   The build is self-contained: you do not need to install .NET.
   (Another folder works too — then replace `C:\Tools\KnowledgeMemory` in every block below.)
2. Double-click `KnowledgeMemory.exe`: the blue **K** icon appears in the tray. On first start the program creates
   `appsettings.json` (port `47831`) and the `data` folder next to itself. All settings are in [docs/configuration.md](docs/configuration.md).
3. Connect your AI client with one of the ready blocks below. That is all: the first tool call starts the service
   with its tray icon. Optional: `KnowledgeMemory.exe autostart on` (or the checkbox in the tray Settings) to start it with Windows.

## Updating

Your memory lives only in `data\` and your settings in `appsettings.json`. The release zip never contains either,
so an update cannot overwrite them.

Every connected AI client keeps its own `KnowledgeMemory.exe mcp-stdio` process running. Those processes lock the
program files and start the tray again on the next tool call, so stop all of them first:

1. Close the AI clients that use Knowledge Memory (Claude Code, Codex, Claude Desktop, Cursor), or turn the
   `knowledge-memory` server off in each of them. Stop a `serve` window if you started one.
2. Right-click the tray icon → **Exit**.
3. Check that nothing is left. This PowerShell command must print nothing:
   `Get-Process KnowledgeMemory -ErrorAction SilentlyContinue`
   If it prints a process, close the client that owns it, or end it with
   `Get-Process KnowledgeMemory | Stop-Process`.
4. Unzip the new `contextvault-win-x64.zip` into the same folder and allow it to replace the files.
5. Double-click `KnowledgeMemory.exe`, then start or reconnect your AI clients. If the new version has a newer
   database format, it upgrades `knowledge.db` on start. The MCP blocks in your AI clients keep working because
   the path does not change.

Do not delete the program folder when updating: that would delete `data\` with it.

Coming from 1.2.0 or older: those versions kept the memory in `%LOCALAPPDATA%\KnowledgeMemory`. If your
`appsettings.json` does not set `Storage.DataDirectory`, the new version keeps using that folder as long as it has
a database and `data\` next to the program does not. To move it, copy the folder's contents into `data\` while
nothing is running.

## Connecting AI clients

Every block below is complete: copy it, paste it, restart the client. **Easiest:** right-click the tray icon →
**Copy MCP settings** → pick your client; the block is copied with the real paths of your installation.
The program finds `appsettings.json` next to itself, so no `--config` is needed (add `--config "<path>"` only for a config elsewhere).

The blocks also allow the
Knowledge Memory tools without a confirmation prompt on every call — only these five tools, nothing else.
All clients share one database. The token is never written into a client config: the adapter reads it
from the protected file in the data folder.

### Claude Code

1. Register the server for all your projects (run once in any terminal):

```bash
claude mcp add -s user knowledge-memory -- "C:\Tools\KnowledgeMemory\KnowledgeMemory.exe" mcp-stdio
```

2. Allow its tools without prompts: add this to `%USERPROFILE%\.claude\settings.json`
   (if the file already has `"permissions"` → `"allow"`, add just the `"mcp__knowledge-memory"` line to that list):

```json
{
  "permissions": {
    "allow": [
      "mcp__knowledge-memory"
    ]
  }
}
```

Check: `claude mcp get knowledge-memory` shows `✔ Connected`.

### Codex (CLI and the Codex app)

Add this to `%USERPROFILE%\.codex\config.toml`:

```toml
[mcp_servers.knowledge-memory]
command = "C:\\Tools\\KnowledgeMemory\\KnowledgeMemory.exe"
args = ["mcp-stdio"]
default_tools_approval_mode = "approve"
```

`default_tools_approval_mode = "approve"` lets Codex call the tools without asking; without it,
non-interactive `codex exec` cannot call them at all.

### Claude Desktop

Edit `%APPDATA%\Claude\claude_desktop_config.json` (Settings → Developer → Edit Config). If the file already has
`"mcpServers"`, add only the `"knowledge-memory"` entry to it:

```json
{
  "mcpServers": {
    "knowledge-memory": {
      "command": "C:\\Tools\\KnowledgeMemory\\KnowledgeMemory.exe",
      "args": ["mcp-stdio"]
    }
  }
}
```

Claude Desktop has no config key for automatic approval: on the first call of each tool choose
**Always allow**.

### Cursor

Create or edit `%USERPROFILE%\.cursor\mcp.json` (all projects) or `.cursor\mcp.json` in a project:

```json
{
  "mcpServers": {
    "knowledge-memory": {
      "command": "C:\\Tools\\KnowledgeMemory\\KnowledgeMemory.exe",
      "args": ["mcp-stdio"]
    }
  }
}
```

Approval of tool calls is set in Cursor's own settings (auto-run), not in this file.

### HTTP MCP (other clients)

Streamable HTTP, stateless: `http://127.0.0.1:47831/mcp` with the header
`Authorization: Bearer <contents of data\secrets\api-token next to the program>`.
Use it only with clients that store headers locally; stdio is preferred. Over HTTP `serve` must already be running.

### Instructions for the AI

Tray → **Copy MCP settings → Instructions for the AI** copies one ready prompt: a request to add the server
and keep the rules (including "save context and progress whenever we switch tasks"), the full rules below, and
the Codex server block with the real path to `KnowledgeMemory.exe`. Paste it into the AI chat, and the AI sets
itself up. Or add the block from [docs/agent-instructions.md](docs/agent-instructions.md) to the client's instructions
(`CLAUDE.md`, `AGENTS.md`, custom instructions): when to search, how to select what matters, how to update a
card without losing data.

**Client verification status:** the protocol is covered by tests with the official MCP C# SDK client (stdio and
HTTP, two clients at once). Tested by hand on 2026-11-17: **Claude Code** 2.1.325 (Haiku) and **Codex CLI** 0.167 —
save, search from a new session, update with a version check. **Claude Desktop and Cursor have not been tested** —
those blocks follow their documentation.

## Tray icon, settings and start with Windows

Knowledge Memory runs as a tray app: a blue **K** icon in the Windows notification area (grey when the service is not running).

- **Left click** or **Open in browser** — opens the web UI already signed in (no typing of `localhost` and a port).
- **Settings…** — the port (with a free/busy check and a "Find a free port" button), **Start Knowledge Memory when I
  sign in to Windows**, and **Start the service automatically when an AI client needs it**. Saving writes
  `appsettings.json` (other settings are kept) and restarts the service if the port changed. Connected AI clients
  pick up the new port on their next call — their config does not change.
- **Copy MCP settings** — copies a ready block with the real paths of this installation: Claude Code command,
  Claude Code permissions, Codex, Claude Desktop / Cursor, or the instructions for the AI. A notification says where to paste it.
- **Open data folder**, **Restart service**, **Exit** (stops the service the tray started).

You do not have to start anything by hand: the first AI tool call starts the tray (and the service) in the background,
and it keeps running after the AI client closes. To start it yourself, or to have it start with Windows:

```bash
C:\Tools\KnowledgeMemory\KnowledgeMemory.exe tray
```

```bash
C:\Tools\KnowledgeMemory\KnowledgeMemory.exe autostart on
```

`autostart on|off` does the same as the checkbox in Settings: it sets `Server.StartWithWindows` in the config and adds
(or removes) the entry in *HKCU\Software\Microsoft\Windows\CurrentVersion\Run* — current user only, no administrator
rights. Without `--config`, all commands use `appsettings.json` next to the program (created from the
example if it is missing).

Other commands (without `--config` they use `appsettings.json` next to the program):

| Command | What it does |
|---|---|
| `open-ui` | Opens the web UI with a one-time sign-in link (valid 2 minutes); `--print-url` prints the link instead. |
| `check` | Validates the config and checks that the service answers. |
| `serve` | Runs the service in a console window without the tray (for troubleshooting). |

A second service for the same data folder does not start; a second tray for the same data folder exits quietly.

## Usage

Save knowledge (in a chat with a connected client):

```text
Task 245 in project Revqora is done.
Collect the important knowledge from the discussion available to you and save a task card:
goal, decisions made and why, constraints, what was verified and what is left.
If the card already exists, update it carefully and keep the important earlier information.
```

The AI looks for an existing card, creates or updates it (with a version check) and reports success only
after the server responds: ID, version, operation `created`/`updated`/`unchanged`, save time.

Find it in another chat: "Do you remember task 245 about the import?" — the AI calls search (project + exact key first),
gets short results and reads the full card with a separate call. If the number exists in several
projects, the AI should ask which project. "What did we save this week?" — the AI calls `knowledge_recent`.

MCP provides tools, not chat monitoring: the server does not see the conversation history and saves nothing on its own.
Saving happens when the AI calls `knowledge_save`: when you ask, and, with the recommended
[instructions](docs/agent-instructions.md), on the AI's own judgment as soon as something worth keeping appears
(a decision, a verified result, a root cause). Each such save is reported in one line, and you can tell the AI to stop.
What already left the model's context before it was saved cannot be recovered, so name the project and task at the start.

## Web UI

- Left: "All knowledge", projects with card counts, "Disk manager".
- Top: search (task number, a key like `ET-6449`, or words), type and status filters. The query and filters
  are kept in the page address — Back/Forward work.
- A card opens on the right: full text (Markdown is shown as text), tags, sources, metadata.
- **History**: every update keeps the previous version (up to 20 per card). "View" shows an old version with the
  changes marked (− removed later, + added later); "Restore version N" makes it current again as a new version —
  the replaced text stays in the history, so a restore never loses anything.
- **Deleting**: the "Delete" button in a list row and in the open card. The dialog shows the project, key and
  title; there is no trash, the card's history is deleted with it, and existing backups are not changed. If another
  client changed the card after it was opened, the delete is not performed and the new version is shown.
  After a delete, space reclaim starts automatically.
- After the service restarts, open the UI again from the tray (browser sessions are kept in the service's memory).

## Disk space, backup and export

The "Disk manager" shows the sizes of the main file, WAL and SHM, backups, the total size of the application's files,
the reclaimable estimate and the maintenance state (idle, scheduled, running,
deferred: database busy, error).

- After deletes, space is returned in the background in batches (search segment merge, `incremental_vacuum`,
  WAL truncation). One short entry may not shrink the file — that is normal.
- "Reclaim space" — full compaction (`VACUUM`) with an integrity check; shows the size before/after.
  It needs free disk space of about twice the database size; if there is not enough, it fails without changing data.
- "Create backup" — a verified copy in `DataDirectory\backups`. The program never deletes backups.
- "Export JSON/Markdown" — all current cards with sources, without history, tokens or settings.
- Compaction is not secure erasure (SSDs, OS snapshots, backups).

Details: [docs/storage.md](docs/storage.md). Security: [docs/security.md](docs/security.md).

## Limitations

- Single user, this computer only (loopback). No sync, remote access or phone.
- Search is full-text with light word-form matching for Russian, Ukrainian and English (`клавиатурой` finds
  `клавиатуры`, `decisions` finds `decision`). It is a heuristic stemmer: no typo correction, no synonyms, no
  understanding of paraphrases. The last word is also matched as a prefix (`deploy` finds `deployment`).
- The project filter organizes cards; it is not access control: any connected client reads all projects.
- History keeps the last 20 versions per card (`Knowledge.MaxHistoryVersions`). No import, no bulk delete.
- The data folder must not be in OneDrive/Dropbox or on a network drive.
- Secret detection is only a warning for common formats, not a guarantee.

## Troubleshooting

| Symptom | What to do |
|---|---|
| `Configuration error: …` | The message names the setting and the allowed values. Non-loopback addresses and ports outside 1024–65535 are rejected. |
| `Port … is used by another program` | Change `Server.Port` (the adapters use the same config) and restart. |
| `already running for data directory` | `serve` is already running with this folder; use it (`open-ui`). |
| The AI says "service is not running" / "did not answer within 20 s" | Look at the tray icon (grey = not running, hover for the reason) or run `serve` by hand to see the error. With `"AutoStart": false` start `tray` yourself. |
| Tray says the port is used by another program | Settings… → Find a free port → Save. |
| The client asks for permission on every call | Add the permission part of your client's block above. |
| "No session" / "The sign-in link is invalid" | Run `open-ui` again (the link is one-time and lives 2 minutes). |
| "Cannot reach the … service" in the UI | `serve` is stopped; start it and reload the page. |
| Maintenance "Deferred: database busy" | A read is in progress; it retries automatically. |
| `serve` does not start: integrity / not a Knowledge Memory database | The file is corrupt or foreign — the program does not touch it. Restore from `backups` (docs/storage.md). |

## Building from source

Requires .NET SDK 10.0.401+ (pinned in `global.json`).

```bash
dotnet restore
```

```bash
dotnet build -c Release
```

```bash
dotnet test -c Release
```

Performance measurement on 10,000 cards (optional):

```bash
powershell -NoProfile -Command "$env:KM_PERF='1'; dotnet test --filter PerformanceTests --logger 'console;verbosity=detailed'"
```

Self-contained win-x64 publish and ZIP (runs the tests, checks that the package has no databases or tokens):

```bash
powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
```

Output: `artifacts\KnowledgeMemory-win-x64\` and `artifacts\KnowledgeMemory-win-x64.zip`.

### Solution layout

```text
src/KnowledgeMemory.Core      models, validation and normalization, configuration, search query parsing
src/KnowledgeMemory.Storage   SQLite, migrations, FTS5, save/search/delete, disk maintenance, backup
src/KnowledgeMemory.Host      CLI, ASP.NET host, security, REST API, MCP (HTTP and stdio), web UI (wwwroot)
tests/KnowledgeMemory.Tests   storage and search, search quality (RU/UK/EN), stemming, history, migration, disk, security, MCP, performance
docs/                         AI instructions, configuration, security, storage
```

Dependencies: .NET 10 (SDK 10.0.401), ModelContextProtocol + ModelContextProtocol.AspNetCore 2.4.0,
Microsoft.Data.Sqlite 10.0.15 (SQLitePCLRaw bundle_e_sqlite3, FTS5), xUnit 2.9.3.

## GitHub CI/CD

Pull requests and main are built and tested only on Windows. Successful main builds publish a self-contained Windows x64 ZIP with SHA-256 checksums. Release versions are 1.2.N, where N is the main first-parent commit count minus one. Runtime data, tokens, local configuration and build output are excluded from Git. The executable and existing data-directory names remain KnowledgeMemory for compatibility.

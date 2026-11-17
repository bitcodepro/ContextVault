# Security

## Threat model

Protects against: accidental network access, a malicious web page in the user's browser,
DNS rebinding, CSRF, XSS through card content, project mix-ups, prompt injection through saved text.

**Does not protect** against malware already running under the same Windows account: it can
read the token file and the database just like the user can.

## Network

- Only loopback is listened on (`127.0.0.1` by default). A non-loopback address in the config is a startup error.
- The `Host` header must be exactly `127.0.0.1:<port>`, `localhost:<port>` or `[::1]:<port>`, otherwise 400
  (DNS rebinding protection), even with a valid token.
- A sent `Origin` must match the service's own origin; `Sec-Fetch-Site: cross-site/same-site` is rejected.
  CORS is not enabled.

## Authentication

Two separate kinds of access:

1. **Machine** (stdio adapter, MCP HTTP, the `open-ui`/`check` commands): `Authorization: Bearer <token>`.
   The token is 32 random bytes in `DataDirectory\secrets\api-token`, with a "current user only" ACL.
   The token never appears in command-line arguments, URLs, HTML/JS, localStorage, logs or config examples.
   It is compared in constant time.
2. **Browser**: `open-ui` asks the service for a one-time code (valid for 2 minutes) and opens
   `http://127.0.0.1:<port>/#bootstrap=<code>`. The code is in the URL fragment, so the browser does not send it
   to the server when loading the page and it never reaches logs. JS removes the fragment from the address bar at once
   (`history.replaceState`) and exchanges the code (`POST /api/auth/exchange`, Origin checked) for the cookie
   `km_session_<port>`: `HttpOnly`, `SameSite=Strict`, `Path=/`, 12 h lifetime with sliding renewal.
   The `Secure` flag is not set: this is plain HTTP on loopback without TLS, and with `Secure` the browser would not keep the cookie.
   Sessions live in memory: after restarting `serve`, run `open-ui` again.

Only machine access can save cards (`POST /api/documents`), issue sign-in codes and call `/mcp`.
The browser can read, delete, restore an earlier version (`POST /api/documents/{id}/versions/{n}/restore`, with the same
CSRF, Origin and `expectedVersion` checks as delete), run disk maintenance, create backups and export.
There is no MCP tool for deleting or restoring.

## CSRF and mutations

- All changes use `POST`/`DELETE` only, never `GET`.
- A browser mutation requires a valid cookie session **and** the `X-CSRF-Token` header (given only to
  same-origin JS via `/api/session`) **and** the service's own `Origin`.
- `POST` accepts only `Content-Type: application/json`; simple forms and `no-cors` requests are rejected (415).
- An arbitrary header (for example `X-Requested-With`) does not replace authentication.

## Response headers

`Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'`,
`X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `X-Frame-Options: DENY`, `Cache-Control: no-store` for the API.
There are no inline scripts and no external resources (CDNs, fonts).

## Rendering content

- All card text is rendered via `textContent`; HTML/Markdown is not interpreted (Markdown is shown as text).
- Match highlighting splits the text and uses `<mark>` elements created through the DOM API.
- Only `http://` and `https://` sources become links, with `target=_blank rel="noopener noreferrer"`.
  The server rejects `sources.url` with other schemes (`javascript:`, `file:`, `data:` …).

## Request limits

- Request body ≤ 2 MB, headers ≤ 32 KB, at most 16 concurrent requests (queue of 64, then 429).
- The server validates lengths, IDs (UUID), statuses, kind, version, keys, tags and sources; the API never accepts file paths.
- In search, user text is tokenized and every word is quoted again, so FTS syntax
  (`OR`, `NEAR`, `column:`, `*`, parentheses) is not passed through; the project filter is applied in SQL.

## Logs and data

- `knowledge_save` bodies, card content, authorization headers and sources are not logged.
- The technical audit keeps only time, operation and ID (30 days). Idempotency responses keep ID, version and hash (7 days).
- The server warns about strings that look like secrets (GitHub/Slack/AWS tokens, `sk-…`, JWTs, private keys,
  `password=`), but detection is not guaranteed.

## Good to know

- Data is stored locally, **but** MCP tool results read by a cloud AI client are sent
  to the model provider together with the rest of the chat context.
- The `project` filter organizes knowledge; it is not an access boundary: any connected client can read
  all projects. Blocking reads of individual projects would require server-side scope authorization.
- There is deliberately no MCP delete tool: only the user deletes, in the web UI.
- `mcp-stdio` with `Server.AutoStart` starts only its own executable with `serve --config <same file>`; nothing from the
  request is passed to the command line.
- Deleting a card also deletes its stored history. Deletion and compaction are not guaranteed secure erasure on SSDs, in OS snapshots or in backups.

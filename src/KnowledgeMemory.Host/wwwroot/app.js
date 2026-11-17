"use strict";
// Knowledge Memory web UI. All knowledge text is rendered with textContent only, never parsed as HTML.
(() => {
  const $ = (id) => document.getElementById(id);
  const el = (tag, cls, text) => {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text !== undefined && text !== null) e.textContent = text;
    return e;
  };

  const KIND = { task: "Task", decision: "Decision", reference: "Reference", preference: "Preference" };
  const STATUS = { active: "Active", completed: "Completed", superseded: "Superseded" };
  const MATCH = { exactKey: "exact key", fullText: "text", partialText: "partial match" };

  let csrf = null;
  let searchSeq = 0;
  let searchAbort = null;
  let debounceTimer = null;
  let items = [];
  let nextCursor = null;
  let current = null; // currently opened document
  let deleting = false;
  let diskTimer = null;

  // ------------------------------------------------------------------ API
  class ApiError extends Error {
    constructor(status, body) {
      super((body && body.error && body.error.message) || `HTTP ${status}`);
      this.status = status;
      this.code = body && body.error && body.error.code;
      this.details = body && body.error && body.error.details;
    }
  }

  async function api(path, opts = {}) {
    const headers = { Accept: "application/json" };
    const init = { method: opts.method || "GET", headers, credentials: "same-origin", cache: "no-store", signal: opts.signal };
    if (init.method !== "GET") {
      if (csrf) headers["X-CSRF-Token"] = csrf;
      if (opts.body !== undefined) {
        headers["Content-Type"] = "application/json";
        init.body = JSON.stringify(opts.body);
      }
    }
    const res = await fetch(path, init);
    let body = null;
    try { body = await res.json(); } catch { /* empty */ }
    if (!res.ok) throw new ApiError(res.status, body);
    return body;
  }

  // ------------------------------------------------------------------ auth
  async function boot() {
    const m = /^#bootstrap=([A-Za-z0-9_-]{20,100})$/.exec(location.hash);
    if (m) {
      // Remove the one-time code from the address bar before anything else.
      history.replaceState(null, "", location.pathname + location.search);
      try {
        const r = await api("/api/auth/exchange", { method: "POST", body: { code: m[1] } });
        csrf = r.csrfToken;
      } catch (e) {
        if (e instanceof ApiError) return showGate("The sign-in link is invalid, expired or already used.", true);
        return showGate("The Knowledge Memory service is unavailable. Start it and open the interface again.", true);
      }
    } else {
      try {
        const r = await api("/api/session");
        csrf = r.csrfToken;
      } catch (e) {
        if (e instanceof ApiError && e.status === 401) return showGate("No session. Open the interface with this local command — it creates a one-time sign-in link:", true);
        return showGate("The Knowledge Memory service is unavailable (offline). Start it with the serve command and reload the page.", false);
      }
    }
    $("gate").hidden = true;
    $("app").hidden = false;
    wire();
    await Promise.all([loadProjects(), render()]);
  }

  function showGate(message, withCommand) {
    $("app").hidden = true;
    $("gate").hidden = false;
    $("gate-message").textContent = message;
    $("gate-command").hidden = !withCommand;
    $("gate-command").textContent = 'KnowledgeMemory.exe open-ui --config "C:\\Path\\appsettings.json"';
  }

  // ------------------------------------------------------------------ URL state
  function state() {
    const p = new URLSearchParams(location.search);
    return {
      view: p.get("view") === "disk" ? "disk" : "list",
      q: p.get("q") || "",
      project: p.get("project") || "",
      kind: p.get("kind") || "",
      status: p.get("status") || "",
      doc: p.get("doc") || "",
    };
  }

  function navigate(patch, replace = false) {
    const s = Object.assign(state(), patch);
    const p = new URLSearchParams();
    if (s.view === "disk") p.set("view", "disk");
    for (const k of ["q", "project", "kind", "status", "doc"]) if (s[k] && s.view !== "disk") p.set(k, s[k]);
    const url = location.pathname + (p.toString() ? "?" + p : "");
    if (url === location.pathname + location.search) return render();
    (replace ? history.replaceState : history.pushState).call(history, null, "", url);
    return render();
  }

  // ------------------------------------------------------------------ wiring
  function wire() {
    window.addEventListener("popstate", () => render());
    document.querySelectorAll("[data-nav]").forEach((b) => b.addEventListener("click", () => {
      if (b.dataset.nav === "disk") navigate({ view: "disk", doc: "" });
      else navigate({ view: "list", project: "", doc: "" });
    }));
    $("search-form").addEventListener("submit", (e) => { e.preventDefault(); clearTimeout(debounceTimer); navigate({ q: $("q").value.trim() }, true); });
    $("q").addEventListener("input", () => {
      clearTimeout(debounceTimer);
      debounceTimer = setTimeout(() => navigate({ q: $("q").value.trim() }, true), 250);
    });
    $("kind").addEventListener("change", () => navigate({ kind: $("kind").value }));
    $("status").addEventListener("change", () => navigate({ status: $("status").value }));
    $("more").addEventListener("click", () => search(true));
    $("detail-close").addEventListener("click", () => navigate({ doc: "" }));
    $("detail-delete").addEventListener("click", () => openDelete());
    $("delete-cancel").addEventListener("click", () => closeDelete());
    $("delete-dialog").addEventListener("cancel", (e) => { if (deleting) e.preventDefault(); });
    $("delete-confirm").addEventListener("click", () => confirmDelete());
    $("btn-compact").addEventListener("click", () => diskAction("compact"));
    $("btn-backup").addEventListener("click", () => diskAction("backup"));
    document.addEventListener("keydown", (e) => {
      if (e.key === "Escape" && !$("detail").hidden && !$("delete-dialog").open) navigate({ doc: "" });
    });
  }

  // ------------------------------------------------------------------ render
  let lastListKey = null;
  async function render() {
    const s = state();
    document.querySelectorAll("[data-nav]").forEach((b) => b.removeAttribute("aria-current"));
    document.querySelectorAll("#project-list .nav-item").forEach((b) => b.toggleAttribute("aria-current", s.view === "list" && b.dataset.project === s.project));
    if (s.view === "disk") document.querySelector('[data-nav="disk"]').setAttribute("aria-current", "page");
    else if (!s.project) document.querySelector('[data-nav="all"]').setAttribute("aria-current", "page");
    document.querySelectorAll("#project-list .nav-item[aria-current]").forEach((b) => b.setAttribute("aria-current", "page"));

    $("view-disk").hidden = s.view !== "disk";
    $("view-list").hidden = s.view === "disk";
    clearInterval(diskTimer);
    if (s.view === "disk") {
      closeDetail();
      lastListKey = null;
      await loadDisk();
      diskTimer = setInterval(() => loadDisk(true), 5000);
      return;
    }
    if (document.activeElement !== $("q")) $("q").value = s.q;
    $("kind").value = s.kind;
    $("status").value = s.status;
    $("list-title").textContent = s.project ? projectName(s.project) : "All knowledge";

    const listKey = JSON.stringify([s.q, s.project, s.kind, s.status]);
    if (listKey !== lastListKey) {
      lastListKey = listKey;
      await search(false);
    }
    if (s.doc) await openDetail(s.doc); else closeDetail();
  }

  // ------------------------------------------------------------------ projects
  let projects = [];
  function projectName(key) {
    const p = projects.find((x) => x.project === key);
    return p ? p.display : key;
  }
  async function loadProjects() {
    try {
      projects = await api("/api/projects");
    } catch { projects = []; }
    const ul = $("project-list");
    ul.replaceChildren();
    let total = 0;
    for (const p of projects) {
      total += p.documents;
      const b = el("button", "nav-item");
      b.type = "button";
      b.dataset.project = p.project;
      b.append(el("span", null, p.display), el("span", "count", String(p.documents)));
      b.addEventListener("click", () => navigate({ view: "list", project: p.project, doc: "" }));
      const li = el("li");
      li.append(b);
      ul.append(li);
    }
    if (!projects.length) ul.append(el("li", "muted small nav-item", "None yet"));
    $("nav-all-count").textContent = projects.length ? String(total) : "";
    const s = state();
    if (s.project && s.view === "list") $("list-title").textContent = projectName(s.project);
    document.querySelectorAll("#project-list .nav-item").forEach((b) => { if (s.view === "list" && b.dataset.project === s.project) b.setAttribute("aria-current", "page"); });
  }

  // ------------------------------------------------------------------ search
  async function search(append) {
    const s = state();
    const seq = ++searchSeq;
    if (searchAbort) searchAbort.abort();
    searchAbort = new AbortController();
    const p = new URLSearchParams();
    if (s.q) p.set("q", s.q);
    if (s.project) p.set("project", s.project);
    if (s.kind) p.set("kind", s.kind);
    if (s.status) p.set("status", s.status);
    p.set("limit", "20");
    if (append && nextCursor) p.set("cursor", nextCursor);
    if (!append) setListState("loading");
    $("more").disabled = true;
    try {
      const r = await api("/api/documents?" + p, { signal: searchAbort.signal });
      if (seq !== searchSeq) return; // a newer search is in flight: ignore late answers
      items = append ? items.concat(r.items) : r.items;
      nextCursor = r.nextCursor || null;
      drawList(s);
      $("more").hidden = !r.hasMore;
      $("more").disabled = false;
    } catch (e) {
      if (e.name === "AbortError" || seq !== searchSeq) return;
      items = [];
      $("results").replaceChildren();
      $("more").hidden = true;
      if (e instanceof ApiError && e.status === 401) return showGate("The session has expired. Open the interface again with:", true);
      setListState(e instanceof ApiError ? "error" : "offline", e.message);
    }
  }

  function setListState(kind, message) {
    const box = $("list-state");
    box.className = "state" + (kind === "error" || kind === "offline" ? " error" : "");
    box.replaceChildren();
    $("result-count").textContent = "";
    if (kind === "loading") {
      box.textContent = "Loading…";
    } else if (kind === "empty") {
      box.append(el("h2", null, "The knowledge base is empty"));
      box.append(el("p", null, "Cards are saved by a connected AI client over MCP. No demo data is added."));
      const ol = el("ol");
      ol.append(
        el("li", null, "Connect Knowledge Memory in your AI client (the mcp-stdio command or the HTTP endpoint /mcp — see README)."),
        el("li", null, "Discuss a task and ask: “The task is done. Save the key points to the knowledge base.”"),
        el("li", null, "The card appears here once knowledge_save confirms the save."));
      box.append(ol);
    } else if (kind === "nomatch") {
      box.append(el("p", null, "Nothing found. That does not mean the task was never discussed: it may not have been saved, or it was described in other words."));
    } else if (kind === "offline") {
      box.textContent = "Cannot reach the Knowledge Memory service. Check that serve is running and reload the page.";
    } else {
      box.textContent = "Error: " + (message || "unknown error");
    }
    box.hidden = false;
  }

  function terms(q) {
    return (q.toLowerCase().match(/[\p{L}\p{N}_]+/gu) || []).filter((t) => t.length >= 2).slice(0, 12);
  }

  // Safe highlighting: split text and wrap matches into <mark> elements created via DOM APIs.
  function highlighted(text, q) {
    const frag = document.createDocumentFragment();
    const ts = terms(q);
    if (!ts.length) { frag.append(text); return frag; }
    const lower = text.toLowerCase();
    let i = 0;
    while (i < text.length) {
      let best = -1, len = 0;
      for (const t of ts) {
        const at = lower.indexOf(t, i);
        if (at !== -1 && (best === -1 || at < best)) { best = at; len = t.length; }
      }
      if (best === -1) { frag.append(text.slice(i)); break; }
      if (best > i) frag.append(text.slice(i, best));
      frag.append(el("mark", null, text.slice(best, best + len)));
      i = best + len;
    }
    return frag;
  }

  function keyLabel(d) {
    return d.externalKey ? `${d.projectDisplay} / #${d.externalKey}` : d.projectDisplay;
  }

  function fmtDate(iso) {
    const d = new Date(iso);
    return isNaN(d) ? "" : d.toLocaleString("en-US", { dateStyle: "medium", timeStyle: "short" });
  }

  function drawList(s) {
    const ul = $("results");
    ul.replaceChildren();
    if (!items.length) {
      const filtered = s.q || s.project || s.kind || s.status;
      setListState(filtered ? "nomatch" : "empty");
      return;
    }
    $("list-state").hidden = true;
    $("result-count").textContent = `${items.length}${nextCursor ? "+" : ""} ${plural(items.length, "card", "cards")}`;
    for (const d of items) {
      const li = el("li", "card");
      li.dataset.id = d.id;
      if (d.id === s.doc) li.classList.add("selected");
      const b = el("button", "card-open");
      b.type = "button";
      b.setAttribute("aria-label", `${keyLabel(d)}: ${d.title}`);
      const top = el("div", "card-top");
      top.append(el("span", "key", keyLabel(d)), el("span", "date", fmtDate(d.updatedAtUtc)));
      const title = el("div", "card-title");
      title.append(highlighted(d.title, s.q));
      const text = el("p", "card-text");
      text.append(highlighted(d.matchType === "fullText" || d.matchType === "partialText" ? d.snippet : d.summary, s.q));
      const badges = el("div", "tags");
      badges.append(el("span", "badge", KIND[d.kind] || d.kind));
      if (d.status !== "active") badges.append(el("span", "badge " + d.status, STATUS[d.status] || d.status));
      if (s.q && MATCH[d.matchType]) badges.append(el("span", "badge match", MATCH[d.matchType]));
      for (const t of d.tags) badges.append(el("span", "tag", "#" + t));
      b.append(top, title, text, badges);
      b.addEventListener("click", () => navigate({ doc: d.id }));
      const del = el("button", "row-delete", "Delete");
      del.type = "button";
      del.setAttribute("aria-label", `Delete: ${keyLabel(d)} — ${d.title}`);
      del.addEventListener("click", () => openDelete(d));
      li.append(b, del);
      ul.append(li);
    }
  }

  function plural(n, one, many) {
    return n === 1 ? one : many;
  }

  // ------------------------------------------------------------------ detail
  let detailSeq = 0;
  async function openDetail(id) {
    const seq = ++detailSeq;
    $("app").classList.add("with-detail");
    $("detail").hidden = false;
    document.querySelectorAll(".card").forEach((c) => c.classList.toggle("selected", c.dataset.id === id));
    if (current && current.id === id) return;
    $("detail-body").hidden = true;
    $("detail-key").textContent = "";
    $("detail-title").textContent = "";
    detailState("Loading…");
    try {
      const page = await api(`/api/documents/${encodeURIComponent(id)}?maxChars=1000000`);
      if (seq !== detailSeq) return;
      showDoc(page);
    } catch (e) {
      if (seq !== detailSeq) return;
      current = null;
      detailState(e instanceof ApiError && e.status === 404 ? "Card not found — it may have been deleted." : "Could not load the card: " + e.message, true);
    }
  }

  function detailState(text, isError) {
    const s = $("detail-state");
    s.textContent = text;
    s.className = "state" + (isError ? " error" : "");
    s.hidden = !text;
  }

  function showDoc(page) {
    const d = page.document;
    current = d;
    detailState("");
    $("version-banner").hidden = true;
    $("detail-body").hidden = false;
    $("detail-key").textContent = keyLabel(d);
    $("detail-title").textContent = d.title;
    const badges = $("detail-badges");
    badges.replaceChildren(el("span", "badge", KIND[d.kind] || d.kind), el("span", "badge " + d.status, STATUS[d.status] || d.status));
    for (const t of d.tags) badges.append(el("span", "tag", "#" + t));
    $("detail-summary").textContent = d.summary;
    $("detail-content").textContent = d.content;
    $("detail-truncated").hidden = !page.truncated;
    $("detail-truncated").textContent = page.truncated ? `Showing ${d.content.length} of ${page.contentTotalLength} characters.` : "";

    const src = $("detail-sources");
    src.replaceChildren();
    if (!d.sources.length) src.append(el("li", "muted", "Source unknown"));
    for (const s of d.sources) {
      const li = el("li");
      if (s.label) li.append(el("span", null, s.label + " "));
      if (s.url && /^https?:\/\//i.test(s.url)) {
        const a = el("a", null, s.url);
        a.href = s.url;
        a.target = "_blank";
        a.rel = "noopener noreferrer";
        li.append(a);
      } else if (s.url) {
        li.append(el("span", "muted", s.url)); // never an executable link
      }
      if (s.note) li.append(el("div", "muted small", s.note));
      src.append(li);
    }

    const meta = $("detail-meta");
    meta.replaceChildren();
    const row = (k, v) => meta.append(el("dt", null, k), el("dd", null, v));
    row("ID", d.id);
    row("Version", String(d.version));
    row("Created", fmtDate(d.createdAtUtc));
    row("Updated", fmtDate(d.updatedAtUtc));
    if (d.supersededBy) row("Superseded by", d.supersededBy);
    meta.append(el("dt", null, ""), el("dd", "muted small", "Times are when the card was saved, not when the decision was made."));
    loadHistory(d);
  }

  function closeDetail() {
    current = null;
    detailSeq++;
    $("detail").hidden = true;
    $("app").classList.remove("with-detail");
    document.querySelectorAll(".card.selected").forEach((c) => c.classList.remove("selected"));
  }


  // ------------------------------------------------------------------ version history
  let historySeq = 0;
  let restoring = false;

  async function loadHistory(d) {
    const seq = ++historySeq;
    const box = $("detail-history");
    box.replaceChildren(el("p", "muted small", "Loading…"));
    try {
      const versions = await api(`/api/documents/${encodeURIComponent(d.id)}/versions`);
      if (seq !== historySeq || !current || current.id !== d.id) return;
      box.replaceChildren();
      if (!versions.length) {
        box.append(el("p", "muted small", "No earlier versions. Each update keeps the previous version here."));
        return;
      }
      const ul = el("ul", "history");
      for (const v of versions) {
        const li = el("li", "history-row");
        const info = el("div", "history-info");
        info.append(el("strong", null, "Version " + v.version), el("span", "muted small", ` · saved ${fmtDate(v.savedAtUtc)} · ${v.contentLength} characters`));
        if (v.title !== d.title) info.append(el("div", "small", v.title));
        const view = el("button", "btn btn-secondary btn-small", "View");
        view.type = "button";
        view.setAttribute("aria-label", `View version ${v.version}`);
        view.addEventListener("click", () => viewVersion(d, v.version));
        li.append(info, view);
        ul.append(li);
      }
      box.append(ul);
    } catch (e) {
      if (seq !== historySeq) return;
      box.replaceChildren(el("p", "error small", "Could not load the history: " + e.message));
    }
  }

  async function viewVersion(d, version) {
    const banner = $("version-banner");
    try {
      const old = await api(`/api/documents/${encodeURIComponent(d.id)}/versions/${version}`);
      if (!current || current.id !== d.id) return;
      banner.replaceChildren();
      banner.append(el("p", null, `You are viewing version ${version} (saved ${fmtDate(old.updatedAtUtc)}). The current version is ${d.version}. Lines marked − were removed later, lines marked + were added later.`));
      const actions = el("div", "actions");
      const back = el("button", "btn btn-secondary", "Back to current");
      back.type = "button";
      back.addEventListener("click", () => showCurrentAgain());
      const restore = el("button", "btn", `Restore version ${version}`);
      restore.type = "button";
      restore.addEventListener("click", () => restoreVersion(d, version, restore));
      actions.append(back, restore);
      banner.append(actions);
      banner.hidden = false;
      $("detail-title").textContent = old.title;
      $("detail-summary").textContent = old.summary;
      renderDiff($("detail-content"), old.content, d.content);
      banner.scrollIntoView({ block: "nearest" });
    } catch (e) {
      toast("Could not load the version: " + e.message);
    }
  }

  function showCurrentAgain() {
    $("version-banner").hidden = true;
    if (current) {
      $("detail-title").textContent = current.title;
      $("detail-summary").textContent = current.summary;
      $("detail-content").textContent = current.content;
    }
  }

  async function restoreVersion(d, version, button) {
    if (restoring) return;
    if (!window.confirm(`Restore version ${version}? It becomes the new current version ${d.version + 1}. The current text stays in the history.`)) return;
    restoring = true;
    button.disabled = true;
    try {
      const r = await api(`/api/documents/${encodeURIComponent(d.id)}/versions/${version}/restore`, { method: "POST", body: { expectedVersion: d.version } });
      toast(`Version ${version} restored as version ${r.version}.`);
    } catch (e) {
      if (e instanceof ApiError && e.code === "version_conflict") toast("Another client changed this card. The new version is shown — check it before restoring.");
      else toast("Not restored: " + e.message);
    } finally {
      restoring = false;
      button.disabled = false;
      $("version-banner").hidden = true;
      current = null;
      lastListKey = null;
      await navigate({ doc: d.id }, true);
    }
  }

  /** Line diff (LCS). Text is inserted with textContent only. */
  function renderDiff(container, oldText, newText) {
    const a = oldText.split("\n"), b = newText.split("\n");
    container.replaceChildren();
    if (a.length * b.length > 4_000_000) { container.textContent = oldText; return; }
    const n = a.length, m = b.length;
    const dp = Array.from({ length: n + 1 }, () => new Uint32Array(m + 1));
    for (let i = n - 1; i >= 0; i--)
      for (let j = m - 1; j >= 0; j--)
        dp[i][j] = a[i] === b[j] ? dp[i + 1][j + 1] + 1 : Math.max(dp[i + 1][j], dp[i][j + 1]);
    let i = 0, j = 0;
    const line = (cls, mark, text) => container.append(el("div", "diff-line " + cls, mark + " " + text));
    while (i < n && j < m) {
      if (a[i] === b[j]) { line("same", " ", a[i]); i++; j++; }
      else if (dp[i + 1][j] >= dp[i][j + 1]) { line("del", "−", a[i]); i++; }
      else { line("ins", "+", b[j]); j++; }
    }
    while (i < n) line("del", "−", a[i++]);
    while (j < m) line("ins", "+", b[j++]);
  }

  // ------------------------------------------------------------------ delete
  let target = null; // card being deleted: the opened document or a list row
  function openDelete(fromRow) {
    target = fromRow || current;
    if (!target) return;
    const d = target;
    const what = $("delete-what");
    what.replaceChildren();
    const row = (k, v) => what.append(el("dt", null, k), el("dd", null, v));
    row("Project", d.projectDisplay);
    row("Key", d.externalKey ? "#" + d.externalKey : "—");
    row("Title", d.title);
    row("Version", String(d.version));
    $("delete-error").hidden = true;
    setDeleting(false);
    $("delete-dialog").showModal();
    $("delete-cancel").focus();
  }

  function closeDelete() {
    if (deleting) return;
    $("delete-dialog").close();
  }

  function setDeleting(on) {
    deleting = on;
    $("delete-confirm").disabled = on;
    $("delete-cancel").disabled = on;
    $("delete-confirm").textContent = on ? "Deleting…" : "Delete";
  }

  async function confirmDelete() {
    if (deleting || !target) return; // blocks double submit
    const d = target;
    setDeleting(true);
    try {
      await api(`/api/documents/${encodeURIComponent(d.id)}?expectedVersion=${d.version}`, { method: "DELETE" });
      deleted(d, "Card deleted. Disk space reclaim is scheduled.");
    } catch (e) {
      if (e instanceof ApiError && e.code === "version_conflict") {
        setDeleting(false);
        $("delete-dialog").close();
        current = null;
        lastListKey = null;
        await navigate({ doc: d.id }, true);
        toast("Another client changed this card after you opened it. The new version is shown — review it before deleting.");
      } else if (e instanceof ApiError && e.status === 404) {
        deleted(d, "The card was already deleted by another client.");
      } else if (e instanceof ApiError) {
        setDeleting(false);
        showDeleteError("Not deleted: " + e.message);
      } else {
        // Connection broken: the outcome is unknown, re-read the actual state.
        try {
          await api(`/api/documents/${encodeURIComponent(d.id)}?maxChars=1000`);
          setDeleting(false);
          showDeleteError("The connection dropped. The card still exists — it was not deleted.");
        } catch (e2) {
          if (e2 instanceof ApiError && e2.status === 404) deleted(d, "Card deleted (confirmed by a second check).");
          else { setDeleting(false); showDeleteError("Cannot reach the service. The delete result is unknown — reload the page once the service is available."); }
        }
      }
    }
  }

  function showDeleteError(text) {
    $("delete-error").textContent = text;
    $("delete-error").hidden = false;
  }

  function deleted(d, message) {
    setDeleting(false);
    $("delete-dialog").close();
    items = items.filter((x) => x.id !== d.id);
    const li = document.querySelector(`.card[data-id="${CSS.escape(d.id)}"]`);
    if (li) li.remove();
    if (!items.length) drawList(state());
    else $("result-count").textContent = `${items.length}${nextCursor ? "+" : ""} ${plural(items.length, "card", "cards")}`;
    if (current && current.id === d.id) current = null;
    target = null;
    toast(message);
    loadProjects();
    if (state().doc === d.id) navigate({ doc: "" }, true);
  }

  // ------------------------------------------------------------------ disk
  function fmtBytes(n) {
    if (n < 1024) return `${n} B`;
    const u = ["KB", "MB", "GB"];
    let v = n / 1024, i = 0;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return `${v.toLocaleString("en-US", { maximumFractionDigits: v < 10 ? 2 : 1 })} ${u[i]}`;
  }

  const RESULT = {
    compact_ok: "Compaction finished",
    compact_failed: "Compaction failed",
    reclaim_done: "Free pages returned to the file system",
    reclaim_partial: "Some pages returned, the rest is scheduled",
    checkpoint_busy: "WAL not truncated: the database is busy with reads, will retry",
    maintenance_error: "Maintenance error, will retry",
  };
  const MAINT = { idle: "Idle", pending: "Scheduled", running: "Running", busy: "Deferred: database busy", error: "Error" };

  async function loadDisk(quiet) {
    const box = $("disk-state");
    if (!quiet) { box.textContent = "Loading…"; box.className = "state"; box.hidden = false; }
    try {
      const s = await api("/api/disk");
      box.hidden = true;
      $("disk-body").hidden = false;
      const stats = $("disk-stats");
      stats.replaceChildren();
      const stat = (parent, k, v) => { const d = el("div"); d.append(el("dt", null, k), el("dd", null, v)); parent.append(d); };
      stat(stats, "Cards", String(s.documents));
      stat(stats, "Main database file", fmtBytes(s.mainBytes));
      stat(stats, "WAL", fmtBytes(s.walBytes));
      stat(stats, "SHM", fmtBytes(s.shmBytes));
      stat(stats, `Backups (${s.backupCount})`, fmtBytes(s.backupBytes));
      stat(stats, "Exports", fmtBytes(s.exportBytes));
      stat(stats, "All application files", fmtBytes(s.totalBytes));
      stat(stats, "Reclaimable (estimate)", fmtBytes(s.reclaimableEstimateBytes));
      const m = s.maintenance;
      const mt = $("disk-maint");
      mt.replaceChildren();
      stat(mt, "State", MAINT[m.state] || m.state);
      stat(mt, "Last run", m.lastRunAtUtc ? fmtDate(m.lastRunAtUtc) : "—");
      const res = RESULT[m.lastResultCode] || m.lastResult || "—";
      stat(mt, "Result", m.lastError ? `${res}: ${m.lastError}` : res);
      if (m.lastBytesBefore != null) stat(mt, "Working files before → after", `${fmtBytes(m.lastBytesBefore)} → ${fmtBytes(m.lastBytesAfter)}`);
    } catch (e) {
      if (quiet) return;
      $("disk-body").hidden = true;
      box.className = "state error";
      box.textContent = e instanceof ApiError ? "Error: " + e.message : "Cannot reach the Knowledge Memory service.";
      box.hidden = false;
    }
  }

  async function diskAction(kind) {
    const btn = kind === "compact" ? $("btn-compact") : $("btn-backup");
    const out = $("disk-result");
    btn.disabled = true;
    out.hidden = false;
    out.className = "result-box";
    out.textContent = kind === "compact" ? "Compacting the database… New changes will wait until it finishes." : "Creating and verifying a backup…";
    try {
      if (kind === "compact") {
        const r = await api("/api/disk/compact", { method: "POST", body: {} });
        out.className = "result-box " + (r.status === "ok" ? "ok" : "error");
        const lines = [
          r.status === "ok" ? "Compaction finished." : r.status === "busy" ? "The database is busy — try again later." : "Compaction failed.",
          `Working files (DB + WAL + SHM): ${fmtBytes(r.bytesBefore)} → ${fmtBytes(r.bytesAfter)} (${r.bytesAfter <= r.bytesBefore ? "freed " + fmtBytes(r.bytesBefore - r.bytesAfter) : "grew by " + fmtBytes(r.bytesAfter - r.bytesBefore)}).`,
          `Duration: ${r.durationMs} ms.`,
        ];
        if (r.error) lines.push(r.error);
        for (const w of r.warnings) lines.push("Warning: " + w);
        out.textContent = lines.join("\n");
      } else {
        const r = await api("/api/disk/backup", { method: "POST", body: {} });
        out.className = "result-box ok";
        out.textContent = `Backup created and verified (integrity_check): ${r.fileName}, ${fmtBytes(r.bytes)}.`;
      }
    } catch (e) {
      out.className = "result-box error";
      out.textContent = e instanceof ApiError ? "Error: " + e.message : "Cannot reach the service. The result is unknown — refresh the statistics.";
    } finally {
      btn.disabled = false;
      loadDisk(true);
    }
  }

  // ------------------------------------------------------------------ toast
  let toastTimer = null;
  function toast(text) {
    const t = $("toast");
    t.textContent = text;
    t.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => { t.hidden = true; }, 6000);
  }

  $("gate-retry").addEventListener("click", () => location.reload());
  boot();
})();

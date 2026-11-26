"use strict";
(() => {
  const $ = (s, r = document) => r.querySelector(s);
  const $$ = (s, r = document) => [...r.querySelectorAll(s)];
  const THUMB = 0x01, FULL = 0x02, SHARE = 0x10;
  const natural = (a, b) => String(a).localeCompare(String(b), "pt", { numeric: true, sensitivity: "base" });
  const store = {
    get: (k, d) => { try { return localStorage.getItem(k) ?? d; } catch { return d; } },
    set: (k, v) => { try { localStorage.setItem(k, v); } catch {} },
  };

  const state = {
    token: sessionStorage.getItem("token"),
    me: null,
    ws: null,
    agents: new Map(),        // id -> {info, el, imgUrl}
    selected: new Set(),
    filter: store.get("roomFilter2", ""),
    search: "",
    catalog: { schools: [], rooms: [] },
    viewing: null,
    viewerUrl: null,
    share: null,
    procsFor: null,
    threads: new Map(),   // id -> {msgs:[{text,mine,hand,at}], unread, hand}
    openThread: null,
    reconnectDelay: 1000,
  };

  // ================================================================== utilitários
  function toast(msg) {
    const t = $("#toast");
    t.textContent = msg;
    t.hidden = false;
    clearTimeout(toast.timer);
    toast.timer = setTimeout(() => (t.hidden = true), 2600);
  }

  async function api(path, opts = {}) {
    const r = await fetch(path, {
      ...opts,
      headers: { "Content-Type": "application/json", Authorization: "Bearer " + state.token, ...(opts.headers || {}) },
      body: opts.body && typeof opts.body !== "string" ? JSON.stringify(opts.body) : opts.body,
    });
    if (r.status === 401) { sessionStorage.clear(); location.reload(); throw new Error("sessão expirada"); }
    const j = await r.json().catch(() => ({}));
    if (!r.ok) throw new Error(j.error || `Erro ${r.status}`);
    return j;
  }

  const esc = (s) => String(s ?? "").replace(/[&<>"']/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
  const names = (ids) => [...ids].map((id) => state.agents.get(id)?.info.hostname || id).join(", ");
  const schoolName = (id) => state.catalog.schools.find((s) => s.id === id)?.name || id || "Outros";

  // ================================================================== marca (nome + frase)
  fetch("/api/brand").then((r) => r.json()).then((b) => {
    for (const el of $$("[data-brand-name]")) el.textContent = b.name;
    for (const el of $$("[data-brand-tagline]")) el.textContent = b.tagline;
    document.title = b.name;
  }).catch(() => {});

  // ================================================================== login
  $("#loginForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = new FormData(e.target);
    $("#loginError").textContent = "";
    try {
      const r = await fetch("/api/login", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ username: f.get("username"), password: f.get("password") }),
      });
      const j = await r.json().catch(() => ({}));
      if (!r.ok) { $("#loginError").textContent = j.error || "Falha no login."; return; }
      state.token = j.token;
      sessionStorage.setItem("token", j.token);
      showConsole();
    } catch {
      $("#loginError").textContent = "Sem ligação ao servidor.";
    }
  });

  async function logout() {
    stopShare();
    try { await api("/api/logout", { method: "POST" }); } catch {}
    sessionStorage.clear();
    location.reload();
  }

  async function showConsole() {
    $("#login").hidden = true;
    $("#console").hidden = false;
    try { state.me = await api("/api/me"); } catch { return; }
    $("#who").textContent = state.me.displayName || state.me.username;
    if (state.me.version) {
      const d = state.me.buildDate ? new Date(state.me.buildDate) : null;
      $("#appVersion").textContent = `v${state.me.version}` + (d ? ` · ${d.toLocaleDateString("pt-PT")} ${d.toLocaleTimeString("pt-PT", { hour: "2-digit", minute: "2-digit" })}` : "");
    }
    for (const el of $$("[data-admin]")) el.hidden = !state.me.isAdmin;
    $("#zoom").value = store.get("zoom", "240");
    applyZoom();
    buildSwatches();
    await loadCatalog();
    connect();
  }

  // ================================================================== menu do utilizador
  $("#userBtn").addEventListener("click", (e) => { e.stopPropagation(); $("#userMenu").hidden = !$("#userMenu").hidden; });
  document.addEventListener("click", () => { $("#userMenu").hidden = true; $("#ctx").hidden = true; const mm = $("#moreMenu"); if (mm) mm.hidden = true; });
  $("#userMenu").addEventListener("click", (e) => {
    const act = e.target.closest("button")?.dataset.act;
    if (act === "logout") logout();
    if (act === "password") openPassword();
    if (act === "users") openUsers();
    if (act === "rooms") openRooms();
    if (act === "system") openSystem();
  });

  // ================================================================== WebSocket
  function connect() {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    const ws = new WebSocket(`${proto}//${location.host}/ws/teacher?token=${encodeURIComponent(state.token)}`);
    ws.binaryType = "arraybuffer";
    state.ws = ws;
    setStatus("A ligar…", "");
    ws.onopen = () => { state.reconnectDelay = 1000; setStatus("Ligado", "ok"); sendPrefs(); };
    ws.onclose = () => {
      if (state.ws !== ws) return;
      setStatus("Sem ligação — a tentar de novo…", "bad");
      for (const id of [...state.agents.keys()]) removeAgent(id);
      setTimeout(async () => {
        try {
          const r = await fetch("/api/me", { headers: { Authorization: "Bearer " + state.token } });
          if (r.status === 401) { sessionStorage.clear(); location.reload(); return; }
        } catch {}
        connect();
      }, state.reconnectDelay);
      state.reconnectDelay = Math.min(state.reconnectDelay * 2, 15000);
    };
    ws.onmessage = (ev) => (typeof ev.data === "string" ? onJson(JSON.parse(ev.data)) : onBinary(new Uint8Array(ev.data)));
  }

  const send = (o) => state.ws?.readyState === WebSocket.OPEN && state.ws.send(JSON.stringify(o));
  const cmd = (action, targets, extra = {}) => send({ type: "cmd", action, targets: [...targets], ...extra });
  function sendPrefs() {
    send({ type: "prefs", thumbMinIntervalMs: parseInt($("#netMode").value, 10), room: state.filter || null, thumbScale: thumbScale() });
  }
  $("#netMode").addEventListener("change", sendPrefs);

  function setStatus(text, cls) {
    const s = $("#status");
    s.textContent = text;
    s.className = "pill " + cls;
  }

  function onJson(m) {
    switch (m.type) {
      case "hello": for (const a of m.agents) upsertAgent(a); refresh(); break;
      case "agentJoined":
      case "agentUpdate": upsertAgent(m.agent); refresh(); break;
      case "agentLeft": removeAgent(m.id); refresh(); break;
      case "reply": onIncoming(m.id, m.user, m.text, false); break;
      case "procs": if (m.id === state.procsFor) renderProcs(m.list || []); break;
      case "chat": onIncoming(m.id, m.user, m.text, false); break;
      case "hand": onHand(m.id, m.user); break;
      case "catalogChanged": loadCatalog(); break;
      case "collectBatch": state.collectBatch = m.batch; state.collectWaiting = 0; break;
      case "collected": onCollected(m); break;
      case "pollStarted": onPollStarted(m); break;
      case "answer": onAnswer(m); break;
    }
  }

  function onBinary(buf) {
    const kind = buf[0], idLen = buf[1];
    const id = new TextDecoder().decode(buf.subarray(2, 2 + idLen));
    const url = URL.createObjectURL(new Blob([buf.subarray(2 + idLen)], { type: "image/jpeg" }));
    if (kind === THUMB) {
      const a = state.agents.get(id);
      if (!a) return URL.revokeObjectURL(url);
      const img = a.el.querySelector("img");
      img.src = url;
      img.hidden = false;
      a.el.querySelector(".noimg").hidden = true;
      if (a.imgUrl) URL.revokeObjectURL(a.imgUrl);
      a.imgUrl = url;
    } else if (kind === FULL && id === state.viewing) {
      $("#viewerImg").src = url;
      $("#viewerWait").hidden = true;
      if (state.viewerUrl) URL.revokeObjectURL(state.viewerUrl);
      state.viewerUrl = url;
    } else URL.revokeObjectURL(url);
  }

  // ================================================================== escolas e salas (menu "Sala")
  async function loadCatalog() {
    try { state.catalog = await api("/api/catalog"); } catch {}
    buildRoomSelect();
    refresh();
  }

  function allRooms() {
    // salas conhecidas do servidor + salas dos PCs ligados
    const map = new Map();
    for (const r of state.catalog.rooms) map.set(r.key, r);
    for (const a of state.agents.values()) {
      const key = a.info.key || `${a.info.school || ""}/${a.info.room}`;
      if (a.info.room && !map.has(key)) map.set(key, { school: a.info.school || "", name: a.info.room, key });
    }
    return [...map.values()];
  }

  function buildRoomSelect() {
    const sel = $("#roomFilter");
    const rooms = allRooms();
    const schools = [...state.catalog.schools.map((s) => s.id)];
    for (const r of rooms) if (!schools.includes(r.school)) schools.push(r.school);
    sel.innerHTML = "";
    sel.add(new Option("🌍 Todas as escolas e salas", ""));
    for (const sc of schools) {
      const list = rooms.filter((r) => r.school === sc).sort((a, b) => natural(a.name, b.name));
      if (!list.length && (!state.me?.isAdmin || !state.catalog.schools.some((s) => s.id === sc))) continue;
      const g = document.createElement("optgroup");
      g.label = `🏫 ${schoolName(sc)}`;
      g.append(new Option(`Toda a escola — ${schoolName(sc)}`, `${sc}/*`));
      for (const r of list) g.append(new Option(r.name, r.key));
      sel.append(g);
    }
    if (![...sel.options].some((o) => o.value === state.filter)) {
      if (state.filter) sel.add(new Option(state.filter, state.filter));
    }
    sel.value = state.filter;
  }

  $("#roomFilter").addEventListener("change", (e) => {
    state.filter = e.target.value;
    store.set("roomFilter2", state.filter);
    state.selected.clear();
    sendPrefs();
    refresh();
  });

  $("#search").addEventListener("input", (e) => { state.search = e.target.value.trim().toLowerCase(); refresh(); });
  // miniaturas maiores: o PC passa a enviar imagens com mais resolução (2x a partir de 400 px, 3x a partir de 640 px)
  function thumbScale() { const z = +$("#zoom").value; return z >= 640 ? 3 : z >= 400 ? 2 : 1; }
  let lastScale = 0;
  $("#zoom").addEventListener("input", () => {
    store.set("zoom", $("#zoom").value); applyZoom();
    if (thumbScale() !== lastScale) { lastScale = thumbScale(); sendPrefs(); }
  });
  function applyZoom() { $("#grid").style.setProperty("--tile", $("#zoom").value + "px"); }
  for (const b of document.querySelectorAll("[data-zoom]")) b.addEventListener("click", () => {
    $("#zoom").value = b.dataset.zoom; $("#zoom").dispatchEvent(new Event("input"));
  });

  const PALETTE = ["#4f6bff", "#12a372", "#e5394f", "#ffad1f", "#6a3dff", "#00b8a3", "#ff5a6a", "#121826"];
  function buildSwatches() {
    for (const host of $$("[data-swatches]")) {
      const hidden = host.closest("label").parentElement.querySelector("[name=color]");
      host.innerHTML = PALETTE.map((c) => `<button type="button" style="background:${c}" data-c="${c}" class="${c === hidden.value ? "sel" : ""}"></button>`).join("");
      host.onclick = (e) => {
        const bt = e.target.closest("[data-c]");
        if (!bt) return;
        hidden.value = bt.dataset.c;
        for (const x of host.children) x.classList.toggle("sel", x === bt);
      };
    }
  }

  function matches(info) {
    const f = state.filter;
    if (f) {
      if (f.endsWith("/*")) { if ((info.school || "") !== f.slice(0, -2)) return false; }
      else if ((info.key || `${info.school || ""}/${info.room}`) !== f) return false;
    }
    if (state.search) {
      const hay = `${info.hostname} ${info.user} ${info.userName || ""} ${info.label || ""} ${info.id} ${info.room}`.toLowerCase();
      if (!hay.includes(state.search)) return false;
    }
    return true;
  }

  /** Nome de quem tem sessão: nome completo do AD; senão a conta sem domínio; senão "sem sessão". */
  function personName(info) {
    if (info.label) return info.label;
    if (info.userName) return info.userName;
    if (info.user) return info.user.split("\\").pop();
    return "— sem sessão —";
  }

  $("#moreBtn").addEventListener("click", (e) => {
    e.stopPropagation();
    const m = $("#moreMenu");
    m.hidden = !m.hidden;
    if (m.hidden) return;
    // abre para o lado onde houver espaço (nunca fica cortado fora da janela)
    m.classList.remove("to-left");
    const r = m.getBoundingClientRect();
    if (r.right > innerWidth - 8) m.classList.add("to-left");
    if (m.getBoundingClientRect().left < 8) m.classList.remove("to-left");
  });

  // ================================================================== grelha
  function upsertAgent(info) {
    let a = state.agents.get(info.id);
    if (!a) {
      const el = document.createElement("div");
      el.className = "pc";
      el.tabIndex = 0;
      el.innerHTML = `<div class="shot"><img alt="" hidden><span class="noimg">sem imagem</span></div>
        <div class="badges"></div><div class="meta"><span class="person"></span><span class="host"></span></div>`;
      el.addEventListener("click", (e) => { if (e.detail <= 1) toggleSelect(info.id, e.shiftKey || e.ctrlKey || e.metaKey); });
      el.addEventListener("dblclick", () => openViewer(info.id));
      el.addEventListener("keydown", (e) => { if (e.key === "Enter") openViewer(info.id); });
      el.addEventListener("contextmenu", (e) => { e.preventDefault(); openCtx(info.id, e.clientX, e.clientY); });
      a = { el, info, imgUrl: null };
      state.agents.set(info.id, a);
    }
    a.info = info;
    a.el.querySelector(".person").textContent = personName(info);
    a.el.querySelector(".host").textContent = `${info.hostname || info.id} · ${info.room || "sem sala"}`;
    a.el.title = `${info.id} · ${info.ip} · ${info.os}\n${schoolName(info.school)} · ${info.room}`;
    a.el.dataset.key = info.hostname || info.id;
    const b = a.el.querySelector(".badges");
    b.innerHTML = (info.locked ? `<span class="badge lock">🔒 BLOQUEADO</span>` : "") +
      (info.noInternet ? `<span class="badge net">🚫 SEM NET</span>` : "") +
      (info.demo ? `<span class="badge demo">🎬 EM DEMONSTRAÇÃO</span>` : "") +
      (info.broadcasting ? `<span class="badge share">📺 A VER</span>` : "") +
      (info.outdated ? `<span class="badge" title="Este PC vai atualizar o agente sozinho">⟳</span>` : "");
    placeSorted(a.el);
  }

  function placeSorted(el) {
    const grid = $("#grid");
    const key = el.dataset.key;
    const after = [...grid.children].find((c) => c !== el && natural(c.dataset.key, key) > 0);
    if (el.nextSibling !== after || el.parentNode !== grid) grid.insertBefore(el, after || null);
  }

  function removeAgent(id) {
    const a = state.agents.get(id);
    if (!a) return;
    if (a.imgUrl) URL.revokeObjectURL(a.imgUrl);
    a.el.remove();
    state.agents.delete(id);
    state.selected.delete(id);
    state.share?.targets.delete(id);
    if (state.viewing === id) closeViewer();
    if (state.openThread === id) closeThread();
  }

  const visibleIds = () => [...state.agents.values()].filter((a) => matches(a.info)).map((a) => a.info.id);

  let roomsSig = "";
  function refresh() {
    const sig = allRooms().map((r) => r.key).sort().join("|") + state.catalog.schools.map((s) => s.id + s.name).join();
    if (sig !== roomsSig) { roomsSig = sig; buildRoomSelect(); }
    const vis = new Set(visibleIds());
    let locked = 0, withUser = 0;
    for (const [id, a] of state.agents) {
      const v = vis.has(id);
      a.el.hidden = !v;
      if (!v) state.selected.delete(id);
      a.el.classList.toggle("selected", state.selected.has(id));
      if (v && a.info.locked) locked++;
      if (v && a.info.user) withUser++;
    }
    $("#empty").hidden = vis.size > 0;
    $("#counts").textContent = vis.size ? `${vis.size} ligados · ${withUser} com sessão · ${locked} bloqueados` : "";
    $("#selCount").textContent = state.selected.size ? `${state.selected.size} selecionado${state.selected.size === 1 ? "" : "s"}` : "nenhum selecionado";
    for (const b of $$("[data-needs-sel]")) b.disabled = state.selected.size === 0;
    $("#shareBtn").hidden = !!state.share;
    $("#shareStopBtn").hidden = !state.share;
    if (state.share) $("#shareStopBtn").textContent = `⏹ Parar partilha (${state.share.targets.size} PCs)`;
    renderThreads();
  }

  function toggleSelect(id, additive) {
    if (!additive) {
      const only = state.selected.size === 1 && state.selected.has(id);
      state.selected.clear();
      if (!only) state.selected.add(id);
    } else if (state.selected.has(id)) state.selected.delete(id);
    else state.selected.add(id);
    refresh();
  }
  $("#selAll").addEventListener("click", () => { for (const id of visibleIds()) state.selected.add(id); refresh(); });
  $("#selNone").addEventListener("click", () => { state.selected.clear(); refresh(); });

  // ================================================================== ações
  function runAction(action, ids) {
    ids = new Set(ids);
    if (!ids.size) return;
    switch (action) {
      case "view": return openViewer([...ids][0]);
      case "lock": return openLock(ids);
      case "unlock": cmd("unlock", ids); return toast(`🔓 Desbloqueados: ${ids.size}`);
      case "message": return openMessage(ids);
      case "openUrl": return openDialog("#urlDialog", ids, (f) => {
        const url = String(f.get("url")).trim();
        cmd("openUrl", ids, { url });
        toast(`🌐 Link aberto em ${ids.size} PC(s)`);
      });
      case "type": return openDialog("#typeDialog", ids, (f) => {
        cmd("type", ids, { text: String(f.get("text")) });
        toast(`⌨ Texto enviado para ${ids.size} PC(s)`);
      });
      case "procs": return openProcs([...ids][0]);
      case "internetOff": cmd("internetOff", ids); return toast(`🚫 Internet cortada em ${ids.size} PC(s)`);
      case "internetOn": cmd("internetOn", ids); return toast(`🌐 Internet reposta em ${ids.size} PC(s)`);
      case "closeAll":
        if (confirm(`Fechar todos os programas em ${ids.size} PC(s)?\nO trabalho não guardado perde-se.`)) cmd("closeAll", ids);
        return;
      case "sendFile": return pickAndSend(ids);
      case "collect":
        cmd("collect", ids);
        state.collectWaiting = ids.size;
        return toast(`📥 A recolher trabalhos de ${ids.size} PC(s)…`);
      case "poll": return openPoll(ids);
      case "demo": return startDemo([...ids][0]);
      case "chat": return openThread([...ids][0]);
      case "notice": return openDialog("#noticeDialog", ids, (f) => {
        const endAction = f.get("endAction") || "";
        cmd("notice", ids, { text: f.get("text"), color: f.get("color"), seconds: +f.get("seconds"),
          style: f.get("style") || "banner", corner: f.get("corner") || "br",
          closable: f.get("closable") === "on", endAction,
          endSeconds: endAction === "lock" ? (+(f.get("endMinutes") || 0)) * 60 : 0 });
        toast(`\u{1F4E3} Aviso em ${ids.size} PC(s)`);
      });
      case "minimize": cmd("minimize", ids); return toast("Minimizado");
      case "logoff":
      case "reboot":
      case "shutdown": {
        const label = { logoff: "Terminar a sessão em", reboot: "Reiniciar", shutdown: "Desligar" }[action];
        if (confirm(`${label} ${ids.size} computador(es)?\n${names(ids)}\n\nO trabalho não guardado dos alunos perde-se.`)) cmd(action, ids);
      }
    }
  }
  let muted = false;
  for (const b of $$("[data-cmd]")) b.addEventListener("click", () => {
    if (b.dataset.cmd === "mute") { muted = !muted; cmd("mute", state.selected, { on: muted }); b.textContent = muted ? "\u{1F50A} Repor som" : "\u{1F507} Silenciar"; return; }
    runAction(b.dataset.cmd, state.selected);
  });

  // menu de contexto (botão direito num PC)
  let ctxIds = [], ctxOne = null;
  function openCtx(id, x, y) {
    ctxOne = id;
    ctxIds = state.selected.has(id) && state.selected.size > 1 ? [...state.selected] : [id];
    const m = $("#ctx");
    m.hidden = false;
    m.style.left = Math.min(x, innerWidth - m.offsetWidth - 8) + "px";
    m.style.top = Math.min(y, innerHeight - m.offsetHeight - 8) + "px";
    // ver, programas, conversar e demonstração dizem respeito ao PC em que se clicou (não à seleção)
    for (const b of $$("[data-ctx=view],[data-ctx=procs],[data-ctx=demo],[data-ctx=chat]", m)) b.disabled = false;
  }
  $("#ctx").addEventListener("click", (e) => {
    const act = e.target.closest("button")?.dataset.ctx;
    if (!act) return;
    runAction(act, ["view", "procs", "demo", "chat"].includes(act) ? [ctxOne] : ctxIds);
  });

  // diálogo genérico com formulário
  function openDialog(sel, ids, onOk) {
    const d = $(sel);
    const form = $("form", d);
    form.reset();
    const t = $("[data-targets]", d);
    if (t) t.textContent = "Para: " + names(ids);
    d.returnValue = "";
    d.onclose = () => { if (d.returnValue === "ok") onOk(new FormData(form)); };
    d.showModal();
  }

  function openMessage(ids) {
    openDialog("#msgDialog", ids, (f) => {
      const text = String(f.get("text") || "").trim();
      if (!text) return;
      cmd("message", ids, { title: f.get("title"), text, allowReply: f.get("allowReply") === "on" });
      logGroup(new Set(ids), text);
    });
  }

  function openLock(ids) {
    openDialog("#lockDialog", ids, (f) => {
      cmd("lock", ids, { text: String(f.get("text") || ""), color: f.get("color"), seconds: +(f.get("seconds") || 0) });
      toast(`🔒 Bloqueados: ${ids.size}`);
    });
  }
  document.addEventListener("change", (e) => {
    if (e.target.id === "endAction") $("#endLockWrap").hidden = e.target.value !== "lock";
    if (e.target.id === "noticeStyle") $("#cornerWrap").hidden = e.target.value !== "corner";
  });
  $("#lockPresets").addEventListener("click", (e) => {
    const b = e.target.closest(".chip");
    if (b) $("#lockDialog [name=text]").value = b.textContent;
  });

  // ---------------------------------------------------------- conversas por aluno + mãos no ar
  function thread(id) {
    let t = state.threads.get(id);
    if (!t) { t = { msgs: [], unread: 0, hand: false }; state.threads.set(id, t); }
    return t;
  }
  function onIncoming(id, user, text, hand) {
    const t = thread(id);
    t.msgs.push({ text, mine: false, hand, at: Date.now() });
    if (state.openThread === id) renderThread(id);
    else t.unread++;
    renderThreads();
    toast(`💬 ${user || id}: ${text.slice(0, 40)}`);
  }
  function onHand(id, user) {
    const t = thread(id);
    t.hand = true;
    t.msgs.push({ text: "✋ Levantou a mão", mine: false, hand: true, at: Date.now() });
    if (state.openThread === id) renderThread(id); else t.unread++;
    renderThreads();
    toast(`✋ ${user || id} levantou a mão`);
    const a = state.agents.get(id);
    if (a && !a.el.querySelector(".hand-badge")) {
      const b = document.createElement("div");
      b.className = "hand-badge";
      b.textContent = "✋";
      a.el.append(b);
    }
  }
  function clearHand(id) {
    thread(id).hand = false;
    state.agents.get(id)?.el.querySelector(".hand-badge")?.remove();
    renderThreads();
  }
  function sendChat(id, text) {
    cmd("chat", [id], { text });
    thread(id).msgs.push({ text, mine: true, at: Date.now() });
    renderThread(id);
  }
  function renderThreads() {
    const box = $("#threads");
    const entries = [...state.threads.entries()].filter(([id]) => state.agents.has(id) || thread(id).msgs.length);
    entries.sort((a, b) => (b[1].hand - a[1].hand) || ((b[1].msgs.at(-1)?.at || 0) - (a[1].msgs.at(-1)?.at || 0)));
    box.innerHTML = entries.map(([id, t]) => {
      const a = state.agents.get(id);
      const name = a?.info.hostname || id;
      const last = t.msgs.at(-1);
      return `<li data-thread="${esc(id)}" class="${t.hand ? "hand" : ""}">
        <span class="av">${esc((a?.info.user || name).slice(0, 1).toUpperCase())}</span>
        <span class="who"><b>${t.hand ? "✋ " : ""}${esc(name)}</b><span>${esc(last ? (last.mine ? "Eu: " : "") + last.text : "")}</span></span>
        ${t.unread ? `<span class="dot"></span>` : ""}</li>`;
    }).join("") || `<li class="muted" style="background:none;cursor:default">Sem conversas. Clica num PC com o botão direito → Conversar, ou espera que um aluno escreva.</li>`;
  }
  function openThread(id) {
    state.openThread = id;
    thread(id).unread = 0;
    clearHand(id);
    $("#threads").hidden = true;
    $("#thread").hidden = false;
    $("#chatBack").hidden = false;
    const a = state.agents.get(id);
    $("#sideTitle").textContent = `💬 ${a?.info.hostname || id}`;
    renderThread(id);
    $("#chatForm text") && ($("#chatForm [name=text]").value = "");
    $("#chatForm [name=text]").focus();
  }
  function renderThread(id) {
    const t = thread(id);
    $("#chat").innerHTML = t.msgs.map((m) => `<li class="${m.mine ? "out" : ""} ${m.hand ? "hand" : ""}">
      <div class="h">${new Date(m.at).toLocaleTimeString("pt-PT", { hour: "2-digit", minute: "2-digit" })} · ${m.mine ? "Eu" : esc(state.agents.get(id)?.info.user || id)}</div>
      <div class="t">${esc(m.text)}</div></li>`).join("");
    $("#chat").scrollTop = $("#chat").scrollHeight;
  }
  function closeThread() {
    state.openThread = null;
    $("#thread").hidden = true;
    $("#threads").hidden = false;
    $("#chatBack").hidden = true;
    $("#sideTitle").textContent = "💬 Mensagens e mãos no ar";
    renderThreads();
  }
  $("#chatBack").addEventListener("click", closeThread);
  $("#threads").addEventListener("click", (e) => {
    const id = e.target.closest("[data-thread]")?.dataset.thread;
    if (id) openThread(id);
  });
  $("#chatForm").addEventListener("submit", (e) => {
    e.preventDefault();
    const inp = e.target.elements.text;
    const text = inp.value.trim();
    if (text && state.openThread) { sendChat(state.openThread, text); inp.value = ""; }
  });
  // mensagens enviadas em grupo (diálogo "Mensagem") -> entram na conversa de cada um
  function logGroup(ids, text) { for (const id of ids) { thread(id).msgs.push({ text, mine: true, at: Date.now() }); } renderThreads(); if (state.openThread && ids.has?.(state.openThread)) renderThread(state.openThread); }
  function addChat() {}

  // ---------------------------------------------------------- programas abertos
  function openProcs(id) {
    state.procsFor = id;
    $("#procsDialog [data-targets]").textContent = state.agents.get(id)?.info.hostname || id;
    $("#procsBody").innerHTML = `<tr><td colspan="3" class="muted">A pedir a lista…</td></tr>`;
    $("#procsDialog").showModal();
    cmd("procs", [id]);
  }
  function renderProcs(list) {
    list.sort((a, b) => natural(a.name, b.name));
    $("#procsBody").innerHTML = list.length
      ? list.map((p) => `<tr><td><strong>${esc(p.name)}</strong></td><td>${esc(p.title)}</td>
          <td><button class="danger" data-kill="${p.pid}">✕ Fechar</button></td></tr>`).join("")
      : `<tr><td colspan="3" class="muted">Nenhum programa com janela aberta.</td></tr>`;
  }
  $("#procsBody").addEventListener("click", (e) => {
    const pid = e.target.closest("[data-kill]")?.dataset.kill;
    if (pid && state.procsFor) { cmd("kill", [state.procsFor], { pid: +pid }); toast("A fechar o programa…"); }
  });
  $("#procsRefresh").addEventListener("click", () => state.procsFor && cmd("procs", [state.procsFor]));
  $("#procsClose").addEventListener("click", () => $("#procsDialog").close());
  $("#procsDialog").addEventListener("close", () => (state.procsFor = null));

  // ================================================================== partilha do ecrã do professor
  $("#shareBtn").addEventListener("click", startShare);
  $("#shareStopBtn").addEventListener("click", stopShare);

  async function startShare() {
    if (!navigator.mediaDevices?.getDisplayMedia) return alert("Este browser não permite partilhar o ecrã. Usa Chrome, Edge, Firefox ou Safari, com https://");
    const targets = new Set(state.selected);
    let stream;
    try { stream = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: 10 }, audio: false }); } catch { return; }
    const video = $("#shareVideo"), canvas = $("#shareCanvas"), ctx = canvas.getContext("2d");
    video.srcObject = stream;
    await video.play();
    const saving = $("#netMode").value !== "0";
    const fps = saving ? 3 : 6, maxW = saving ? 1280 : 1920, q = saving ? 0.5 : 0.65;
    let busy = false;
    const timer = setInterval(() => {
      const ws = state.ws;
      if (busy || !video.videoWidth || !ws || ws.readyState !== WebSocket.OPEN || ws.bufferedAmount > 1_000_000) return;
      const scale = Math.min(1, maxW / video.videoWidth);
      canvas.width = Math.round(video.videoWidth * scale);
      canvas.height = Math.round(video.videoHeight * scale);
      ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
      busy = true;
      canvas.toBlob(async (blob) => {
        try {
          if (!blob) return;
          const body = new Uint8Array(await blob.arrayBuffer());
          const pkt = new Uint8Array(body.length + 1);
          pkt[0] = SHARE;
          pkt.set(body, 1);
          ws.send(pkt);
        } finally { busy = false; }
      }, "image/jpeg", q);
    }, 1000 / fps);
    stream.getVideoTracks()[0].addEventListener("ended", stopShare);
    state.share = { stream, timer, targets };
    cmd("broadcastStart", targets, { fullscreen: true });
    toast("📺 A partilhar o teu ecrã");
    refresh();
  }

  function stopShare() {
    const s = state.share;
    if (!s) return;
    state.share = null;
    clearInterval(s.timer);
    s.stream.getTracks().forEach((t) => t.stop());
    if (s.targets.size) cmd("broadcastStop", s.targets);
    refresh();
  }

  // ================================================================== ver em grande / controlo remoto
  const viewer = $("#viewer"), vimg = $("#viewerImg");

  function openViewer(id) {
    if (state.viewing) closeViewer();
    const a = state.agents.get(id);
    if (!a) return;
    state.viewing = id;
    $("#viewerTitle").textContent = `${a.info.hostname || id} — ${a.info.user || "sem sessão"} · ${a.info.room}`;
    $("#controlToggle").checked = false;
    $("#viewerKeys").hidden = true;
    vimg.classList.remove("control");
    vimg.removeAttribute("src");
    $("#viewerWait").hidden = false;
    cmd("watch", [id]);
    viewer.showModal();
  }
  function closeViewer() {
    if (!state.viewing) return;
    cmd("unwatch", [state.viewing]);
    state.viewing = null;
    if (state.viewerUrl) { URL.revokeObjectURL(state.viewerUrl); state.viewerUrl = null; }
    if (document.fullscreenElement) document.exitFullscreen().catch(() => {});
    if (viewer.open) viewer.close();
  }
  $("#viewerClose").addEventListener("click", closeViewer);
  viewer.addEventListener("close", closeViewer);
  viewer.addEventListener("cancel", (e) => { if ($("#controlToggle").checked) e.preventDefault(); });
  $("#viewerLock").addEventListener("click", () => state.viewing && openLock([state.viewing]));
  $("#viewerMsg").addEventListener("click", () => state.viewing && openMessage([state.viewing]));
  $("#viewerProcs").addEventListener("click", () => state.viewing && openProcs(state.viewing));
  $("#viewerType").addEventListener("click", () => state.viewing && runAction("type", [state.viewing]));
  // ecrã inteiro (Safari no Mac usa os nomes "webkit"); duplo clique na imagem também alterna
  function toggleViewerFull() {
    const st = $("#viewerStage");
    const cur = document.fullscreenElement || document.webkitFullscreenElement;
    try {
      if (cur) (document.exitFullscreen || document.webkitExitFullscreen).call(document);
      else {
        const r = (st.requestFullscreen || st.webkitRequestFullscreen).call(st);
        if (r && r.catch) r.catch(() => toast("O browser não deixou abrir em ecrã inteiro."));
      }
    } catch { toast("O browser não deixou abrir em ecrã inteiro."); }
  }
  $("#viewerFull").addEventListener("click", toggleViewerFull);
  vimg.addEventListener("dblclick", () => { if (!vimg.classList.contains("control")) toggleViewerFull(); });
  $("#viewerSave").addEventListener("click", () => {
    if (!state.viewerUrl) return;
    const a = document.createElement("a");
    const host = state.agents.get(state.viewing)?.info.hostname || "ecra";
    a.href = state.viewerUrl;
    a.download = `${host}-${new Date().toISOString().slice(0, 19).replace(/[:T]/g, "-")}.jpg`;
    a.click();
  });
  $("#controlToggle").addEventListener("change", (e) => {
    vimg.classList.toggle("control", e.target.checked);
    $("#viewerKeys").hidden = !e.target.checked;
    if (e.target.checked) vimg.focus();
  });
  $("#viewerKeys").addEventListener("click", (e) => {
    const k = e.target.closest("[data-keys]")?.dataset.keys;
    if (!k || !state.viewing) return;
    const vks = k.split("+").map(Number);
    for (const vk of vks) input({ ev: "key", vk, down: true });
    for (const vk of [...vks].reverse()) input({ ev: "key", vk, down: false });
    vimg.focus();
  });

  const controlling = () => state.viewing && $("#controlToggle").checked;
  const input = (o) => cmd("input", [state.viewing], o);
  function pos(e) {
    const r = vimg.getBoundingClientRect();
    const iw = vimg.naturalWidth || r.width, ih = vimg.naturalHeight || r.height;
    const s = Math.min(r.width / iw, r.height / ih);
    const w = iw * s, h = ih * s;
    const left = r.left + (r.width - w) / 2, top = r.top + (r.height - h) / 2;
    return { x: Math.min(1, Math.max(0, (e.clientX - left) / w)), y: Math.min(1, Math.max(0, (e.clientY - top) / h)) };
  }
  let lastMove = 0;
  vimg.addEventListener("mousemove", (e) => {
    if (!controlling()) return;
    const now = performance.now();
    if (now - lastMove < 33) return;
    lastMove = now;
    input({ ev: "move", ...pos(e) });
  });
  vimg.addEventListener("mousedown", (e) => { if (!controlling()) return; e.preventDefault(); vimg.focus(); input({ ev: "down", button: e.button, ...pos(e) }); });
  vimg.addEventListener("mouseup", (e) => { if (!controlling()) return; e.preventDefault(); input({ ev: "up", button: e.button, ...pos(e) }); });
  vimg.addEventListener("contextmenu", (e) => { if (controlling()) e.preventDefault(); });
  vimg.addEventListener("wheel", (e) => { if (!controlling()) return; e.preventDefault(); input({ ev: "wheel", delta: Math.round(-e.deltaY), ...pos(e) }); }, { passive: false });
  for (const type of ["keydown", "keyup"]) {
    document.addEventListener(type, (e) => {
      if (!controlling() || document.activeElement !== vimg) return;
      const vk = window.VK[e.code];
      if (!vk) return;
      e.preventDefault();
      input({ ev: "key", vk, down: type === "keydown" });
    });
  }

  // ================================================================== palavra-passe
  function openPassword() {
    const d = $("#pwDialog"), form = $("form", d);
    form.reset();
    $("[data-error]", d).textContent = "";
    d.returnValue = "";
    form.onsubmit = async (e) => {
      if (e.submitter?.value !== "ok") return;
      e.preventDefault();
      const f = new FormData(form);
      if (f.get("next") !== f.get("next2")) return ($("[data-error]", d).textContent = "As palavras-passe novas não são iguais.");
      try {
        await api("/api/me/password", { method: "POST", body: { current: f.get("current"), next: f.get("next") } });
        d.close();
        toast("🔑 Palavra-passe alterada");
      } catch (err) { $("[data-error]", d).textContent = err.message; }
    };
    d.showModal();
  }

  // ================================================================== utilizadores (administradores)
  let users = [], editing = null;

  async function openUsers() {
    try {
      [users] = await Promise.all([api("/api/users"), loadCatalog()]);
    } catch (e) { return toast(e.message); }
    renderUsers();
    $("#userForm").hidden = true;
    $("#usersDialog").showModal();
  }

  function accessText(u) {
    if (u.isAdmin) return "⭐ Administrador";
    if (u.rooms.includes("*")) return "Todas as salas";
    return u.rooms.map((r) => r.endsWith("/*") ? `🏫 ${schoolName(r.slice(0, -2))}` : r.split("/").pop()).join(", ") || "— sem acesso —";
  }

  function renderUsers() {
    $("#usersBody").innerHTML = users.map((u) => `<tr data-user="${esc(u.username)}" class="${editing === u.username ? "sel" : ""}">
      <td><strong>${esc(u.displayName || u.username)}</strong><br><span class="muted small">${esc(u.username)}</span></td>
      <td class="small">${esc(accessText(u))}</td></tr>`).join("");
  }

  $("#usersBody").addEventListener("click", (e) => {
    const name = e.target.closest("[data-user]")?.dataset.user;
    if (name) editUser(users.find((u) => u.username === name));
  });
  $("#userNew").addEventListener("click", () => editUser(null));
  $("#userCancel").addEventListener("click", () => { $("#userForm").hidden = true; editing = null; renderUsers(); });
  $("#usersClose").addEventListener("click", () => $("#usersDialog").close());

  function editUser(u) {
    editing = u?.username ?? null;
    const f = $("#userForm");
    f.reset();
    f.hidden = false;
    $("[data-error]", f).textContent = "";
    $("#userFormTitle").textContent = u ? `Editar ${u.displayName || u.username}` : "Novo colega";
    f.username.value = u?.username ?? "";
    f.username.readOnly = !!u;
    f.displayName.value = u?.displayName ?? "";
    f.password.placeholder = u ? "(deixa vazio para manter)" : "mínimo 8 caracteres";
    f.password.required = !u;
    f.isAdmin.checked = !!u?.isAdmin;
    $("#userDelete").hidden = !u || u.username === state.me.username;
    renderRoomsPick(new Set(u?.rooms ?? []));
    syncAdmin();
    renderUsers();
  }

  function renderRoomsPick(chosen) {
    const box = $("#roomsPick");
    const rooms = allRooms();
    let html = `<legend>Salas a que tem acesso</legend>
      <label class="check"><input type="checkbox" value="*" ${chosen.has("*") ? "checked" : ""}> 🌍 Todas as escolas e salas</label>`;
    for (const s of state.catalog.schools) {
      html += `<label class="check school"><input type="checkbox" value="${esc(s.id)}/*" ${chosen.has(s.id + "/*") ? "checked" : ""}> 🏫 ${esc(s.name)} (toda a escola)</label>`;
      for (const r of rooms.filter((r) => r.school === s.id).sort((a, b) => natural(a.name, b.name)))
        html += `<label class="check room"><input type="checkbox" value="${esc(r.key)}" ${chosen.has(r.key) ? "checked" : ""}> ${esc(r.name)}</label>`;
    }
    // salas guardadas que já não existem no catálogo
    for (const c of chosen) if (c !== "*" && !c.endsWith("/*") && !rooms.some((r) => r.key === c))
      html += `<label class="check room"><input type="checkbox" value="${esc(c)}" checked> ${esc(c)}</label>`;
    box.innerHTML = html;
  }

  function syncAdmin() {
    const f = $("#userForm");
    $("#roomsPick").disabled = f.isAdmin.checked;
  }
  $("#userForm").addEventListener("change", (e) => { if (e.target.name === "isAdmin") syncAdmin(); });

  $("#userForm").addEventListener("submit", async (e) => {
    e.preventDefault();
    const f = e.target;
    const rooms = $$("#roomsPick input:checked").map((i) => i.value);
    try {
      await api("/api/users", {
        method: "POST",
        body: {
          username: f.username.value.trim(),
          displayName: f.displayName.value.trim(),
          password: f.password.value || null,
          isAdmin: f.isAdmin.checked,
          rooms,
          create: !editing,
        },
      });
      users = await api("/api/users");
      editing = f.username.value.trim();
      renderUsers();
      toast("👥 Guardado");
      f.password.value = "";
    } catch (err) { $("[data-error]", f).textContent = err.message; }
  });

  $("#userDelete").addEventListener("click", async () => {
    if (!editing || !confirm(`Apagar a conta "${editing}"?`)) return;
    try {
      await api("/api/users/" + encodeURIComponent(editing), { method: "DELETE" });
      users = await api("/api/users");
      editing = null;
      $("#userForm").hidden = true;
      renderUsers();
      toast("Conta apagada");
    } catch (err) { $("[data-error]", $("#userForm")).textContent = err.message; }
  });

  // ================================================================== escolas e salas (administradores)
  let roomsData = null, roomsTab = null;

  async function openRooms() {
    let res;
    try { res = await api("/api/admin/rooms"); } catch (e) { return toast(e.message); }
    roomsData = res.view || res;
    roomsData.counts = res.counts || {};
    const ids = roomsData.schools.map((s) => s.id);
    for (const f of roomsData.folders) if (!ids.includes(f.school)) { ids.push(f.school); roomsData.schools.push({ id: f.school, name: f.school }); }
    roomsTab = ids[0];
    $("#roomsAdWarn").hidden = roomsData.adOk;
    $("#roomsAdWarn").textContent = `Não consegui ler as pastas do AD (${roomsData.adBase}). Só aparecem as salas já configuradas.`;
    $("[data-error]", $("#roomsDialog")).textContent = "";
    renderRoomsTabs();
    $("#roomsDialog").showModal();
  }

  function renderRoomsTabs() {
    $("#schoolTabs").innerHTML = roomsData.schools.map((s) =>
      `<button data-tab="${esc(s.id)}" class="${s.id === roomsTab ? "active" : ""}">🏫 ${esc(s.id)}</button>`).join("");
    const school = roomsData.schools.find((s) => s.id === roomsTab);
    $("#schoolName").value = school?.name ?? "";
    const rows = roomsData.folders.filter((f) => f.school === roomsTab).sort((a, b) => natural(a.folder, b.folder));
    $("#roomNames").innerHTML = [...new Set(rows.map((r) => r.name).filter(Boolean))].map((n) => `<option value="${esc(n)}">`).join("");
    $("#roomsBody").innerHTML = rows.length ? rows.map((r) => `<tr data-folder="${esc(r.folder)}">
        <td>${esc(r.folder)} ${r.inAd ? "" : '<span class="tag-ad">(não está no AD)</span>'}</td>
        <td>${r.count || ""}</td>
        <td>${(() => { const c = roomsData.counts[`${r.school}/${r.folder}`]; return c ? `<span class="st-online">${c.online}</span> · ${c.seen}` : ""; })()}</td>
        <td><input data-f="name" list="roomNames" value="${esc(r.name)}" placeholder="${esc(r.folder)}"></td>
        <td><input data-f="active" type="checkbox" ${r.active ? "checked" : ""}></td>
        <td>${r.inAd ? `<button type="button" data-status="${esc(r.folder)}" title="Ver os PCs desta sala">👁 PCs</button>` : ""}</td></tr>`).join("")
      : `<tr><td colspan="6" class="muted">Sem pastas nesta escola.</td></tr>`;
  }

  $("#schoolTabs").addEventListener("click", (e) => {
    const t = e.target.closest("[data-tab]")?.dataset.tab;
    if (t) { roomsTab = t; renderRoomsTabs(); }
  });
  $("#schoolName").addEventListener("input", (e) => {
    const s = roomsData.schools.find((s) => s.id === roomsTab);
    if (s) s.name = e.target.value;
  });
  $("#roomsBody").addEventListener("input", (e) => {
    const tr = e.target.closest("[data-folder]");
    if (!tr) return;
    const row = roomsData.folders.find((f) => f.school === roomsTab && f.folder === tr.dataset.folder);
    if (e.target.dataset.f === "name") row.name = e.target.value;
    if (e.target.dataset.f === "active") row.active = e.target.checked;
  });
  $("#roomsBody").addEventListener("change", (e) => {
    if (e.target.dataset.f !== "active") return;
    const tr = e.target.closest("[data-folder]");
    roomsData.folders.find((f) => f.school === roomsTab && f.folder === tr.dataset.folder).active = e.target.checked;
  });
  $("#roomsCancel").addEventListener("click", () => $("#roomsDialog").close());

  // ---------------------------------------------------------- PCs de uma sala (o que falta?)
  $("#roomsBody").addEventListener("click", (e) => {
    const f = e.target.closest("[data-status]")?.dataset.status;
    if (f) openRoomStatus(roomsTab, f);
  });
  async function openRoomStatus(school, folder) {
    let st;
    try { st = await api(`/api/admin/room-status?school=${encodeURIComponent(school)}&folder=${encodeURIComponent(folder)}`); }
    catch (e) { return toast(e.message); }
    $("#rsTitle").textContent = `${schoolName(school)} — ${folder}`;
    $("#rsSummary").textContent = `${st.total} PCs no AD · ${st.online} ligados agora · ${st.seen} já tiveram o agente · ${st.total - st.seen} nunca apareceram`;
    const label = { online: "🟢 Ligado", seen: "🔵 Tem o agente (desligado)", never: "🟠 Nunca apareceu" };
    const order = { never: 0, seen: 1, online: 2 };
    st.rows.sort((a, b) => order[a.state] - order[b.state] || natural(a.name, b.name));
    $("#rsBody").innerHTML = st.rows.map((x) => `<tr>
      <td class="st-${x.state}">${label[x.state]}</td>
      <td>${esc(x.name)}<br><span class="muted small">${esc(x.mac || "sem MAC")}</span></td>
      <td>${esc(x.pc || "—")}</td><td>${esc(x.user || "—")}</td>
      <td class="small">${x.lastSeen ? new Date(x.lastSeen).toLocaleString("pt-PT") : "—"}</td>
      <td class="small">${esc(x.version || "")}</td></tr>`).join("") || `<tr><td colspan="6" class="muted">Sem contas nesta pasta.</td></tr>`;
    $("#roomStatusDialog").showModal();
  }
  $("#rsClose").addEventListener("click", () => $("#roomStatusDialog").close());

  // ---------------------------------------------------------- sistema e instalação
  let sysFilter = null, sysTimer = null;
  async function openSystem() {
    await loadSystem();
    $("#systemDialog").showModal();
    clearInterval(sysTimer);
    sysTimer = setInterval(() => { if ($("#systemDialog").open) loadSystem(); else clearInterval(sysTimer); }, 15000);
  }
  async function loadSystem() {
    let s;
    try { s = await api("/api/admin/system"); } catch (e) { return toast(e.message); }
    const dt = (v) => v ? new Date(v).toLocaleString("pt-PT") : "—";
    $("#sysUpdate").innerHTML = `<dl class="kv">
      <dt>Versão</dt><dd><strong>v${esc(s.version)}</strong> ${s.commit ? `<span class="muted">(${esc(s.commit)})</span>` : ""}</dd>
      <dt>Compilada em</dt><dd>${dt(s.buildDate)}</dd>
      <dt>A correr desde</dt><dd>${dt(s.startedAt)}</dd>
      <dt>Atualização automática</dt><dd>${s.autoUpdate ? `ligada · reinicia só entre ${esc(s.window.replace("-", " e "))} e sem consolas abertas` : "desligada"}</dd>
      <dt>Última verificação</dt><dd>${dt(s.lastCheck)}</dd>
      <dt>Resultado</dt><dd class="${s.lastCheck ? (s.lastOk ? "ok" : "bad") : ""}">${esc(s.lastResult)}</dd>
      <dt>Agentes ligados</dt><dd>${s.agents.total} PCs · ${s.agents.outdated ? `<span class="bad">${s.agents.outdated} a atualizar</span>` : `<span class="ok">todos na versão atual</span>`}</dd></dl>
      <div class="settings-row">
        <label class="check"><input type="checkbox" id="setAuto" ${s.autoUpdate ? "checked" : ""}> Atualizar automaticamente</label>
        <label class="inline">entre <input id="setWin" value="${esc(s.window)}" size="11" placeholder="20:00-07:00"></label>
        <button id="setSave">Guardar</button>
      </div>`;
    const t = s.installTask;
    $("#sysInstall").innerHTML = !t.exists
      ? `<p class="warn">Falta criar a tarefa de instalação no servidor (uma vez): na pasta do agente, <code>.\\criar-tarefa-instalar.ps1</code>.</p>`
      : `<p>${t.running ? "⏳ <strong>A instalar agora…</strong> (atualiza sozinho)" : "Pronto."} ${t.lastRun ? `<span class="muted">Última vez: ${esc(t.lastRun)}</span>` : ""}</p>`;
    $("#sysInstallNow").disabled = !t.exists || t.running;
    renderReport(s.report);
    state.pcReport = s.pcReport; renderPcReport();
  }
  function renderReport(rep) {
    if (!rep) { $("#sysReport").innerHTML = `<p class="muted">Ainda não há relatórios de instalação.</p>`; return; }
    const rows = sysFilter ? rep.rows.filter((r) => categoryOf(r.result) === sysFilter) : rep.rows;
    $("#sysReport").innerHTML = `<p class="muted small">Último relatório: ${new Date(rep.time).toLocaleString("pt-PT")} · ${rep.total} PCs verificados</p>
      <div class="report-summary">${rep.summary.map((g) => `<button type="button" class="chip ${sysFilter === g.category ? "on" : ""}" data-cat="${esc(g.category)}">${esc(g.category)}: ${g.count}</button>`).join("")}</div>
      <table class="table"><thead><tr><th>PC</th><th>Resultado</th></tr></thead><tbody>${rows.slice(0, 300).map((r) =>
        `<tr><td>${esc(r.pc)}</td><td class="small">${esc(r.result)}</td></tr>`).join("")}</tbody></table>`;
    state.lastReport = rep;
  }
  function renderPcReport() {
    const rep = state.pcReport, el = $("#sysPcReport");
    if (!rep) { el.innerHTML = ""; return; }
    if (rep.missing) {
      el.innerHTML = `<p class="warn small">Ainda não está ativado. No servidor, na pasta do agente: <code>.\\ativar-relatorios.ps1</code></p>`;
      return;
    }
    const q = ($("#pcRepSearch").value || "").trim().toLowerCase();
    const rows = q ? rep.rows.filter((r) => r.pc.toLowerCase().includes(q)) : rep.rows;
    el.innerHTML = `<div class="report-summary">${rep.summary.map((g) => `<span class="chip">${esc(g.category)}: ${g.count}</span>`).join("")}</div>
      <table class="table"><thead><tr><th>PC</th><th>Quando</th><th>Resultado</th></tr></thead><tbody>${rows.slice(0, 300).map((r) =>
        `<tr><td>${esc(r.pc)}</td><td class="small">${new Date(r.time).toLocaleString("pt-PT")}</td><td class="small">${esc(r.result)}</td></tr>`).join("") ||
        `<tr><td colspan="3" class="muted">Nenhum PC escreveu ainda.</td></tr>`}</tbody></table>`;
  }
  $("#pcRepSearch").addEventListener("input", renderPcReport);
  function categoryOf(r) {
    if (/^Instalado/i.test(r)) return "Instalado agora";
    if (/^Já instalado/i.test(r)) return "Já tinha o agente";
    if (/^Sem sala/i.test(r)) return "Não pertence a uma sala ativa";
    if (/desligado/i.test(r)) return "Desligado ou inacessível";
    if (/\.NET/i.test(r)) return "Falta o .NET Framework";
    if (/não está ativa/i.test(r)) return "Sala não ativa";
    return "Erro / outro";
  }
  $("#sysReport").addEventListener("click", (e) => {
    const c = e.target.closest("[data-cat]")?.dataset.cat;
    if (!c) return;
    sysFilter = sysFilter === c ? null : c;
    renderReport(state.lastReport);
  });
  $("#sysRefresh").addEventListener("click", loadSystem);
  $("#sysPen").addEventListener("click", () => {
    toast("⏳ A preparar o pacote (cerca de 150 MB)…");
    download("/api/admin/pen-package", "SalaViva-pen.zip");
  });
  $("#sysUpdate").addEventListener("click", async (e) => {
    if (e.target.id !== "setSave") return;
    try {
      await api("/api/admin/settings", { method: "POST", body: { autoUpdate: $("#setAuto").checked, window: $("#setWin").value.trim() } });
      toast("⚙️ Definições guardadas");
      loadSystem();
    } catch (err) { toast(err.message); }
  });
  $("#sysClose").addEventListener("click", () => { $("#systemDialog").close(); clearInterval(sysTimer); });
  $("#sysCheck").addEventListener("click", async () => {
    toast("A verificar no GitHub…");
    try { const r = await api("/api/admin/update", { method: "POST" }); toast(r.message); } catch (e) { toast(e.message); }
    loadSystem();
  });
  $("#sysUpdateNow").addEventListener("click", async () => {
    if (!confirm("Atualizar já? Se houver versão nova, o servidor reinicia dentro de ~1 minuto e a consola desliga-se durante esse tempo.")) return;
    toast("⏳ A descarregar a versão nova do GitHub… (pode demorar 1–2 min)");
    try { const r = await api("/api/admin/update?now=1", { method: "POST" }); toast(r.message); loadSystem(); } catch (e) { toast(e.message); }
    loadSystem();
  });
  $("#sysInstallNow").addEventListener("click", async () => {
    if (!confirm("Instalar agora em todos os PCs ligados das salas ativas?\nCorre em segundo plano (pode demorar 30-60 min).")) return;
    try { await api("/api/admin/install-now", { method: "POST" }); toast("▶ Instalação iniciada"); } catch (e) { toast(e.message); }
    setTimeout(loadSystem, 3000);
  });
  $("#roomsSave").addEventListener("click", async () => {
    try {
      await api("/api/admin/rooms", {
        method: "POST",
        body: {
          schools: roomsData.schools,
          folders: roomsData.folders.map((f) => ({ school: f.school, folder: f.folder, name: f.name, active: f.active })),
        },
      });
      $("#roomsDialog").close();
      toast("🏫 Salas guardadas");
      loadCatalog();
    } catch (err) { $("[data-error]", $("#roomsDialog")).textContent = err.message; }
  });

  // ================================================================== enviar ficheiro
  let sendTargets = null;
  function pickAndSend(ids) {
    sendTargets = new Set(ids);
    $("#filePick").value = "";
    $("#filePick").click();
  }
  $("#filePick").addEventListener("change", async (e) => {
    const file = e.target.files[0];
    if (!file || !sendTargets) return;
    if (file.size > 100 * 1024 * 1024) return toast("Ficheiro demasiado grande (máximo 100 MB)");
    toast(`📁 A enviar "${file.name}"…`);
    try {
      const r = await fetch("/api/files", {
        method: "POST",
        headers: { Authorization: "Bearer " + state.token, "X-File-Name": encodeURIComponent(file.name) },
        body: file,
      });
      if (!r.ok) throw new Error(`Erro ${r.status}`);
      const j = await r.json();
      cmd("sendFile", sendTargets, { fileId: j.id });
      toast(`📁 "${file.name}" enviado para ${sendTargets.size} PC(s)`);
    } catch (err) { toast("Falhou o envio: " + err.message); }
  });

  // ================================================================== recolher trabalhos
  function onCollected(m) {
    const who = m.user || m.id;
    toast(m.file ? `📥 Recebido o trabalho de ${who}` : `📥 ${who}: ${m.error}`);
    if ($("#entregasDialog").open) loadEntregas();
  }
  $("#entregasBtn").addEventListener("click", () => { loadEntregas(); $("#entregasDialog").showModal(); });
  $("#entregasRefresh").addEventListener("click", loadEntregas);
  $("#entregasClose").addEventListener("click", () => $("#entregasDialog").close());

  async function loadEntregas() {
    let list = [];
    try { list = await api("/api/entregas"); } catch (e) { return toast(e.message); }
    const kb = (n) => n > 1048576 ? (n / 1048576).toFixed(1) + " MB" : Math.ceil(n / 1024) + " KB";
    $("#entregasBody").innerHTML = list.length ? list.map((b) => `<div class="entrega-batch">
        <h3>📦 ${esc(b.batch.replace("_", " às ").replace(/-(\d\d)$/, "h$1"))} <button data-batch="${esc(b.batch)}">⬇ Descarregar tudo (.zip)</button></h3>
        <ul>${b.files.map((f) => `<li><a data-file="${esc(f.file)}">${esc(f.file.split("/").pop())}</a> <span class="muted small">${kb(f.size)}</span></li>`).join("") || "<li class='muted'>Sem entregas.</li>"}</ul>
      </div>`).join("") : `<p class="muted">Ainda não recolheste trabalhos.</p>`;
  }
  $("#entregasBody").addEventListener("click", (e) => {
    const f = e.target.closest("[data-file]")?.dataset.file;
    const b = e.target.closest("[data-batch]")?.dataset.batch;
    if (f) download(`/api/entregas/download?file=${encodeURIComponent(f)}`, f.split("/").pop());
    if (b) download(`/api/entregas/download?batch=${encodeURIComponent(b)}`, `entregas-${b}.zip`);
  });
  async function download(url, name) {
    try {
      const r = await fetch(url, { headers: { Authorization: "Bearer " + state.token } });
      if (!r.ok) throw new Error(`Erro ${r.status}`);
      const blob = await r.blob();
      const a = document.createElement("a");
      a.href = URL.createObjectURL(blob);
      a.download = name;
      a.click();
      setTimeout(() => URL.revokeObjectURL(a.href), 5000);
    } catch (err) { toast("Falhou: " + err.message); }
  }

  // ================================================================== pergunta rápida
  const BAR_COLORS = ["#4f6bff", "#12a372", "#ffad1f", "#e5394f", "#6a3dff", "#00b8a3", "#ff5a6a", "#2b2f44"];
  state.poll = null;
  function openPoll(ids) {
    openDialog("#pollDialog", ids, (f) => {
      const options = String(f.get("options")).split("\n").map((o) => o.trim()).filter(Boolean).slice(0, 8);
      if (options.length < 2) return toast("Indica pelo menos duas respostas");
      cmd("poll", ids, { question: String(f.get("question")), options });
    });
  }
  $("#pollPresets").addEventListener("click", (e) => {
    const o = e.target.closest("[data-opts]")?.dataset.opts;
    if (o) $("#pollDialog [name=options]").value = o.split("|").join("\n");
  });
  function onPollStarted(m) {
    state.poll = { id: m.id, question: m.question, options: m.options, targets: new Set(m.targets), answers: new Map() };
    renderPoll();
    $("#pollResults").showModal();
  }
  function onAnswer(m) {
    if (!state.poll || m.poll !== state.poll.id) return;
    state.poll.answers.set(m.id, { user: m.user, choice: m.choice });
    renderPoll();
  }
  function renderPoll() {
    const p = state.poll;
    if (!p) return;
    $("#pollQ").textContent = p.question;
    const total = p.answers.size;
    $("#pollCount").textContent = `${total} de ${p.targets.size} responderam`;
    $("#pollBars").innerHTML = p.options.map((o, i) => {
      const n = [...p.answers.values()].filter((a) => a.choice === o).length;
      const pct = total ? Math.round((n / total) * 100) : 0;
      return `<div class="bar"><b>${esc(o)}</b><div class="track"><div class="fill" style="width:${pct}%;background:${BAR_COLORS[i % BAR_COLORS.length]}"></div></div><span>${n}</span></div>`;
    }).join("");
    $("#pollWho").innerHTML = [...p.answers.entries()].map(([id, a]) =>
      `<tr><td>${esc(a.user || id)}</td><td class="muted">${esc(state.agents.get(id)?.info.hostname || id)}</td><td><strong>${esc(a.choice)}</strong></td></tr>`).join("");
  }
  $("#pollHide").addEventListener("click", () => $("#pollResults").close());
  $("#pollEnd").addEventListener("click", () => {
    if (state.poll) cmd("pollClose", state.poll.targets, { poll: state.poll.id });
    $("#pollResults").close();
  });

  // ================================================================== mostrar o ecrã de um aluno à turma
  function startDemo(sourceId) {
    const viewers = visibleIds().filter((id) => id !== sourceId);
    if (!viewers.length) return toast("Não há outros PCs nesta sala para ver a demonstração");
    const src = state.agents.get(sourceId)?.info;
    if (!confirm(`Mostrar o ecrã de ${src ? personName(src) : sourceId} aos outros ${viewers.length} PC(s) desta sala?`)) return;
    cmd("demoStart", viewers, { source: sourceId });
    $("#demoStopBtn").hidden = false;
    toast("🎬 Demonstração iniciada");
  }
  $("#demoStopBtn").addEventListener("click", () => {
    cmd("demoStop", visibleIds().length ? visibleIds() : [...state.agents.keys()]);
    $("#demoStopBtn").hidden = true;
    toast("🎬 Demonstração terminada");
  });

  // ================================================================== exportar lista (presenças)
  $("#exportBtn").addEventListener("click", () => {
    const rows = [["Escola", "Sala", "PC", "Nome", "Conta", "IP", "Bloqueado", "Sem Internet", "Hora"]];
    const now = new Date().toLocaleString("pt-PT");
    for (const id of visibleIds()) {
      const i = state.agents.get(id).info;
      rows.push([schoolName(i.school), i.room, i.hostname, i.userName || "", i.user || "", i.ip, i.locked ? "sim" : "não", i.noInternet ? "sim" : "não", now]);
    }
    rows.sort((a, b) => natural(a[2], b[2]));
    const csv = "﻿" + rows.map((r) => r.map((c) => `"${String(c ?? "").replace(/"/g, '""')}"`).join(";")).join("\r\n");
    const a = document.createElement("a");
    a.href = URL.createObjectURL(new Blob([csv], { type: "text/csv;charset=utf-8" }));
    a.download = `presencas-${(state.filter || "todas").replace(/[\/*]/g, "_")}-${new Date().toISOString().slice(0, 16).replace(/[:T]/g, "-")}.csv`;
    a.click();
  });

  // ================================================================== arranque
  if (state.token) showConsole();
  window.addEventListener("beforeunload", stopShare);
})();

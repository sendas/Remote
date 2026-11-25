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
    for (const el of $$("[data-admin]")) el.hidden = !state.me.isAdmin;
    $("#zoom").value = store.get("zoom", "240");
    applyZoom();
    await loadCatalog();
    connect();
  }

  // ================================================================== menu do utilizador
  $("#userBtn").addEventListener("click", (e) => { e.stopPropagation(); $("#userMenu").hidden = !$("#userMenu").hidden; });
  document.addEventListener("click", () => { $("#userMenu").hidden = true; $("#ctx").hidden = true; });
  $("#userMenu").addEventListener("click", (e) => {
    const act = e.target.closest("button")?.dataset.act;
    if (act === "logout") logout();
    if (act === "password") openPassword();
    if (act === "users") openUsers();
    if (act === "rooms") openRooms();
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
    send({ type: "prefs", thumbMinIntervalMs: parseInt($("#netMode").value, 10), room: state.filter || null });
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
      case "reply": addChat(`${m.user || m.id} (${m.id})`, m.text, false); toast(`💬 Resposta de ${m.user || m.id}`); break;
      case "procs": if (m.id === state.procsFor) renderProcs(m.list || []); break;
      case "catalogChanged": loadCatalog(); break;
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
  $("#zoom").addEventListener("input", () => { store.set("zoom", $("#zoom").value); applyZoom(); });
  function applyZoom() { $("#grid").style.setProperty("--tile", $("#zoom").value + "px"); }

  function matches(info) {
    const f = state.filter;
    if (f) {
      if (f.endsWith("/*")) { if ((info.school || "") !== f.slice(0, -2)) return false; }
      else if ((info.key || `${info.school || ""}/${info.room}`) !== f) return false;
    }
    if (state.search) {
      const hay = `${info.hostname} ${info.user} ${info.id} ${info.room}`.toLowerCase();
      if (!hay.includes(state.search)) return false;
    }
    return true;
  }

  // ================================================================== grelha
  function upsertAgent(info) {
    let a = state.agents.get(info.id);
    if (!a) {
      const el = document.createElement("div");
      el.className = "pc";
      el.tabIndex = 0;
      el.innerHTML = `<div class="shot"><img alt="" hidden><span class="noimg">sem imagem</span></div>
        <div class="badges"></div><div class="meta"><span class="name"></span><span class="user"></span></div>`;
      el.addEventListener("click", (e) => { if (e.detail <= 1) toggleSelect(info.id, e.shiftKey || e.ctrlKey || e.metaKey); });
      el.addEventListener("dblclick", () => openViewer(info.id));
      el.addEventListener("keydown", (e) => { if (e.key === "Enter") openViewer(info.id); });
      el.addEventListener("contextmenu", (e) => { e.preventDefault(); openCtx(info.id, e.clientX, e.clientY); });
      a = { el, info, imgUrl: null };
      state.agents.set(info.id, a);
    }
    a.info = info;
    a.el.querySelector(".name").textContent = info.hostname || info.id;
    a.el.querySelector(".user").textContent = `${info.user || "sem sessão"} · ${info.room || "sem sala"}`;
    a.el.title = `${info.id} · ${info.ip} · ${info.os}\n${schoolName(info.school)} · ${info.room}`;
    a.el.dataset.key = info.hostname || info.id;
    const b = a.el.querySelector(".badges");
    b.innerHTML = (info.locked ? `<span class="badge lock">🔒 BLOQUEADO</span>` : "") +
      (info.broadcasting ? `<span class="badge share">📺 A VER</span>` : "");
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
        addChat(`🌐 Link → ${names(ids)}`, url, true);
      });
      case "type": return openDialog("#typeDialog", ids, (f) => {
        cmd("type", ids, { text: String(f.get("text")) });
        toast(`⌨ Texto enviado para ${ids.size} PC(s)`);
      });
      case "procs": return openProcs([...ids][0]);
      case "logoff":
      case "reboot":
      case "shutdown": {
        const label = { logoff: "Terminar a sessão em", reboot: "Reiniciar", shutdown: "Desligar" }[action];
        if (confirm(`${label} ${ids.size} computador(es)?\n${names(ids)}\n\nO trabalho não guardado dos alunos perde-se.`)) cmd(action, ids);
      }
    }
  }
  for (const b of $$("[data-cmd]")) b.addEventListener("click", () => runAction(b.dataset.cmd, state.selected));

  // menu de contexto (botão direito num PC)
  let ctxIds = [];
  function openCtx(id, x, y) {
    ctxIds = state.selected.has(id) && state.selected.size > 1 ? [...state.selected] : [id];
    const m = $("#ctx");
    m.hidden = false;
    m.style.left = Math.min(x, innerWidth - m.offsetWidth - 8) + "px";
    m.style.top = Math.min(y, innerHeight - m.offsetHeight - 8) + "px";
    for (const b of $$("[data-ctx=view],[data-ctx=procs]", m)) b.disabled = ctxIds.length > 1;
  }
  $("#ctx").addEventListener("click", (e) => {
    const act = e.target.closest("button")?.dataset.ctx;
    if (act) runAction(act, ctxIds);
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
      addChat(`Eu → ${names(ids)}`, text, true);
    });
  }

  function openLock(ids) {
    openDialog("#lockDialog", ids, (f) => {
      cmd("lock", ids, { text: String(f.get("text") || "") });
      toast(`🔒 Bloqueados: ${ids.size}`);
    });
  }
  $("#lockPresets").addEventListener("click", (e) => {
    const b = e.target.closest(".chip");
    if (b) $("#lockDialog [name=text]").value = b.textContent;
  });

  function addChat(head, text, outgoing) {
    const li = document.createElement("li");
    if (outgoing) li.className = "out";
    li.innerHTML = `<div class="h">${new Date().toLocaleTimeString("pt-PT", { hour: "2-digit", minute: "2-digit" })} · ${esc(head)}</div><div class="t">${esc(text)}</div>`;
    $("#chat").append(li);
    $("#chat").scrollTop = $("#chat").scrollHeight;
  }

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
    addChat(`📺 Partilha → ${names(targets)}`, "Iniciada", true);
    refresh();
  }

  function stopShare() {
    const s = state.share;
    if (!s) return;
    state.share = null;
    clearInterval(s.timer);
    s.stream.getTracks().forEach((t) => t.stop());
    if (s.targets.size) cmd("broadcastStop", s.targets);
    addChat("📺 Partilha", "Terminada", true);
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
  $("#viewerFull").addEventListener("click", () => $("#viewerStage").requestFullscreen?.().catch(() => {}));
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
    try { roomsData = await api("/api/admin/rooms"); } catch (e) { return toast(e.message); }
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
        <td><input data-f="name" list="roomNames" value="${esc(r.name)}" placeholder="${esc(r.folder)}"></td>
        <td><input data-f="active" type="checkbox" ${r.active ? "checked" : ""}></td></tr>`).join("")
      : `<tr><td colspan="4" class="muted">Sem pastas nesta escola.</td></tr>`;
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

  // ================================================================== arranque
  if (state.token) showConsole();
  window.addEventListener("beforeunload", stopShare);
})();

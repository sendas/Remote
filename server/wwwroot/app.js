"use strict";
(() => {
  const $ = (s) => document.querySelector(s);
  const THUMB = 0x01, FULL = 0x02, SHARE = 0x10;

  const state = {
    token: sessionStorage.getItem("token"),
    username: sessionStorage.getItem("username"),
    ws: null,
    agents: new Map(),       // id -> {info, el, imgUrl}
    selected: new Set(),
    room: "",
    viewing: null,           // id do PC aberto em grande
    viewerUrl: null,
    share: null,             // {stream, timer, targets:Set}
    reconnectDelay: 1000,
  };

  // ------------------------------------------------------------------ login
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
      state.token = j.token; state.username = j.username;
      sessionStorage.setItem("token", j.token);
      sessionStorage.setItem("username", j.username);
      showConsole();
    } catch {
      $("#loginError").textContent = "Sem ligação ao servidor.";
    }
  });

  $("#logoutBtn").addEventListener("click", async () => {
    stopShare();
    try { await fetch("/api/logout", { method: "POST", headers: { Authorization: "Bearer " + state.token } }); } catch {}
    sessionStorage.clear();
    location.reload();
  });

  function showConsole() {
    $("#login").hidden = true;
    $("#console").hidden = false;
    $("#who").textContent = state.username;
    connect();
  }

  // ------------------------------------------------------------------ WebSocket
  function connect() {
    const proto = location.protocol === "https:" ? "wss:" : "ws:";
    const ws = new WebSocket(`${proto}//${location.host}/ws/teacher?token=${encodeURIComponent(state.token)}`);
    ws.binaryType = "arraybuffer";
    state.ws = ws;
    setStatus("A ligar…", "");

    ws.onopen = () => {
      state.reconnectDelay = 1000;
      setStatus("Ligado", "ok");
      sendPrefs();
    };
    ws.onclose = (ev) => {
      if (state.ws !== ws) return;
      setStatus("Sem ligação — a tentar de novo…", "bad");
      // limpa a grelha: ao voltar recebemos a lista completa outra vez
      for (const id of [...state.agents.keys()]) removeAgent(id);
      setTimeout(async () => {
        // se o token expirou, o servidor recusa o upgrade; confirma antes de insistir
        try {
          const r = await fetch("/api/me", { headers: { Authorization: "Bearer " + state.token } });
          if (r.status === 401) { sessionStorage.clear(); location.reload(); return; }
        } catch {}
        connect();
      }, state.reconnectDelay);
      state.reconnectDelay = Math.min(state.reconnectDelay * 2, 15000);
    };
    ws.onmessage = (ev) => {
      if (typeof ev.data === "string") onJson(JSON.parse(ev.data));
      else onBinary(new Uint8Array(ev.data));
    };
    ws.onerror = () => {};
  }

  function send(obj) {
    if (state.ws && state.ws.readyState === WebSocket.OPEN) state.ws.send(JSON.stringify(obj));
  }
  function cmd(action, targets, extra = {}) {
    send({ type: "cmd", action, targets: [...targets], ...extra });
  }
  function sendPrefs() {
    send({ type: "prefs", thumbMinIntervalMs: parseInt($("#netMode").value, 10) });
  }
  $("#netMode").addEventListener("change", sendPrefs);

  function setStatus(text, cls) {
    const s = $("#status");
    s.textContent = text;
    s.className = "pill " + cls;
  }

  function onJson(m) {
    switch (m.type) {
      case "hello":
        for (const a of m.agents) upsertAgent(a);
        refresh();
        break;
      case "agentJoined":
      case "agentUpdate":
        upsertAgent(m.agent);
        refresh();
        break;
      case "agentLeft":
        removeAgent(m.id);
        refresh();
        break;
      case "reply":
        addChat(`${m.user || m.id} (${m.id})`, m.text, false);
        break;
    }
  }

  function onBinary(buf) {
    const kind = buf[0], idLen = buf[1];
    const id = new TextDecoder().decode(buf.subarray(2, 2 + idLen));
    const jpeg = buf.subarray(2 + idLen);
    const url = URL.createObjectURL(new Blob([jpeg], { type: "image/jpeg" }));
    if (kind === THUMB) {
      const a = state.agents.get(id);
      if (!a) { URL.revokeObjectURL(url); return; }
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
    } else {
      URL.revokeObjectURL(url);
    }
  }

  // ------------------------------------------------------------------ grelha de PCs
  function upsertAgent(info) {
    let a = state.agents.get(info.id);
    if (!a) {
      const el = document.createElement("div");
      el.className = "pc";
      el.tabIndex = 0;
      el.innerHTML = `
        <div class="shot"><img alt="" hidden><span class="noimg">sem imagem</span></div>
        <div class="badges"></div>
        <div class="meta"><span class="name"></span><span class="user"></span></div>`;
      el.addEventListener("click", (e) => { if (e.detail <= 1) toggleSelect(info.id, e.shiftKey || e.ctrlKey || e.metaKey); });
      el.addEventListener("dblclick", () => openViewer(info.id));
      el.addEventListener("keydown", (e) => { if (e.key === "Enter") openViewer(info.id); });
      a = { el, info, imgUrl: null };
      state.agents.set(info.id, a);
      insertSorted(el, info);
    }
    a.info = info;
    a.el.querySelector(".name").textContent = info.hostname || info.id;
    a.el.querySelector(".user").textContent = info.user || "(ninguém com sessão)";
    a.el.title = `${info.id} · ${info.ip} · ${info.os}${info.room ? " · sala " + info.room : ""}`;
    const b = a.el.querySelector(".badges");
    b.innerHTML = "";
    if (info.locked) b.insertAdjacentHTML("beforeend", `<span class="badge lock">BLOQUEADO</span>`);
    if (info.broadcasting) b.insertAdjacentHTML("beforeend", `<span class="badge share">A VER O PROFESSOR</span>`);
  }

  function insertSorted(el, info) {
    const grid = $("#grid");
    const key = (info.hostname || info.id).toLowerCase();
    const after = [...grid.children].find((c) => c.dataset.key > key);
    el.dataset.key = key;
    grid.insertBefore(el, after || null);
  }

  function removeAgent(id) {
    const a = state.agents.get(id);
    if (!a) return;
    if (a.imgUrl) URL.revokeObjectURL(a.imgUrl);
    a.el.remove();
    state.agents.delete(id);
    state.selected.delete(id);
    if (state.share) state.share.targets.delete(id);
    if (state.viewing === id) closeViewer();
  }

  function visibleIds() {
    return [...state.agents.values()]
      .filter((a) => !state.room || a.info.room === state.room)
      .map((a) => a.info.id);
  }

  function refresh() {
    // salas
    const rooms = [...new Set([...state.agents.values()].map((a) => a.info.room).filter(Boolean))].sort();
    const sel = $("#roomFilter");
    const cur = sel.value;
    const opts = ["", ...rooms];
    if (sel.options.length !== opts.length || opts.some((r, i) => sel.options[i].value !== r)) {
      sel.innerHTML = "";
      for (const r of opts) sel.add(new Option(r || "Todas", r));
      sel.value = opts.includes(cur) ? cur : "";
    }
    // visibilidade e seleção
    const vis = new Set(visibleIds());
    for (const [id, a] of state.agents) {
      a.el.hidden = !vis.has(id);
      if (!vis.has(id)) state.selected.delete(id);
      a.el.classList.toggle("selected", state.selected.has(id));
    }
    $("#empty").hidden = vis.size > 0;
    $("#selCount").textContent = `${state.selected.size} selecionado${state.selected.size === 1 ? "" : "s"} de ${vis.size}`;
    for (const b of document.querySelectorAll("[data-needs-sel]")) b.disabled = state.selected.size === 0;
    $("#shareBtn").hidden = !!state.share;
    $("#shareStopBtn").hidden = !state.share;
    if (state.share) $("#shareStopBtn").textContent = `⏹ Parar partilha (${state.share.targets.size} PCs)`;
  }

  $("#roomFilter").addEventListener("change", (e) => { state.room = e.target.value; refresh(); });

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

  // ------------------------------------------------------------------ ações
  const names = (ids) => [...ids].map((id) => state.agents.get(id)?.info.hostname || id).join(", ");

  $("#lockBtn").addEventListener("click", () => cmd("lock", state.selected));
  $("#unlockBtn").addEventListener("click", () => cmd("unlock", state.selected));
  $("#msgBtn").addEventListener("click", () => openMessage(state.selected));
  $("#urlBtn").addEventListener("click", () => {
    const d = $("#urlDialog");
    d.querySelector("form").reset();
    d.returnValue = "";
    d.showModal();
  });
  $("#urlDialog").addEventListener("close", (e) => {
    const d = e.target;
    if (d.returnValue !== "ok") return;
    const url = d.querySelector("[name=url]").value.trim();
    cmd("openUrl", state.selected, { url });
    addChat(`Link → ${names(state.selected)}`, url, true);
  });

  const confirmCmd = (action, label) => () => {
    if (confirm(`${label} ${state.selected.size} computador(es)?\n${names(state.selected)}\n\nO trabalho não guardado dos alunos perde-se.`))
      cmd(action, state.selected);
  };
  $("#logoffBtn").addEventListener("click", confirmCmd("logoff", "Terminar a sessão em"));
  $("#rebootBtn").addEventListener("click", confirmCmd("reboot", "Reiniciar"));
  $("#shutdownBtn").addEventListener("click", confirmCmd("shutdown", "Desligar"));

  let msgTargets = new Set();
  function openMessage(ids) {
    msgTargets = new Set(ids);
    const d = $("#msgDialog");
    d.querySelector("[name=text]").value = "";
    $("#msgTargets").textContent = "Para: " + names(msgTargets);
    d.returnValue = "";
    d.showModal();
  }
  $("#msgDialog").addEventListener("close", (e) => {
    const d = e.target;
    if (d.returnValue !== "ok") return;
    const f = new FormData(d.querySelector("form"));
    const text = String(f.get("text") || "").trim();
    if (!text) return;
    cmd("message", msgTargets, { title: f.get("title"), text, allowReply: f.get("allowReply") === "on" });
    addChat(`Eu → ${names(msgTargets)}`, text, true);
  });

  function addChat(head, text, outgoing) {
    const li = document.createElement("li");
    if (outgoing) li.className = "out";
    const h = document.createElement("div");
    h.className = "h";
    h.textContent = `${new Date().toLocaleTimeString("pt-PT", { hour: "2-digit", minute: "2-digit" })} · ${head}`;
    const t = document.createElement("div");
    t.className = "t";
    t.textContent = text;
    li.append(h, t);
    const chat = $("#chat");
    chat.append(li);
    chat.scrollTop = chat.scrollHeight;
  }

  // ------------------------------------------------------------------ partilha do ecrã do professor
  $("#shareBtn").addEventListener("click", startShare);
  $("#shareStopBtn").addEventListener("click", stopShare);

  async function startShare() {
    if (!navigator.mediaDevices?.getDisplayMedia) {
      alert("Este browser não permite partilhar o ecrã aqui. Usa Chrome, Edge, Firefox ou Safari, e abre a consola por https://");
      return;
    }
    const targets = new Set(state.selected);
    let stream;
    try {
      stream = await navigator.mediaDevices.getDisplayMedia({ video: { frameRate: 10 }, audio: false });
    } catch { return; }

    const video = $("#shareVideo");
    video.srcObject = stream;
    await video.play();
    const canvas = $("#shareCanvas");
    const ctx = canvas.getContext("2d");
    const saving = $("#netMode").value !== "0";
    const fps = saving ? 3 : 6, maxW = saving ? 1280 : 1920, q = saving ? 0.5 : 0.65;
    let busy = false;

    const timer = setInterval(() => {
      if (busy || !video.videoWidth) return;
      const ws = state.ws;
      if (!ws || ws.readyState !== WebSocket.OPEN) return;
      if (ws.bufferedAmount > 1_000_000) return; // rede lenta: salta frames em vez de acumular atraso
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
    addChat(`Partilha de ecrã → ${names(targets)}`, "Iniciada", true);
    refresh();
  }

  function stopShare() {
    const s = state.share;
    if (!s) return;
    state.share = null;
    clearInterval(s.timer);
    s.stream.getTracks().forEach((t) => t.stop());
    if (s.targets.size) cmd("broadcastStop", s.targets);
    addChat("Partilha de ecrã", "Terminada", true);
    refresh();
  }

  // ------------------------------------------------------------------ ver em grande / controlo remoto
  const viewer = $("#viewer"), vimg = $("#viewerImg");

  function openViewer(id) {
    if (state.viewing) closeViewer();
    const a = state.agents.get(id);
    if (!a) return;
    state.viewing = id;
    $("#viewerTitle").textContent = `${a.info.hostname || id} — ${a.info.user || "sem sessão"}`;
    $("#controlToggle").checked = false;
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
    if (viewer.open) viewer.close();
  }

  $("#viewerClose").addEventListener("click", closeViewer);
  viewer.addEventListener("close", closeViewer);
  viewer.addEventListener("cancel", (e) => { if ($("#controlToggle").checked) e.preventDefault(); });
  $("#viewerLock").addEventListener("click", () => state.viewing && cmd("lock", [state.viewing]));
  $("#viewerMsg").addEventListener("click", () => state.viewing && openMessage([state.viewing]));
  $("#controlToggle").addEventListener("change", (e) => {
    vimg.classList.toggle("control", e.target.checked);
    if (e.target.checked) vimg.focus();
  });

  const controlling = () => state.viewing && $("#controlToggle").checked;
  const input = (o) => cmd("input", [state.viewing], o);

  // a imagem usa object-fit: contain -> calcula a área real ocupada pelo ecrã do aluno
  function pos(e) {
    const r = vimg.getBoundingClientRect();
    const iw = vimg.naturalWidth || r.width, ih = vimg.naturalHeight || r.height;
    const s = Math.min(r.width / iw, r.height / ih);
    const w = iw * s, h = ih * s;
    const left = r.left + (r.width - w) / 2, top = r.top + (r.height - h) / 2;
    return {
      x: Math.min(1, Math.max(0, (e.clientX - left) / w)),
      y: Math.min(1, Math.max(0, (e.clientY - top) / h)),
    };
  }
  let lastMove = 0;
  vimg.addEventListener("mousemove", (e) => {
    if (!controlling()) return;
    const now = performance.now();
    if (now - lastMove < 33) return;
    lastMove = now;
    input({ ev: "move", ...pos(e) });
  });
  vimg.addEventListener("mousedown", (e) => {
    if (!controlling()) return;
    e.preventDefault();
    vimg.focus();
    input({ ev: "down", button: e.button, ...pos(e) });
  });
  vimg.addEventListener("mouseup", (e) => {
    if (!controlling()) return;
    e.preventDefault();
    input({ ev: "up", button: e.button, ...pos(e) });
  });
  vimg.addEventListener("contextmenu", (e) => { if (controlling()) e.preventDefault(); });
  vimg.addEventListener("wheel", (e) => {
    if (!controlling()) return;
    e.preventDefault();
    input({ ev: "wheel", delta: Math.round(-e.deltaY), ...pos(e) });
  }, { passive: false });

  for (const type of ["keydown", "keyup"]) {
    document.addEventListener(type, (e) => {
      if (!controlling() || document.activeElement !== vimg) return;
      const vk = window.VK[e.code];
      if (!vk) return;
      e.preventDefault();
      input({ ev: "key", vk, down: type === "keydown" });
    });
  }

  // ------------------------------------------------------------------ arranque
  if (state.token) showConsole();
  window.addEventListener("beforeunload", stopShare);
})();

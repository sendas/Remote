// Agente simulado para testar o servidor sem PCs Windows.
// Uso: NODE_TLS_REJECT_UNAUTHORIZED=0 node fake-agent.mjs wss://localhost:8443/ws/agent <EnrollmentKey> <nome> [sala] [imagem.jpg]
import { readFileSync } from "node:fs";

const [url, key, name = "SALA1-PC01", room = "SALA1", imgPath, school = ""] = process.argv.slice(2);
if (!url || !key) { console.error("uso: node fake-agent.mjs <url> <key> [nome] [sala] [imagem.jpg]"); process.exit(1); }

// JPEG 1x1 cinzento, caso não seja dada imagem
const tiny = Buffer.from("/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////wgALCAABAAEBAREA/8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPxA=", "base64");
const jpeg = imgPath ? readFileSync(imgPath) : tiny;
const frame = (kind) => { const b = Buffer.alloc(jpeg.length + 1); b[0] = kind; jpeg.copy(b, 1); return b; };

const ws = new WebSocket(url);
ws.binaryType = "arraybuffer";
let streamTimer = null;
let fileBytes = 0;
ws.onopen = () => {
  ws.send(JSON.stringify({ type: "hello", key, id: name, hostname: name, room, school, exeSha: process.env.SHA || "", macs: ["aa:bb:cc:00:11:22"], user: "ESCOLA\\aluno." + name.toLowerCase(), os: "Simulado", version: "0.0.0" }));
  setInterval(() => ws.send(frame(0x01)), 2000);
  ws.send(frame(0x01));
  if (process.env.HAND) { setTimeout(() => ws.send(JSON.stringify({ type: "hand" })), 1500); setTimeout(() => ws.send(JSON.stringify({ type: "chat", text: "Stôr, não percebi o exercício 3" })), 2500); }
};
ws.onmessage = (ev) => {
  if (typeof ev.data !== "string") {
    const b = new Uint8Array(ev.data);
    if (b[0] === 0x20) { fileBytes += b.length - 17; return; }
    console.log(`[${name}] frame de partilha recebido (${ev.data.byteLength} bytes)`); return;
  }
  const m = JSON.parse(ev.data);
  if (m.type !== "input" || m.ev !== "move") console.log(`[${name}]`, JSON.stringify(m));
  if (m.type === "message" && m.allowReply) setTimeout(() => ws.send(JSON.stringify({ type: "reply", msgId: m.id, text: "Recebido, professor!" })), 500);
  if (m.type === "procs") ws.send(JSON.stringify({ type: "procs", list: [{ pid: 101, name: "chrome", title: "YouTube - Google Chrome" }, { pid: 202, name: "WINWORD", title: "Trabalho.docx - Word" }] }));
  if (m.type === "fileStart") { fileBytes = 0; console.log(`[${name}] ficheiro a chegar: ${m.name} (${m.size} bytes)`); }
  if (m.type === "fileEnd") console.log(`[${name}] ficheiro completo: ${fileBytes} bytes`);
  if (m.type === "internet") console.log(`[${name}] internet ${m.on ? "ligada" : "cortada"}`);
  if (m.type === "selfUpdate") console.log(`[${name}] pedido para atualizar: ${m.path}`);
  if (m.type === "closeAll") console.log(`[${name}] fechar todos os programas`);
  if (m.type === "poll") { console.log(`[${name}] pergunta: ${m.question} [${m.options.join("/")}]`); setTimeout(() => ws.send(JSON.stringify({ type: "answer", poll: m.id, choice: m.options[name.endsWith("4") ? 1 : 0] })), 300); }
  if (m.type === "collect") {
    ws.send(JSON.stringify({ type: "collectStart", batch: m.batch }));
    const pkt = new Uint8Array(17 + jpeg.length); pkt[0] = 0x21; pkt.set(new TextEncoder().encode(m.batch.padEnd(16).slice(0, 16)), 1); pkt.set(jpeg, 17);
    ws.send(pkt);
    ws.send(JSON.stringify({ type: "collectEnd", ok: true }));
  }
  if (m.type === "chat") setTimeout(() => ws.send(JSON.stringify({ type: "chat", text: "Obrigado, professor!" })), 400);
  if (m.type === "notice") console.log(`[${name}] aviso estilo=${m.style} canto=${m.corner} fechar=${m.closable} fim=${m.endAction} (${m.text})`);
  if (m.type === "minimize") console.log(`[${name}] minimizar tudo`);
  if (m.type === "mute") console.log(`[${name}] som ${m.on ? "off" : "on"}`);
  if (m.type === "lock") console.log(`[${name}] bloqueado cor=${m.color} seconds=${m.seconds}`);
  if (m.type === "unlock") console.log(`[${name}] desbloqueado`);
  if (m.type === "stream") {
    clearInterval(streamTimer);
    if (m.on) streamTimer = setInterval(() => ws.send(frame(0x02)), 1000 / m.fps);
  }
};
ws.onclose = (e) => { console.log(`[${name}] ligação fechada`, e.code); process.exit(0); };
ws.onerror = (e) => console.error(`[${name}] erro`, e.message ?? e);

// Plays a fake host app against a running Viewer: one Session, a Snapshot, then Actions as fast as the ingest hub takes
// them. For checking the Viewer under load by hand (bursts, follow mode) without a real Fluxor app.
//
//   node fake-session.mjs [--url http://localhost:5299] [--actions 5000] [--batch 1] [--delay 0] [--app FakeApp]
//
// Speaks SignalR's JSON protocol over a WebSocket directly, so it needs nothing beyond Node 22+.

import { randomUUID } from "node:crypto";
import { parseArgs } from "node:util";

const { values: opts } = parseArgs({
  options: {
    url: { type: "string", default: "http://localhost:5299" },
    actions: { type: "string", default: "5000" },
    batch: { type: "string", default: "1" },
    delay: { type: "string", default: "0" },
    app: { type: "string", default: "FakeApp" },
  },
});
const total = Number(opts.actions);
const batchSize = Number(opts.batch);
const delay = Number(opts.delay);
const RS = "\x1e"; // SignalR record separator

const hub = new URL("/hub/ingest", opts.url);
const negotiate = await fetch(new URL(`${hub.pathname}/negotiate?negotiateVersion=1`, hub), { method: "POST" });
if (!negotiate.ok) throw new Error(`negotiate: ${negotiate.status}`);
const { connectionToken } = await negotiate.json();

const wsUrl = new URL(hub);
wsUrl.protocol = wsUrl.protocol.replace("http", "ws");
wsUrl.searchParams.set("id", connectionToken);
const ws = new WebSocket(wsUrl);

let invocationId = 0;
const pending = new Map();
let handshake;
const handshaken = new Promise((resolve) => (handshake = resolve));

ws.addEventListener("message", async (event) => {
  const text = typeof event.data === "string" ? event.data : await event.data.text();
  for (const frame of text.split(RS).filter(Boolean)) {
    const message = JSON.parse(frame);
    if (message.type === undefined) handshake(message);
    else if (message.type === 3) {
      const { resolve, reject } = pending.get(message.invocationId);
      pending.delete(message.invocationId);
      message.error ? reject(new Error(message.error)) : resolve();
    } else if (message.type === 7) throw new Error(`hub closed: ${message.error ?? ""}`);
  }
});
await new Promise((resolve, reject) => {
  ws.addEventListener("open", resolve);
  ws.addEventListener("error", reject);
});
ws.send(JSON.stringify({ protocol: "json", version: 1 }) + RS);
const reply = await handshaken;
if (reply.error) throw new Error(`handshake: ${reply.error}`);

function invoke(target, ...args) {
  const id = String(++invocationId);
  ws.send(JSON.stringify({ type: 1, invocationId: id, target, arguments: args }) + RS);
  return new Promise((resolve, reject) => pending.set(id, { resolve, reject }));
}

const sessionId = randomUUID();
const counter = (count) => ({ name: "Counter", stateType: "Fake.CounterState", stateJson: JSON.stringify({ count }) });
await invoke("Snapshot", {
  protocolVersion: { major: 1, minor: 0 },
  sessionId,
  appName: opts.app,
  runtime: "Server",
  startedAt: new Date().toISOString(),
  lastSequence: 0,
  droppedCount: 0,
  features: [counter(0)],
});
console.log(`Session ${sessionId.slice(0, 4)} (${opts.app}): sending ${total} Actions in batches of ${batchSize}…`);

const started = performance.now();
for (let sequence = 1; sequence <= total; ) {
  const batch = [];
  for (; batch.length < batchSize && sequence <= total; sequence++)
    batch.push({
      sessionId,
      sequence,
      timestamp: new Date().toISOString(),
      actionType: "Fake.IncrementAction",
      actionJson: JSON.stringify({ amount: 1 }),
      changedFeatures: [counter(sequence)],
    });
  await invoke("Actions", batch);
  if (delay > 0) await new Promise((resolve) => setTimeout(resolve, delay));
}
console.log(`Sent in ${Math.round(performance.now() - started)} ms. Ctrl+C to disconnect the Session.`);

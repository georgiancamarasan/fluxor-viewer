# Architecture

Fluxor Viewer lets a developer watch, in real time, the Actions dispatched in a Fluxor app and the resulting Feature states — without a browser extension (see [ADR 0001](adr/0001-own-viewer-instead-of-redux-devtools.md)). Vocabulary follows [GLOSSARY.md](../GLOSSARY.md).

## Overview

```mermaid
flowchart LR
  subgraph Host["Host app (Blazor InteractiveAuto)"]
    subgraph S["Server circuit"]
      SS[Fluxor store] --> SM[FluxorViewer middleware]
    end
    subgraph W["Browser (WebAssembly)"]
      WS[Fluxor store] --> WM[FluxorViewer middleware]
    end
  end
  SM -- SignalR /hub/ingest --> V
  WM -- SignalR /hub/ingest (CORS) --> V
  subgraph V["Viewer (dotnet tool, localhost:5299)"]
    H[Ingest hub] --> R[Session registry<br/>+ history]
    R -- in-process change events --> UI[Blazor Server UI]
  end
  UI -- Blazor circuit --> B[Developer's browser]
```

Data flows one way: middleware → Viewer. The Viewer never sends commands to the host app ([ADR 0003](adr/0003-read-only-one-way-reporting.md)).

## Components

| Project | Kind | Responsibility |
|---|---|---|
| `src/FluxorViewer.Protocol` | NuGet (`net10.0`) | Message contracts and protocol version. Shared by both sides so they can't drift. |
| `src/FluxorViewer.Middleware` | NuGet (`net10.0`, Fluxor 6.x) | Fluxor `IMiddleware` that observes the store and reports to the Viewer. |
| `src/FluxorViewer.Server` | `dotnet tool` (`fluxor-viewer`) | ASP.NET Core app: ingest hub, in-memory Session history, Blazor Server UI. |
| `samples/AutoSample` | Sample | InteractiveAuto app (Server + `.Client`) with a few Features, used for manual testing. |
| `tests/FluxorViewer.Middleware.Tests` | Tests | Middleware against a real Fluxor store with a fake transport. |
| `tests/FluxorViewer.Server.Tests` | Tests | `WebApplicationFactory` + real SignalR client driving the ingest hub. |

## Middleware

### Registration

```csharp
services.AddFluxor(o => o
    .ScanAssemblies(typeof(Program).Assembly)
    .UseFluxorViewer(v => {
        v.ServerUrl  = "http://localhost:5299";          // default
        v.AppName    = "MyApp";                          // default: entry assembly name
        v.BufferSize = 1000;                             // default
        v.Enabled    = builder.HostEnvironment.IsDevelopment();
        v.IgnoreAction(a => a is TimerTickAction);
    }));
```

When `Enabled` is false the middleware does nothing: no connection, no serialisation. The same registration is used on both the Server project and the `.Client` project of an InteractiveAuto app.

### Lifecycle

1. **`InitializeAsync(dispatcher, store)`**: generate a `SessionId` (GUID, one per store instance), detect `Runtime` (`OperatingSystem.IsBrowser()` → `WebAssembly`, otherwise `Server`), capture the state reference of every Feature in `store.Features`, and start the background sender.
2. **`AfterDispatch(action)`**: runs after reducers. Assign the next `Sequence`, compare each Feature's `GetState()` by reference against the last captured one (Fluxor state is immutable), and, while connected, enqueue the Action and the **changed** Features' states as references. Nothing is serialised here. Ignored Actions update the captured references but aren't enqueued.
3. **Background sender**: reads from a `Channel` bounded at `BufferSize`, sends up to 100 queued Actions per `Actions` call (whatever is ready, no linger), and sends them over SignalR. On every (re)connect it first sends a **Snapshot** of all Features, built from the captured references and the last `Sequence` (copied together under one lock, so it matches the Actions that follow), then whatever is still buffered. It serialises each message just before sending. Before the first connect nothing is enqueued, since the Snapshot already covers it. `InitializeAsync` never waits for the connection.
4. **Dispose**: the middleware is scoped like the store, so when the scope ends (e.g. a Blazor circuit closes) it cancels any pending connect and closes the hub connection, and the Viewer sees the Session disconnect.

### Safety guarantees

- `AfterDispatch` never awaits or serialises; it only compares references and writes to the channel. Any exception inside it is caught.
- The channel is bounded (`BufferSize`). When it's full, `AfterDispatch` stops queueing and counts the skipped Actions; once the sender has drained what's queued it sends a fresh Snapshot carrying that `DroppedCount`, so the Viewer records a Resync instead of silently reconstructing wrong state.
- Serialisation uses System.Text.Json with `ReferenceHandler.IgnoreCycles` and a max depth. Any failure for a single Action or Feature becomes `{"$error": "<message>"}`; nothing throws into the host app.
- Connection failures retry with backoff forever, silently (logged at Debug).
- In WebAssembly there are no blocking threads; the sender is a plain async loop.

## Protocol

Each Snapshot carries `ProtocolVersion`. The Viewer rejects a Snapshot with a different major version (logged once per Session) and the middleware logs the rejection as a warning. Actions don't carry the version: they're only accepted after a compatible Snapshot on the same connection.

```text
Snapshot
  ProtocolVersion, SessionId, AppName, Runtime (Server|WebAssembly),
  StartedAt, LastSequence, DroppedCount,
  Features: [ { Name, StateType, StateJson } ]

ActionRecorded
  SessionId, Sequence, Timestamp,
  ActionType, ActionJson,
  ChangedFeatures: [ { Name, StateType, StateJson } ]
```

Hub methods on `/hub/ingest`: `Snapshot(Snapshot)` and `Actions(ActionRecorded[])`. One hub connection per Session: the first accepted Snapshot binds the connection to its `SessionId`, and later Snapshots and Actions on it must carry the same one. Rejections are `HubException`s and leave the connection open. CORS allows `localhost`, `127.0.0.1` and `[::1]` origins on any port so WebAssembly apps can connect. The maximum message size is 32 MB (SignalR's default of 32 KB is too small for real Snapshots).

Why changed-Features-only plus Snapshots instead of full state every time: see [ADR 0002](adr/0002-changed-feature-deltas-with-snapshot-resync.md).

## Viewer

### Session registry and history

- `SessionRegistry` holds every Session seen since the Viewer started: info (app name, runtime, started-at, short id), connection status, and its history. In memory only.
- The first Snapshot with a new `SessionId` creates the Session; its info comes from that Snapshot and isn't changed by later ones. The short id (first 4 hex chars) is a display label only and may collide.
- A Session is *connected* while its current hub connection is up; on disconnect it's kept and marked *disconnected*. A reconnect's Snapshot moves the Session to the new connection: a late close from the old one doesn't disconnect it, and Actions still arriving on the old one are dropped (the new Snapshot already covers them).
- **Clear** removes a disconnected Session. A connected Session keeps its current Feature states and last Sequence as its new starting point (in place of a Snapshot) and goes on recording, since the Viewer can't ask the app for a fresh Snapshot.
- Sessions are listed in the order the Viewer first saw them. One lock guards the registry; readers get copies.
- History is an ordered list of entries, capped at N (default 1000, configurable) as a ring buffer:
  - **ActionEntry**: sequence, timestamp, type, payload JSON, set of changed Feature names, and the maps *Feature → state JSON* before and after this Action.
  - **ResyncEntry**: written when a Snapshot arrives for a Session that already has history and either `DroppedCount > 0` or `LastSequence` doesn't follow the last received sequence. Shows how many Actions were lost.
- State reconstruction: each Session has a *current* map (immutable, keyed by Feature name, JSON left unparsed). A Snapshot replaces it. Each Action takes the current map as its *before*, replaces its changed Features to make its *after*, and makes that current. Unchanged Features share the same JSON instance, so per-entry cost is proportional to what changed. Because every entry holds its own before map, evicting old entries or clearing the History never loses the state an entry starts from; Clear keeps the current map.
- The registry raises in-process change events (`SessionAdded`, `SessionStatusChanged`, `EntriesAppended`, `SessionCleared`) which UI components subscribe to.

### UI

```
┌──────────────────────────────────────────────────────────────────────┐
│ Session [● MyApp · WebAssembly · 3f2a ▾]  Feature [CounterState ▾] [Clear] │
├──────────────────────────┬───────────────────────────────────────────┤
│ [filter…] [☐ changed only]│ ▸ Payload: { "amount": 1 }                │
│ 41 IncrementAction 10:02 │ ┌ State ┬ Diff ┐                          │
│ 42 FetchDataAction 10:02 │ │ { "count": 3 }   (collapsible tree)     │
│ ⚠ resync — 12 lost       │ │                                         │
│ 55 IncrementAction 10:03 │ │                                         │
│              [▼ 3 new]   │ └─────────────────────────────────────────┘
└──────────────────────────┴───────────────────────────────────────────┘
```

- **Session picker**: all Sessions, with a connected/disconnected dot; auto-selects when only one exists.
- **Feature picker**: Features of the selected Session; the choice is remembered by name when switching Sessions.
- **Action list**: every Action. Actions that didn't change the selected Feature are dimmed. There's a "changed only" toggle, a type-name filter, and Resync marker rows.
- **Right pane**: payload strip for the selected Action; **State** tab = the selected Feature's state *after* the selected Action; **Diff** tab = before → after rendered with `jsondiffpatch` via JS interop, or "No change to <Feature>".
- **Follow mode**: on by default, so the newest Action stays selected. Selecting an older Action pauses it and a "▼ N new" pill appears; clicking the pill resumes follow mode.

Updates reach the developer's browser through the Blazor Server circuit; no extra hub is needed for the UI.

### Hosting

`fluxor-viewer [--port 5299] [--history 1000]` binds to `localhost` only and prints the URL. There's no authentication; it's a local development tool.

## InteractiveAuto notes

- Prerendering doesn't start the store (Fluxor's `StoreInitializer` initialises after first render), so no Session is created for prerender.
- One tab may produce a Server Session and later a WebAssembly Session. They are deliberately separate Sessions ([ADR 0004](adr/0004-separate-sessions-per-store-under-interactive-auto.md)).
- Blazor Server: one Session (and one hub connection) per circuit.

## Out of scope for v1

Time-travel, replay, dispatching from the Viewer, import/export, persistence across Viewer restarts, correlating Server and WebAssembly Sessions from the same tab, authentication, non-localhost deployment.

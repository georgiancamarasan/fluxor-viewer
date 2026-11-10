# Epic: Middleware (MW)

## MW-1 — Register and configure the middleware

As an app developer, I want to add the Viewer middleware with one call so that my app starts reporting.

- `.UseFluxorViewer(v => …)` on Fluxor options registers the middleware.
- Options: `ServerUrl` (default `http://localhost:5299`), `AppName` (default entry assembly name), `BufferSize` (default 1000), `Enabled` (default false), `IgnoreAction(Func<object,bool>)`.
- With `Enabled = false` the middleware makes no network connection and serialises nothing (verified by test).
- With `Enabled = true`, invalid options (a `ServerUrl` that isn't an absolute http/https URL, a blank `AppName`, or `BufferSize < 1`) fail fast at registration with one descriptive exception listing every problem. Invalid options are ignored while disabled.
- Calling `UseFluxorViewer` twice on the same Fluxor options throws.

## MW-2 — Create a Session and send a Snapshot on start

As a developer, I want each running store to appear as a Session with its full state so that I see where it started.

- On `InitializeAsync` a new `SessionId` is generated per store instance.
- `Runtime` is `WebAssembly` when `OperatingSystem.IsBrowser()`, otherwise `Server`.
- The first message sent after connecting is a Snapshot containing every Feature in `store.Features` with name, state type full name and state JSON, plus `LastSequence = 0` and `DroppedCount = 0`.
- Two store instances in the same process (two circuits) produce two different `SessionId`s.

## MW-3 — Record Actions with changed Features

As a developer, I want every dispatched Action and the state changes it caused to be reported.

- On `AfterDispatch`, a message is enqueued with a sequence number that increases by 1 per Session, a UTC timestamp, the Action's type name, the Action JSON, and only the Features whose state reference changed.
- An Action that changes no Feature is still reported, with empty `ChangedFeatures`.
- Actions dispatched from Effects and Fluxor's internal Actions are reported like any other.
- Test: dispatching an Action that changes Feature A but not B reports only A.

## MW-4 — Never fail on serialisation

As an app developer, I want odd state or Action shapes to never break my app.

- Serialisation uses System.Text.Json with `ReferenceHandler.IgnoreCycles` and a max depth (default 64).
- If an Action or one Feature fails to serialise, that value becomes `{"$error": "<exception message>"}` and the rest of the message is sent normally.
- No exception from the middleware escapes into Fluxor's dispatch (verified with a throwing converter).

## MW-5 — Send without blocking the app

As an app developer, I want reporting to add negligible cost to dispatch.

- `AfterDispatch` performs no network I/O and never awaits; it writes to a bounded `Channel`.
- A background loop batches queued messages (up to 100 per send, or whatever is available) and calls the hub's `Actions` method.
- Works in WebAssembly (single-threaded): no `Thread`, no blocking waits.
- Benchmark/test: dispatching 5,000 Actions with the Viewer unreachable completes without noticeable delay compared with the middleware disabled.

## MW-6 — Buffer while offline, reconnect, and resync

As a developer, I want history to recover when the Viewer restarts or the connection drops.

- When the Viewer is unreachable, the middleware keeps at most `BufferSize` messages, dropping the oldest and counting the drops.
- It reconnects with backoff indefinitely and logs failures at Debug only.
- On every (re)connect it first sends a Snapshot (same `SessionId`, current `LastSequence`, `DroppedCount` since the last Snapshot), then the remaining buffered messages.
- Test: with the Viewer down, dispatch BufferSize + 10 Actions, start the Viewer, and check that the Snapshot reports 10 dropped and the buffered Actions arrive in order.

## MW-7 — Ignore noisy Actions

As a developer, I want to hide high-frequency Actions so the list stays readable.

- Actions matching any `IgnoreAction` predicate are not sent.
- State changes they cause still appear: the next reported Action carries the affected Features as changed, and the next Snapshot includes them.
- A predicate that throws is treated as "not ignored" and logged once.

## MW-8 — Work in both halves of an InteractiveAuto app

As a developer, I want the same registration to work on Server and WebAssembly.

- `samples/AutoSample` calls `UseFluxorViewer` on both Server and `.Client`, enabled only in Development.
- In `samples/AutoSample` with the Viewer running, a Server Session appears when the circuit starts, and a WebAssembly Session appears once WASM takes over.
- No Session is created during prerendering.
- Connecting from WebAssembly works with the Viewer's default CORS policy.

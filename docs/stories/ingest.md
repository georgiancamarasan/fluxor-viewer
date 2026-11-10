# Epic: Ingest (IN)

## IN-1 — Protocol contracts and versioning

As a contributor, I want one shared definition of the messages so that middleware and Viewer stay compatible.

- `FluxorViewer.Protocol` defines `Snapshot`, `ActionRecorded`, `FeatureState`, `Runtime` and `ProtocolVersion` as described in [architecture.md](../architecture.md#protocol).
- Contracts are plain records with no dependencies beyond the BCL.
- State and Action payloads are carried as JSON strings (opaque to the transport).
- Round-trip serialisation tests cover each message.

## IN-2 — Ingest hub

As the Viewer, I want to accept Snapshots and Actions from middleware.

- A SignalR hub at `/hub/ingest` exposes `Snapshot(Snapshot)` and `Actions(ActionRecorded[])`.
- CORS allows any `http(s)://localhost:*` and `127.0.0.1` origin.
- A message with an incompatible major `ProtocolVersion` is rejected (the hub throws `HubException` with an explanation) and logged once per Session.
- `Actions` for a Session with no Snapshot yet on this connection is rejected.
- Integration test: a real SignalR client sends a Snapshot and three Actions, and the registry contains them.

## IN-3 — Session registry and lifecycle

As a developer, I want to see which Sessions exist and whether they're still live.

- The first Snapshot with a new `SessionId` creates a Session (app name, runtime, started-at, short id = first 4 hex chars).
- Session status is *connected* while its hub connection is open and *disconnected* after it closes; disconnected Sessions are kept.
- A later Snapshot with a known `SessionId` (a reconnect) reuses the Session and marks it connected.
- `Clear(sessionId)` removes a Session and its history.
- Thread-safe under concurrent hub calls from many Sessions.

## IN-4 — Reconstruct Feature state per Action

As a developer, I want to see the full state of any Feature after any Action.

- Each ActionEntry stores a map *Feature → state JSON after this Action*, built from the previous entry's map with that Action's changed Features replaced.
- Unchanged Features share the previous entry's JSON instance (verified by reference in a test).
- Each entry records which Features changed, so "did this Action change Feature X?" is O(1).
- The state before an Action is the previous entry's map, or the Snapshot for the first entry.

## IN-5 — Handle Resyncs

As a developer, I want to know when history has a hole so I don't trust a misleading diff.

- When a Snapshot arrives for a Session that already has history and either `DroppedCount > 0` or `LastSequence` ≠ the last received sequence, a ResyncEntry is appended with the number of lost Actions (`DroppedCount`, or the sequence gap if larger).
- The Snapshot becomes the new base map for subsequent entries.
- A reconnect with no loss appends no ResyncEntry.
- Duplicate Actions (sequence ≤ last received) are ignored.

## IN-6 — Cap history per Session

As a developer, I want the Viewer to stay responsive during long sessions.

- Each Session keeps at most `--history` entries (default 1000); the oldest are evicted first.
- After eviction, the oldest remaining entry's "before" state is still available (the base map is kept with it).
- Test: 1,500 Actions with a cap of 1,000 leaves sequences 501–1500 with correct before/after states.

## IN-7 — Notify the UI of changes

As the Viewer UI, I want to be told about changes so that I can update live.

- The registry raises `SessionAdded`, `SessionStatusChanged`, `EntriesAppended(sessionId, count)` and `SessionCleared`.
- Events fire once per hub call (once per batch, not once per Action).
- Subscribers that throw don't affect ingestion or other subscribers.

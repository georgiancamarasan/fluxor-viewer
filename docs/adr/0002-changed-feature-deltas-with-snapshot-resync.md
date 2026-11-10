# Middleware sends changed Features only; Viewer reconstructs state, resyncing from Snapshots

After each Action the middleware sends only the Features whose state reference changed (Fluxor state is immutable, so reference inequality is a reliable, cheap check), and the Viewer reconstructs full per-Feature state and computes diffs itself. This keeps the host app's per-dispatch cost and payloads small, at the price that any lost message corrupts reconstruction — so every (re)connect starts with a full Snapshot and the Viewer marks a Resync in the history. Transport is SignalR because its .NET client works in both Blazor Server and WebAssembly and the Viewer UI already uses it for live updates.

## Considered Options

- **Full state of all Features after every Action**: self-healing, but serialises the whole state tree on every dispatch inside the host app.
- **Middleware computes JSON diffs**: smallest payloads, but puts diffing cost and complexity into the host app.

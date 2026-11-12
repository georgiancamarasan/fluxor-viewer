# A Snapshot is taken from the base, not the current state, and never rewinds

The middleware buffers every unsent Action (up to `BufferSize`, from the Session's start, connected or not) and keeps a **base**: the Feature state references just after the last Action that left the buffer, whether sent or dropped as the oldest when the buffer was full. A Snapshot is built from the base, not the current state, so the buffered Actions that follow it still apply on top of it. That way a Viewer started late or restarted gets real History for the offline period, instead of only "N actions lost". Because states are immutable references, keeping the base costs the same as tracking the current state and needs no serialisation in `AfterDispatch`.

A batch counts as delivered once handed to the send, even if the send fails, so a Snapshot's `LastSequence` is never less than any Sequence already sent. A lost batch then shows as a Sequence gap, and the Viewer never has to accept a Snapshot older than its own state.

Every loss is followed by a Snapshot: an Action dropped from a full buffer, a batch whose send failed (the reconnect starts with one) and an Action that couldn't be built (it leaves the buffer into the base like a dropped one). So the Viewer never detects gaps between Actions or rebuilds state across one; a Resync, placed when a Snapshot doesn't follow on from what it had, is the only marker of lost History. A Snapshot older than the Viewer's state would be a bug: the Viewer still takes it as a Resync rather than rewinding.

This extends [ADR 0002](0002-changed-feature-deltas-with-snapshot-resync.md): every (re)connect still starts with a full Snapshot.

## Considered Options

- **Viewer detects Sequence gaps between Actions**: catches a skipped Action without middleware changes, but needs a second kind of marker whose state stays untrustworthy until some later Snapshot.

- **Snapshot of the current state, no offline buffer**: simpler, but every disconnect, including the Viewer starting after the app, loses all History in between.
- **Resend a failed batch after a Snapshot from before it**: loses nothing when the batch never arrived, but when it did arrive, the Viewer would have to rewind to an older Snapshot and then discard the resent Actions as duplicates, which corrupts its diffs.

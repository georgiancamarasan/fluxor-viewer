# Fluxor Viewer

A developer debugging tool: an app using Fluxor reports what happens to its state to a separate viewer, where the developer inspects actions and state in real time.

## Language

**Host app**:
The application using Fluxor that reports its Sessions to the Viewer, identified to the Viewer by its app name; one Host app can produce many Sessions.
_Avoid_: Client, target, app (on its own)

**Session**:
One running instance of a Fluxor store inside a host app (e.g. one Blazor Server circuit, or one browser tab running WebAssembly), as seen by the viewer.
_Avoid_: Store, connection, client, app

**Session status**:
Whether the Viewer currently has a connection for a Session: *connected* or *disconnected*. Disconnected doesn't mean the Session has ended; it may reconnect, and the Viewer can't tell the difference.
_Avoid_: Live, online, active, dead

**Clear**:
The developer discarding a Session's history. A disconnected Session is removed; a connected Session keeps its current Feature states as the new starting point and goes on recording.
_Avoid_: Reset, delete, purge

**Runtime**:
Where a Session's store executes: on the server (in a Blazor Server circuit) or in the browser (WebAssembly).
_Avoid_: Render mode, host, platform

**Feature**:
A named slice of state registered with Fluxor, with its own state type and reducers; a Session contains many Features.
_Avoid_: Store, slice, module

**Action**:
An object dispatched to a Fluxor store, as recorded in a Session's history.
_Avoid_: Event, message, command

**Action type**:
The .NET type of an Action, which says what kind of Action it is; shown by its short name.
_Avoid_: Action name, kind

**Payload**:
What an Action carried: its contents as the Host app sent them.
_Avoid_: Body, arguments, data, action JSON

**Serialisation error**:
A Payload or Feature state the Host app couldn't turn into JSON, sent in its place with the reason, so the rest of the History stays intact.
_Avoid_: Serialization failure, bad state, `$error` (in prose)

**History**:
The ordered record the Viewer keeps of a Session's Actions and its Resyncs, each with the Feature states after it.
_Avoid_: Log, timeline, journal

**Sequence**:
The position of an Action in its Session's history, counting from 1; a gap means Actions were lost.
_Avoid_: Index, id, counter

**Changed Feature**:
A Feature whose state an Action replaced, even if the new state looks identical to the old one.
_Avoid_: Dirty, touched, affected

**Snapshot**:
The complete state of every Feature in a Session just after one of its Actions (or at its start), sent when a Session (re)connects to the Viewer and followed by the Actions that come after that point.
_Avoid_: Dump, initial state, sync

**Resync**:
A point in a Session's History where the Viewer received a Snapshot that doesn't follow on from what it already had, usually because Actions were lost on the way (including before a new Session's first Snapshot); the state before it can't be diffed against the state after it.
_Avoid_: Gap, reset, reconnect

**Follow mode**:
The Viewer keeping the newest Action the developer can see selected as Actions arrive; selecting an older Action pauses it, until the developer returns to the newest or the selection is lost.
_Avoid_: Auto-scroll, tail, live mode

**Viewer**:
The standalone application that receives Sessions and lets the developer inspect their Actions and Feature states.
_Avoid_: Server, dashboard, DevTools

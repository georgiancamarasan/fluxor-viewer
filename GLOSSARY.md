# Fluxor Viewer

A developer debugging tool: an app using Fluxor reports what happens to its state to a separate viewer, where the developer inspects actions and state in real time.

## Language

**Host app**:
The application using Fluxor that reports its Sessions to the Viewer, identified to the Viewer by its app name; one Host app can produce many Sessions.
_Avoid_: Client, target, app (on its own)

**Session**:
One running instance of a Fluxor store inside a host app (e.g. one Blazor Server circuit, or one browser tab running WebAssembly), as seen by the viewer.
_Avoid_: Store, connection, client, app

**Feature**:
A named slice of state registered with Fluxor, with its own state type and reducers; a Session contains many Features.
_Avoid_: Store, slice, module

**Action**:
An object dispatched to a Fluxor store, as recorded in a Session's history.
_Avoid_: Event, message, command

**Snapshot**:
The complete state of every Feature in a Session at one moment, sent when a Session (re)connects to the Viewer.
_Avoid_: Dump, initial state, sync

**Resync**:
A point in a Session's history where the Viewer received a Snapshot after missing one or more Actions.
_Avoid_: Gap, reset, reconnect

**Viewer**:
The standalone application that receives Sessions and lets the developer inspect their Actions and Feature states.
_Avoid_: Server, dashboard, DevTools

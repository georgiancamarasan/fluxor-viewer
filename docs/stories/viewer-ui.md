# Epic: Viewer UI (UI)

## UI-1 — Layout shell and Session picker

As a developer, I want to choose which Session to inspect.

- The page has a top bar (Session picker, Feature picker, Clear button), a left Action list pane and a right state pane.
- The Session picker lists every Session as "AppName · Runtime · shortId", with a green dot when connected and a grey dot when disconnected, newest first.
- With exactly one Session, it's selected automatically.
- With no Sessions, the page shows "Waiting for an app to connect…" plus a snippet of `UseFluxorViewer` setup.

## UI-2 — Feature picker

As a developer, I want to choose which Feature's state to inspect.

- The picker lists the selected Session's Features alphabetically.
- When switching Sessions, the previously selected Feature name stays selected if the new Session has it; otherwise the first Feature is selected.

## UI-3 — Action list

As a developer, I want to scan the Actions dispatched in the Session.

- Each row shows sequence, Action type name (short name, full name on hover) and local time with milliseconds.
- Rows for Actions that didn't change the selected Feature are dimmed.
- A "changed only" toggle hides those rows.
- A text box filters rows by type name (case-insensitive substring).
- ResyncEntries show as "⚠ resync — N actions lost" rows and are never hidden by the filter or toggle.
- Lists of 1,000 entries scroll smoothly (virtualised).

## UI-4 — State tab

As a developer, I want to see the selected Feature's state after the selected Action.

- It shows the state JSON as a collapsible tree, expanded two levels by default.
- `$error` placeholders render visibly as errors, not as normal data.
- With a ResyncEntry selected, it shows the Snapshot state.
- Expand/collapse choices persist while moving between Actions where the paths still exist.

## UI-5 — Diff tab

As a developer, I want to see exactly what the selected Action changed in the selected Feature.

- The before → after diff renders with `jsondiffpatch` (visual formatter) via JS interop, with added, removed and changed values highlighted.
- If the selected Action didn't change the Feature, it shows "No change to <Feature>".
- With a ResyncEntry selected, it shows "History was lost before this point; diff unavailable".
- The tab choice (State/Diff) persists while moving between Actions.

## UI-6 — Action payload

As a developer, I want to see what the selected Action carried.

- A collapsible strip above the tabs shows the Action payload JSON (collapsed to one line by default).
- Payloads containing `$error` show the error.

## UI-7 — Live updates and follow mode

As a developer, I want new Actions to appear instantly and to keep watching the latest one.

- New Sessions, status changes and Actions appear without reloading (via registry events, IN-7).
- Follow mode is on by default: the newest Action is selected and scrolled into view as it arrives.
- Selecting any older Action turns follow mode off; new Actions then increment a "▼ N new" pill instead of moving the selection.
- Clicking the pill selects the newest Action and turns follow mode back on.
- A burst of 5,000 Actions doesn't freeze the UI (renders are throttled to ~10 per second).

## UI-8 — Clear Session

As a developer, I want to clear a Session's history to start fresh.

- The Clear button removes the selected Session after a confirmation.
- If the Session is still connected, it reappears with fresh history on the next Snapshot (e.g. after a reconnect). Until then, the UI says "Cleared; waiting for next Snapshot".
- The cleared Session disappears from every open Viewer tab.

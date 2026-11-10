# Stories

Grouped by epic. Vocabulary follows [GLOSSARY.md](../../GLOSSARY.md); design in [architecture.md](../architecture.md).

| Epic | File |
|---|---|
| Packaging (PK) | [packaging.md](packaging.md) |
| Middleware (MW) | [middleware.md](middleware.md) |
| Ingest (IN) | [ingest.md](ingest.md) |
| Viewer UI (UI) | [viewer-ui.md](viewer-ui.md) |

## Suggested build order

Thin vertical slice first, then hardening:

1. **PK-1** solution skeleton → **IN-1** protocol contracts → **PK-2** AutoSample
2. **MW-1**, **MW-2**, **MW-3**, **MW-5** → **IN-2**, **IN-3**, **IN-4** → **UI-1**, **UI-2**, **UI-3**, **UI-4**, **UI-7**. At this point the end-to-end slice works.
3. **UI-5** diff, **UI-6** payload, **UI-8** clear
4. Hardening: **MW-4**, **MW-6**, **IN-5**, **IN-6**, **MW-7**, **MW-8**
5. Ship: **PK-3**, **PK-4**, **PK-5**

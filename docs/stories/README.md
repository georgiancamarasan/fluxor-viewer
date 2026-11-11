# Stories

Stories live as issues in Gitea: <http://git.server.rsp:32770/georgian/fluxor-viewer/issues>. The issue title starts with the story ID (e.g. `MW-2: …`). Vocabulary follows [GLOSSARY.md](../../GLOSSARY.md); design in [architecture.md](../architecture.md).

- **Epics** are labels: `epic/packaging` (PK), `epic/middleware` (MW), `epic/ingest` (IN), `epic/viewer-ui` (UI).
- **Build order** is the milestones, worked in sequence: 1 · Skeleton → 2 · End-to-end slice → 3 · Inspection → 4 · Hardening → 5 · Ship. [Milestones](http://git.server.rsp:32770/georgian/fluxor-viewer/milestones) show progress.
- **Acceptance criteria** are the checklist in each issue. Close the issue when it's done, with a comment naming the commit.

```sh
tea issues ls                                  # open stories
tea issues ls --milestone "2 · End-to-end slice"
tea issues ls --state closed                   # done
```

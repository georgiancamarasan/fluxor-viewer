# Browser tools

Playwright, for driving the Viewer and `samples/AutoSample` in a headless browser during development (screenshots,
checking a change in the real app). It isn't part of the build or the tests.

```sh
cd tools/browser
npm ci                          # first time
npx playwright install chromium # first time per machine; browsers live in ~/.cache/ms-playwright
node screenshot.mjs http://localhost:5299 viewer.png [--dark] [--wait "[data-test=state]"]
```

`fake-session.mjs` plays a fake host app (one Session, a Snapshot, then Actions as fast as the hub takes them) for
checking the Viewer under load by hand; it needs only Node, not Playwright:

```sh
node fake-session.mjs [--url http://localhost:5299] [--actions 5000] [--batch 1] [--delay 0] [--app FakeApp] [--resync-every 0]
```

`--resync-every N` drops 3 Actions after every N sent and reports them in a Snapshot, so the Viewer records a Resync;
it only drops at the start of a batch, so with `--batch` above 1 it fires at the first batch boundary after each N.

`auto-sample.mjs` checks `samples/AutoSample` against a running Viewer (MW-8): one Server Session on the first visit
(none from prerendering), a WebAssembly Session after the reload, the Server one disconnected within 10 s, the Viewer's
picker following it, and no console errors (e.g. CORS). Run it once per launch profile, since the `https` profile
redirects http to https. `--app <AppName>` checks another InteractiveAuto Host app instead, such as a fresh
`dotnet new blazor --interactivity Auto` app set up by following the root README (PK-5):

```sh
dotnet run --project src/FluxorViewer.Server                                      # the Viewer, on 5299
dotnet run --project samples/AutoSample/AutoSample --launch-profile http          # then: node auto-sample.mjs http://localhost:5151
dotnet run --project samples/AutoSample/AutoSample --launch-profile https         # then: node auto-sample.mjs https://localhost:7104
```

Scripts that need more (clicking through the sample, say) can `import { chromium } from "playwright"` from here.

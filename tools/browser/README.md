# Browser tools

Playwright, for driving the Viewer and `samples/AutoSample` in a headless browser during development (screenshots,
checking a change in the real app). It isn't part of the build or the tests.

```sh
cd tools/browser
npm ci                          # first time
npx playwright install chromium # first time per machine; browsers live in ~/.cache/ms-playwright
node screenshot.mjs http://localhost:5299 viewer.png [--dark] [--wait "[data-test=state]"]
```

Scripts that need more (clicking through the sample, say) can `import { chromium } from "playwright"` from here.

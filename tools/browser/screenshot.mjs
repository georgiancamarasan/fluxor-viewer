// Usage: node screenshot.mjs <url> <out.png> [--dark] [--wait <selector>]
import { chromium } from "playwright";

const [url, out, ...rest] = process.argv.slice(2);
if (!url || !out) {
    console.error("Usage: node screenshot.mjs <url> <out.png> [--dark] [--wait <selector>]");
    process.exit(2);
}
const dark = rest.includes("--dark");
const wait = rest.includes("--wait") ? rest[rest.indexOf("--wait") + 1] : null;

const browser = await chromium.launch();
try {
    const page = await browser.newPage({ colorScheme: dark ? "dark" : "light", viewport: { width: 1280, height: 800 } });
    page.on("console", m => m.type() === "error" && console.error(`console: ${m.text()}`));
    await page.goto(url, { waitUntil: "networkidle" });
    if (wait)
        await page.waitForSelector(wait);
    await page.screenshot({ path: out });
} finally {
    await browser.close();
}

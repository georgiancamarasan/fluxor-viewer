// Usage: node auto-sample.mjs [<sample url>] [--viewer http://localhost:5299] [--app AutoSample]
//
// --app checks another InteractiveAuto Host app reporting under that AppName instead, e.g. the README walked through on a
// fresh `dotnet new blazor --interactivity Auto` app (PK-5).
//
// Checks samples/AutoSample against a running Viewer (MW-8): the first visit makes exactly one connected
// AutoSample · Server Session (none from prerendering); once WebAssembly has downloaded, a reload makes an
// AutoSample · WebAssembly Session, the Server Session shows disconnected within 10 s, and the Viewer's picker follows
// the reload. Fails on any console error in the sample (e.g. CORS). Exits 0 when every check passes, 1 otherwise.
import { chromium } from "playwright";

const args = process.argv.slice(2);
const option = name => (args.includes(name) ? args[args.indexOf(name) + 1] : null);
const sampleUrl = args.find(a => !a.startsWith("--") && a !== option("--viewer") && a !== option("--app")) ?? "http://localhost:5151";
const viewerUrl = option("--viewer") ?? "http://localhost:5299";
const appName = option("--app") ?? "AutoSample";

const failures = [];
const check = (ok, message) => {
    console.log(`${ok ? "ok  " : "FAIL"} ${message}`);
    if (!ok)
        failures.push(message);
};

/** Waits up to `ms` for `condition` to return something truthy, and returns it (or null on timeout). */
async function until(condition, ms) {
    const end = Date.now() + ms;
    while (Date.now() < end) {
        const result = await condition();
        if (result)
            return result;
        await new Promise(r => setTimeout(r, 250));
    }
    return null;
}

const browser = await chromium.launch();
try {
    // The Viewer, watched in its own page. Option labels read "● AutoSample · Server · 3f2a".
    const viewer = await browser.newPage();
    await viewer.goto(viewerUrl, { waitUntil: "networkidle" });
    const sessions = async () => (await viewer.locator("[data-test=session-option]").evaluateAll(options =>
        options.map(o => ({ id: o.value, label: o.textContent.trim() })))).map(s => ({
            ...s,
            connected: s.label.startsWith("●"),
            app: s.label.slice(2).split(" · ")[0],
            runtime: s.label.split(" · ")[1],
        }));
    const before = new Set((await sessions()).map(s => s.id));
    const newSessions = async () => (await sessions()).filter(s => !before.has(s.id));

    // The sample, in a fresh context so WebAssembly isn't cached yet and the first visit runs on a Server circuit.
    const context = await browser.newContext({ ignoreHTTPSErrors: true });
    const sample = await context.newPage();
    const errors = [];
    sample.on("console", m => m.type() === "error" && errors.push(m.text()));
    sample.on("pageerror", e => errors.push(e.message));
    const webAssemblyDownloaded = sample.waitForResponse(r => /dotnet\.native\..*wasm$/.test(new URL(r.url()).pathname), { timeout: 60_000 });

    console.log(`Sample ${sampleUrl}, Viewer ${viewerUrl}`);
    await sample.goto(sampleUrl, { waitUntil: "networkidle" });
    const server = await until(async () => (await newSessions()).find(s => s.connected && s.runtime === "Server"), 15_000);
    check(server?.app === appName, `first visit: a connected ${appName} · Server Session (${server?.label ?? "none"})`);
    await new Promise(r => setTimeout(r, 2_000)); // Give a stray prerender Session time to show up.
    const first = await newSessions();
    check(first.length === 1, `first visit: exactly one new Session (${first.map(s => s.label).join(", ") || "none"})`);

    if (server) {
        await viewer.selectOption("[data-test=session-picker]", server.id);
        await webAssemblyDownloaded;
        await sample.waitForLoadState("networkidle");
        await sample.reload({ waitUntil: "networkidle" });

        const webAssembly = await until(async () => (await newSessions()).find(s => s.connected && s.runtime === "WebAssembly"), 30_000);
        check(webAssembly?.app === appName, `reload: a connected ${appName} · WebAssembly Session (${webAssembly?.label ?? "none"})`);
        const serverGone = await until(async () => (await newSessions()).some(s => s.id === server.id && !s.connected), 10_000);
        check(serverGone, "reload: the Server Session shows disconnected within 10 s");
        const extra = (await newSessions()).filter(s => s.id !== server.id && s.id !== webAssembly?.id);
        check(extra.length === 0, `reload: no other new Sessions (${extra.map(s => s.label).join(", ") || "none"})`);
        if (webAssembly) {
            const followed = await until(async () => (await viewer.inputValue("[data-test=session-picker]")) === webAssembly.id, 5_000);
            check(followed, "reload: the Viewer's picker follows to the WebAssembly Session");
        }
    }

    check(errors.length === 0, `no console errors in the sample${errors.length ? `:\n  ${errors.join("\n  ")}` : ""}`);
} finally {
    await browser.close();
}

console.log(failures.length ? `\n${failures.length} check(s) failed.` : "\nAll checks passed.");
process.exit(failures.length ? 1 : 0);

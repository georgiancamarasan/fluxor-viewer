# Fluxor Viewer

A debugging tool for Blazor apps that use [Fluxor](https://github.com/mrpmorris/Fluxor). Your app (the **Host app**) reports every Action it dispatches and the resulting Feature states to the **Viewer**, a separate local app where you inspect them in real time, with no browser extension. Each running store is a **Session**: one per Blazor Server circuit, and one per browser tab running WebAssembly.

## Getting started

You need an app that already uses Fluxor, the .NET 10 SDK and Node. Everything builds from source on your own machine and installs from a local folder, so nothing is downloaded and run apart from packages restored from your configured NuGet and npm sources. On Windows, run the shell commands in Git Bash.

The steps below are for an InteractiveAuto app (a server project and a `.Client` project). For a Server-only or WebAssembly-only app, use your one project wherever they say `.Client`.

### 1. Build the packages

```sh
git clone <this repository> fluxor-viewer
cd fluxor-viewer
tools/pack.sh
```

This packs the Viewer tool and the middleware into `artifacts/packages/`.

### 2. Install and start the Viewer

```sh
dotnet tool install -g fluxor-viewer --add-source /path/to/fluxor-viewer/artifacts/packages --prerelease
fluxor-viewer
```

It prints `Fluxor Viewer: http://localhost:5299`. Open that URL: the Viewer waits for a Host app to connect. Leave it running.

If `fluxor-viewer` is blocked or the install fails, see [Troubleshooting](#the-tool-is-blocked-or-the-install-fails-with-nu1301).

### 3. Add the middleware to your app

Add the local folder as a NuGet source (once per machine), then the package to your `.Client` project. The server project gets it through its reference to `.Client`.

```sh
dotnet nuget add source /path/to/fluxor-viewer/artifacts/packages -n fluxor-viewer-local
dotnet add MyApp.Client package FluxorViewer.Middleware --version "0.1.0-dev.*"
```

With central package management, a floating version like this also needs `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>` in `Directory.Packages.props`.

### 4. Report the store, in Development only

Add `UseFluxorViewer` to your `AddFluxor` call. In an InteractiveAuto app, put the registration in the `.Client` project so both halves share it, and let each half say whether it's in Development:

```csharp
// MyApp.Client/AppFluxor.cs
using Fluxor;
using Microsoft.Extensions.DependencyInjection;

public static class AppFluxor
{
    public static IServiceCollection AddAppFluxor(this IServiceCollection services, bool isDevelopment) =>
        services.AddFluxor(o => o
            .ScanAssemblies(typeof(AppFluxor).Assembly)
            .UseFluxorViewer(v =>
            {
                v.Enabled = isDevelopment;
                v.AppName = "MyApp"; // One name for both halves; see Troubleshooting.
            }));
}
```

```csharp
// MyApp/Program.cs (server)
builder.Services.AddAppFluxor(builder.Environment.IsDevelopment());

// MyApp.Client/Program.cs (WebAssembly)
builder.Services.AddAppFluxor(builder.HostEnvironment.IsDevelopment());
```

### 5. Run your app

Run the app as usual and open it. The Viewer shows a Session for it and its Actions as you use the page. In an InteractiveAuto app the first visit runs on a Server circuit; once WebAssembly has downloaded, a reload shows a second Session from WebAssembly.

Nothing showing up? See [Troubleshooting](#troubleshooting).

## Why only in Development

`Enabled` is false unless you set it, and should stay false outside Development:

- **The Viewer has no authentication**, and the Host app sends it every Action and the full state of every Feature, which can include tokens and personal data. The Viewer only listens on `localhost`, but anything on that machine can read it.
- **Recording costs time and memory**: each Action's changed Features are kept until they're sent, and serialised to JSON.
- **In WebAssembly the code runs in your user's browser**, so a production build would try to connect to a Viewer on *their* `localhost`.

With `Enabled` false, `UseFluxorViewer` registers nothing: no connection, no serialisation.

## Options

| Option | Default | Change it when |
| --- | --- | --- |
| `Enabled` | `false` | Always set it, to `IsDevelopment()`. |
| `ServerUrl` | `http://localhost:5299` | The Viewer runs on another port (`fluxor-viewer --port`). Set it in the shared registration so WebAssembly gets it too. |
| `AppName` | The entry assembly's name | Always, in an InteractiveAuto app, so both halves show as one Host app. |
| `BufferSize` | `1000` | The Viewer often shows Resyncs because Actions were dropped while it was slow or not running. It's the most Actions waiting to be sent; beyond it the oldest are dropped. |
| `MaxDepth` | `64` | A Payload or Feature state nests deeper than this and shows as a Serialisation error. 1 to 1024. |
| `MaxValueSize` | 8 MB | A large Payload or Feature state shows as a Serialisation error naming its size. Up to 32 MB, the largest message the Viewer accepts. |
| `IgnoreAction(predicate)` | none | An Action fires so often it drowns out the rest, e.g. `v.IgnoreAction(a => a is TimerTickAction)`. Its state changes are reported with the next reported Action. |

The Viewer's own options are listed by `fluxor-viewer --help`: `--port` (default 5299), `--history` (entries kept per Session, default 1000) and `--disconnected` (disconnected Sessions kept, default 10). If you change `--port`, change `ServerUrl` to match.

## Updating

Pull, run `tools/pack.sh` again, then update both sides from the same pack:

```sh
dotnet tool update -g fluxor-viewer --add-source /path/to/fluxor-viewer/artifacts/packages --prerelease
dotnet restore    # in your app: the floating version picks up the new middleware
```

Every pack is a new `0.1.0-dev.<timestamp>` version, so restoring never gets a stale cached copy. `tools/pack.sh --clean` packs plain `0.1.0`, which outranks every `-dev` build in the folder.

## Troubleshooting

### Seeing what's happening

The first time the middleware can't connect to the Viewer, it logs a warning:

```
FluxorViewer: couldn't connect to the Viewer at http://localhost:5299 (Connection refused (localhost:5299)); retrying, and Actions are kept until it connects. Is fluxor-viewer running, and does ServerUrl match the URL it printed?
```

It keeps retrying in the background, and later failures are logged at Debug. Server-side, the warning is in your app's console; from WebAssembly, it's in the browser's developer console. For every connection attempt, turn on Debug logging for the middleware in either `Program.cs`:

```csharp
builder.Logging.AddFilter("FluxorViewer", LogLevel.Debug);
```

The Viewer logs the Sessions it rejects to its own console.

### Nothing appears in the Viewer

- **`Enabled` is false in this environment.** Check that the app runs in Development: `ASPNETCORE_ENVIRONMENT=Development` for the server. WebAssembly takes its environment from the server that hosts it.
- **The Viewer isn't running,** or it's on another port. `fluxor-viewer` prints its URL on start; `ServerUrl` must match it.
- **The Session is there but not selected.** The Viewer selects a Session by itself only while there's one; with several, pick yours from the Session list in its toolbar.

Actions dispatched before the Viewer is reachable are kept (up to `BufferSize`) and sent once it connects.

### CORS errors in the browser console

The Viewer only accepts WebAssembly connections from pages served on `localhost`, `127.0.0.1` or `[::1]`. If you open your app through a machine name, a custom hostname, a dev tunnel or a container's address, the browser blocks the connection: the browser console names CORS, while the middleware's warning only says it couldn't connect. Open the app through `localhost` instead. The Viewer has no option to widen this: without authentication, it's what keeps other sites out.

### The Server Session connects but the WebAssembly one doesn't

WebAssembly code runs in the browser, so `ServerUrl` is resolved from the browser's machine, and the `.Client` project's registration is the one that counts:

- If you changed `ServerUrl` only in the server's `Program.cs`, WebAssembly still uses the default. Set it in the shared registration.
- If `ServerUrl` comes from configuration, WebAssembly reads `wwwroot/appsettings.json` in the `.Client` project, not the server's `appsettings.json`.
- If the browser runs on another machine than the Viewer (a remote desktop, a VM), `localhost` is the wrong machine. Run the browser where the Viewer runs.

### "protocol version … is not compatible"

The middleware and the Viewer come from builds with different protocol versions. The Viewer rejects the Session and logs why, and your app logs:

```
FluxorViewer: the Viewer rejected Session …: protocol version 2.0 is not compatible with this Viewer's 1.0. Use the same major version of FluxorViewer.Middleware and the Viewer.
```

Update both from the same pack, as in [Updating](#updating), and restart the Viewer and your app.

### One app shows as two Host apps

`AppName` defaults to the entry assembly's name, which differs between the halves of an InteractiveAuto app (`MyApp` and `MyApp.Client`). Set `AppName` in the shared registration.

### The tool is blocked or the install fails with NU1301

**`fluxor-viewer` is blocked.** A global tool starts through a generated `fluxor-viewer.exe` in `%USERPROFILE%\.dotnet\tools`, which application control such as AppLocker may block. Install it as a local tool instead, in a folder of your choice, and it runs through `dotnet` itself:

```sh
dotnet new tool-manifest
dotnet tool install fluxor-viewer --add-source /path/to/fluxor-viewer/artifacts/packages --prerelease
dotnet fluxor-viewer            # from this folder or one below it; options as above
```

**The install fails with NU1301.** NuGet also asks your other configured feeds for the package, and fails when one can't be reached, for example an Azure DevOps feed whose credentials expired. Add `--ignore-failed-sources` to install from the local folder alone.

**Checking a build.** `tools/check-tool.sh` installs the last packed tool into a temporary folder, starts it, and checks that it serves its page.

## Further reading

- [GLOSSARY.md](GLOSSARY.md): the words used here and in the Viewer (Session, Resync, History limit, ...).
- [docs/architecture.md](docs/architecture.md): how the middleware, the protocol and the Viewer work.
- [docs/adr/](docs/adr/): the decisions behind the design:
  - [0001](docs/adr/0001-own-viewer-instead-of-redux-devtools.md) Build our own viewer instead of using Fluxor's Redux DevTools integration
  - [0002](docs/adr/0002-changed-feature-deltas-with-snapshot-resync.md) Middleware sends changed Features only; the Viewer reconstructs state, resyncing from Snapshots
  - [0003](docs/adr/0003-read-only-one-way-reporting.md) The Viewer is read-only; the middleware never accepts commands
  - [0004](docs/adr/0004-separate-sessions-per-store-under-interactive-auto.md) Under InteractiveAuto, the Server and WebAssembly stores are separate Sessions
  - [0005](docs/adr/0005-snapshot-from-the-base-never-rewinds.md) A Snapshot is taken from the base, not the current state, and never rewinds
- [docs/stories/](docs/stories/README.md): where the work is planned.
- [samples/AutoSample](samples/AutoSample) and [tools/browser](tools/browser/README.md): a sample InteractiveAuto app and the scripts that check it.

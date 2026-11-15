# fluxor-viewer

Fluxor middleware and server that can show fluxor state and actions history.

## Using a local build

Everything builds from source on your own machine and installs from the `artifacts/packages/` folder, so nothing is downloaded and run apart from packages restored from your configured NuGet and npm sources. Building needs the .NET 10 SDK and Node; on Windows, run the scripts in Git Bash.

### The middleware

Pack the middleware into `artifacts/packages/`, then add that folder as a NuGet source in your Host app:

```sh
tools/pack.sh                 # FluxorViewer.Middleware + FluxorViewer.Protocol, 0.1.0-dev.<UTC timestamp>
dotnet nuget add source /path/to/fluxor-viewer/artifacts/packages -n fluxor-viewer-local
dotnet add package FluxorViewer.Middleware --version "0.1.0-dev.*"
```

Every pack is a new version, so restoring picks up your latest build instead of a cached one. With central package management, a floating version also needs `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>`. `tools/pack.sh --clean` packs plain `0.1.0`, which outranks every `0.1.0-dev.*` build in the feed.

### The Viewer

`tools/pack.sh` also packs the Viewer as the `fluxor-viewer` tool. Install it from the same folder and start it:

```sh
tools/pack.sh
tools/check-tool.sh           # optional: installs it into a temporary folder and checks it serves its page
dotnet tool install -g fluxor-viewer --add-source /path/to/fluxor-viewer/artifacts/packages --prerelease
fluxor-viewer                 # http://localhost:5299; fluxor-viewer --help lists the options
```

After packing a newer build, `dotnet tool update -g fluxor-viewer --add-source /path/to/fluxor-viewer/artifacts/packages --prerelease` installs it.

**If `fluxor-viewer` is blocked.** A global tool starts through a generated `fluxor-viewer.exe` in `%USERPROFILE%\.dotnet\tools`, which application control such as AppLocker may block. Install it as a local tool instead, in a folder of your choice, and it runs through `dotnet` itself:

```sh
dotnet new tool-manifest
dotnet tool install fluxor-viewer --add-source /path/to/fluxor-viewer/artifacts/packages --prerelease
dotnet fluxor-viewer            # from this folder or one below it; options as above
```

**If the install fails with NU1301.** NuGet also asks your other configured feeds for the package, and fails when one can't be reached, for example an Azure DevOps feed whose credentials expired. Add `--ignore-failed-sources` to install from the local folder alone.

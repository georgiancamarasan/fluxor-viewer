# fluxor-viewer

Fluxor middleware and server that can show fluxor state and actions history.

## Using a local build

Pack the middleware into `artifacts/packages/`, then add that folder as a NuGet source in your Host app:

```sh
tools/pack.sh                 # FluxorViewer.Middleware + FluxorViewer.Protocol, 0.1.0-dev.<UTC timestamp>
dotnet nuget add source /path/to/fluxor-viewer/artifacts/packages -n fluxor-viewer-local
dotnet add package FluxorViewer.Middleware --version "0.1.0-dev.*"
```

Every pack is a new version, so restoring picks up your latest build instead of a cached one. With central package management, a floating version also needs `<CentralPackageFloatingVersionsEnabled>true</CentralPackageFloatingVersionsEnabled>`. `tools/pack.sh --clean` packs plain `0.1.0`, which outranks every `0.1.0-dev.*` build in the feed.

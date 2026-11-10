# Epic: Packaging (PK)

## PK-1 — Solution skeleton and CI

As a contributor, I want the solution laid out with build and test automation so that every change is verified.

- `FluxorViewer.sln` contains `src/FluxorViewer.Protocol`, `src/FluxorViewer.Middleware`, `src/FluxorViewer.Server`, `samples/AutoSample` (Server + `.Client`), `tests/FluxorViewer.Middleware.Tests`, `tests/FluxorViewer.Server.Tests`.
- All projects target `net10.0`; Middleware references Fluxor 6.x and Protocol.
- `Directory.Build.props` enables nullable, warnings-as-errors and central package versions.
- CI runs `dotnet build` and `dotnet test` on every push and PR.

## PK-2 — InteractiveAuto sample app

As a contributor, I want a realistic sample app so I can test the middleware end to end by hand.

- `samples/AutoSample` is a Blazor Web App with `InteractiveAuto` render mode and Fluxor registered on both Server and `.Client`.
- It has at least three Features: a counter, a list loaded through an Effect, and one whose state contains a non-serialisable member (for MW-4).
- It has a button that dispatches a burst of 5,000 Actions (for MW-5/MW-6).
- `UseFluxorViewer` is enabled only in Development.

## PK-3 — NuGet packages for Middleware and Protocol

As an app developer, I want to install the middleware from NuGet.

- `dotnet pack` produces `FluxorViewer.Middleware` and `FluxorViewer.Protocol` packages with README, license, repository URL and SourceLink.
- Package versions come from one place (tag-based) and the two packages always ship with the same version.
- A CI job packs on tag; publishing needs a manually provided API key secret.

## PK-4 — Viewer as a dotnet tool

As an app developer, I want to start the Viewer with one command.

- `dotnet tool install -g fluxor-viewer` installs it; `fluxor-viewer` starts it.
- It binds to `localhost` only, defaults to port 5299, and prints the URL on start.
- `--port` and `--history` (per-Session cap, default 1000) are supported; `--help` documents them.
- If the port is taken, it exits with a clear message and a non-zero code.

## PK-5 — Getting-started documentation

As an app developer, I want a short guide so I can be up and running in five minutes.

- The root README covers installing the tool, adding the package to both Server and `.Client` projects, `UseFluxorViewer` options, and why to enable it only in Development.
- It has a troubleshooting section covering CORS, the wrong `ServerUrl` from WebAssembly, and protocol version mismatch.
- It links to the glossary, architecture and ADRs.

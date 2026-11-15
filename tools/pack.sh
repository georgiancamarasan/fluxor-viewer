#!/usr/bin/env bash
# Usage: tools/pack.sh [--clean]
#
# Packs FluxorViewer.Middleware and FluxorViewer.Protocol into artifacts/packages/, the local feed (PK-3).
# Both packages get one -dev.<UTC timestamp> suffix, computed once here so Middleware's dependency names the Protocol
# package packed beside it, and so a re-pack is a new version the NuGet cache can't serve stale.
# --clean packs plain VersionPrefix (src/Directory.Build.props) instead.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"

case "${1:-}" in
    "") version_args=("-p:VersionSuffix=dev.$(date -u +%Y%m%d%H%M%S)") ;;
    --clean) version_args=("-p:CleanVersion=true") ;;
    *) echo "tools/pack.sh takes no argument or --clean, not \"$1\"." >&2; exit 2 ;;
esac

dotnet pack "$root/FluxorViewer.sln" --configuration Release "${version_args[@]}"

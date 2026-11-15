#!/usr/bin/env bash
# Usage: tools/check-tool.sh
#
# Checks the fluxor-viewer tool that tools/pack.sh last packed into artifacts/packages/ (PK-4): installs it into a new
# temporary folder, runs --help, then starts it and checks the page and the files it loads are served and not empty.
# Catches a broken package (missing wwwroot files, wrong command name) before it's installed for real.
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
feed="$root/artifacts/packages"

package="$(ls -t "$feed"/fluxor-viewer.*.nupkg 2>/dev/null | head -n 1)"
if [[ -z "$package" ]]; then
    echo "No fluxor-viewer package in $feed. Run tools/pack.sh first." >&2
    exit 1
fi
version="$(basename "$package" .nupkg)"
version="${version#fluxor-viewer.}"

tool_path="$(mktemp -d)"
echo "Installing fluxor-viewer $version into $tool_path"
dotnet tool install fluxor-viewer --tool-path "$tool_path" --add-source "$feed" --version "$version"

"$tool_path/fluxor-viewer" --help

port=$((20000 + RANDOM % 10000))
url="http://localhost:$port"
"$tool_path/fluxor-viewer" --port "$port" &
viewer=$!
trap 'kill "$viewer" 2>/dev/null || true' EXIT

for _ in $(seq 100); do
    curl --silent --output /dev/null "$url/" && break
    sleep 0.2
done

# Fails on a non-2xx status or an empty body; a missing static file is served as an empty 200 when wwwroot is lost.
check() {
    local size
    size="$(curl --silent --fail --output /dev/null --write-out '%{size_download}' "$url/$1")" ||
        { echo "GET /$1 failed." >&2; exit 1; }
    [[ "$size" -gt 0 ]] || { echo "GET /$1 was empty." >&2; exit 1; }
    echo "GET /$1: $size bytes"
}

check ""
page="$(curl --silent --fail "$url/")"
for asset in $(grep -oE '(href|src)="[^"]+\.(css|js)"' <<<"$page" | sed -E 's/^(href|src)="(.*)"$/\2/'); do
    check "$asset"
done
# Loaded by the Diff tab's module, not by the page.
check "Components/DiffPane.razor.js"
check "lib/jsondiffpatch/index.js"
check "lib/jsondiffpatch/formatters/html.js"

echo "fluxor-viewer $version works."

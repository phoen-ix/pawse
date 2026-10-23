#!/usr/bin/env bash
# Regenerate the docs screenshots (docs/screenshots/):
#   site-*.png  the website, web/site: served locally, shot by the Playwright tour in e2e/docs/
#   app-*.png   the app's windows: tests/Pawse.Tests/DocsScreenshotTests.cs - Windows only;
#               elsewhere this part is skipped with a note
#
#   scripts/docs-screenshots.sh [outdir]      # default: docs/screenshots
#
# PORT_BASE (default 25950): the site is served on 127.0.0.1:PORT_BASE+2.
# ARTIFACTS: Playwright's test results (default: a temp dir).
# Needs Python for the static server, Node + `npm ci` in e2e/ with
# `npx playwright install chromium`, and on Windows the .NET SDK from global.json.
# The committed set comes from .github/workflows/docs-screenshots.yml (windows-latest).
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
OUT="$(mkdir -p "${1:-docs/screenshots}" && cd "${1:-docs/screenshots}" && pwd)"
ARTIFACTS="${ARTIFACTS:-$(mktemp -d)}"
PORT=$((${PORT_BASE:-25950} + 2))

# Windows has `python`; its `python3` may be the Store stub.
PY=python3
[ "${OS:-}" = Windows_NT ] && PY=python
"$PY" -m http.server "$PORT" --bind 127.0.0.1 --directory web/site >"$ARTIFACTS/http.log" 2>&1 &
SERVER=$!
trap 'kill "$SERVER" 2>/dev/null || true' EXIT
for _ in $(seq 1 50); do
  curl -fsS "http://127.0.0.1:$PORT/" >/dev/null 2>&1 && break
  sleep 0.2
done

(cd e2e && E2E_BASE_URL="http://127.0.0.1:$PORT" DOCS_SCREENSHOT_DIR="$OUT" \
  npx playwright test -c playwright.docs.config.ts --output "$ARTIFACTS/test-results")

if [ "${OS:-}" = Windows_NT ]; then
  # Settings -> About shows the version: the newest release tag, not the dev build's 0.0.0.
  VERSION="$(git describe --tags --abbrev=0 --match 'v[0-9]*.[0-9]*.[0-9]*' 2>/dev/null || echo v0.0.0)"
  DOCS_SCREENSHOT_DIR="$OUT" dotnet test tests/Pawse.Tests/Pawse.Tests.csproj -c Release \
    -p:Version="${VERSION#v}" --filter-trait "Category=Docs"
else
  echo "docs-screenshots: app-*.png need Windows - run .github/workflows/docs-screenshots.yml" >&2
fi
echo "docs-screenshots: $(find "$OUT" -name '*.png' | wc -l) PNGs in $OUT"

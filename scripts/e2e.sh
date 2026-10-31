#!/usr/bin/env bash
# The e2e suite (coverage map: e2e/COVERAGE.md):
#   the website   web/site served locally, Playwright specs in e2e/tests/ - everywhere
#   the app       tests/Pawse.Tests/E2E (xunit, trait Category=E2E) drives the real windows
#                 and the tray icon - Windows only; elsewhere this part is skipped with a note
#
#   scripts/e2e.sh [playwright args]
#
# PORT_BASE (default 25950): the site is served on 127.0.0.1:PORT_BASE+2.
# ARTIFACTS: server log, site.log, Playwright results/traces/report, app.log (default: a temp dir).
# Needs Python for the static server, Node + `npm ci` in e2e/ with
# `npx playwright install chromium`, and on Windows the .NET SDK from global.json.
# CI: ci.yml job `e2e` (windows-latest) runs this whole script.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"
ARTIFACTS="${ARTIFACTS:-$(mktemp -d)}"
mkdir -p "$ARTIFACTS"
ARTIFACTS="$(cd "$ARTIFACTS" && pwd)"
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

# Both halves always run - a site failure must not hide the app suite's result.
site=0
(cd e2e && E2E_BASE_URL="http://127.0.0.1:$PORT" PLAYWRIGHT_HTML_OUTPUT_DIR="$ARTIFACTS/playwright-report" \
  npx playwright test --output "$ARTIFACTS/test-results" "$@") 2>&1 | tee "$ARTIFACTS/site.log" || site=$?

app=0
if [ "${OS:-}" = Windows_NT ]; then
  dotnet test tests/Pawse.Tests/Pawse.Tests.csproj -c Release --filter-trait "Category=E2E" --output Detailed 2>&1 \
    | tee "$ARTIFACTS/app.log" || app=$?
else
  echo "e2e: the app's UI suite (Category=E2E) needs Windows - ci.yml job e2e runs it" >&2
fi

[ "$site" -eq 0 ] && [ "$app" -eq 0 ] || { echo "e2e: failed (site=$site app=$app)" >&2; exit 1; }

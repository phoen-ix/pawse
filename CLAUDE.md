# Pawse

Cat-proof keyboard lock for Windows: C# / .NET 10, WPF windows + a WinForms tray icon.
User docs: README.md. Installer: packaging/README.md. Store build: packaging/msix/README.md.

## Commands

```bash
dotnet restore Pawse.sln -p:EnableWindowsTargeting=true      # Linux: compile-only
dotnet build Pawse.sln -c Release --no-restore -p:EnableWindowsTargeting=true
dotnet format Pawse.sln --verify-no-changes --severity warn  # the CI format gate
dotnet test tests/Pawse.Tests/Pawse.Tests.csproj -c Release  # Windows only
fleet check pawse                                            # build, NSIS compile, format, audit
bash scripts/docs-screenshots.sh [outdir]                    # docs tour, see docs/screenshots/README.md
bash scripts/e2e.sh [playwright args]                        # e2e: site everywhere, app UI on Windows
```

## Invariants

- **Build and run only on Windows.** Linux compiles (type-check) via `EnableWindowsTargeting`;
  the exe, the tests and the app screenshots need the WindowsDesktop runtime, so they run on
  the `windows-latest` CI jobs. `fleet check` reports the test step as skipped.
- **Format gate:** CI fails on `dotnet format --verify-no-changes --severity warn`. Run it
  before committing. `TreatWarningsAsErrors` is on in both projects.
- **Every push to main can cut a release** (`.github/workflows/release.yml`). It reads the
  commit SUBJECTS since the newest `vX.Y.Z` tag: `fix:` -> patch, `feat:` -> minor, `type!:` or
  a `BREAKING CHANGE:` footer -> major, anything else (`docs:`, `chore:`, `ci:`, `test:`) ->
  no release. Pick the prefix with that in mind; Dependabot uses `ci:`/`chore:` on purpose.
  A release builds the zips + three installers, tags, publishes, then commits
  `web/site/latest.json` (the update feed) back to main.
- **Never write the skip-ci marker verbatim in a commit message** - GitHub honours it anywhere
  in the message and silently skips that push's CI and release runs. Only the feed commit
  carries it.
- `web/site/` is pawse.at, served straight from this repo; `latest.json` there is the update
  feed the app trusts for checksums. Never hand-edit it except to set `"auto": false` on a
  bad release. Deployment files for the site live on the server (`web/.gitignore`).
  www.pawse.at is the canonical host: Traefik 301s the apex there, except `/latest.json`, which
  installed copies fetch from the apex directly. Keep such routing notes out of the public HTML.
- Names hard-coded in both `packaging/pawse.nsi` and the app - change together or not at all:
  the mutex `Local\Pawse-single-instance-2b8f9c`, the quit event `Local\Pawse-quit-2b8f9c`
  (`src/Pawse/Core/QuitSignal.cs`), and the uninstall key `Software\...\Uninstall\Pawse`
  (`src/Pawse/Core/Deployment.cs`).
- Store build (`-p:PawseStore=true`): `*.Store.cs` files exist only there; `src/Pawse/Pawse.csproj`'s
  `Compile Remove` list is what exists only outside it (the updater, autostart, uninstall).
- A portable copy writes nothing outside its folder and nothing to the registry - keep it so
  (`src/Pawse/Core/Deployment.cs`, checked by ci.yml installer-smoke).
- Parse and format with `CultureInfo.InvariantCulture`; keep `PredefinedCulturesOnly=false`
  (see the comment in `Pawse.csproj`).

## Docs screenshots

`scripts/docs-screenshots.sh` serves `web/site` on `127.0.0.1:$((PORT_BASE+2))` (PORT_BASE
default 25950; fleet runs use 25950-25959) and runs the Playwright tour in `e2e/` (its own
package: `npm ci` + `npx playwright install chromium`); on Windows it also runs
`tests/Pawse.Tests/DocsScreenshotTests.cs` (trait `Category=Docs`, skipped unless
`DOCS_SCREENSHOT_DIR` is set). The committed PNGs come from the manual
`docs-screenshots.yml` workflow (windows-latest); ci.yml runs both halves as a render check.
No Docker anywhere in this repo.

## E2E

`scripts/e2e.sh` (same port and contract as the docs tour, artifacts under `$ARTIFACTS`) runs
`e2e/tests/site.spec.ts` (Playwright, `e2e/playwright.config.ts`) against `web/site`, and on
Windows `dotnet test --filter-trait Category=E2E`: `tests/Pawse.Tests/E2E/` drives the real
windows through UI Automation peers and the tray through NotifyIcon's callback message, one
test at a time (xunit collection `E2E`, which also forces `Deployment._mode`). ci.yml's
`build` job leaves that trait out; its `e2e` job runs the script. Map: `e2e/COVERAGE.md`.

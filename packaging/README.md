# Pawse installer (NSIS)

`build.bat <version>` (or `./build.sh <version>`) builds **three** installers from the one
script - the `FULL_ONLY` / `MINIMAL_ONLY` defines pick which:

- **`Pawse-Setup-<version>-full.exe`** (all-in-one, ~58 MB) - carries only the
  self-contained `Pawse.exe`, no build-choice page and nothing to install alongside it.
  The one to hand someone who just wants Pawse.
- **`Pawse-Setup-<version>-min.exe`** (true minimal, ~0.5 MB) - carries only
  `Pawse-min.exe`, no build-choice page. For people who know they want the small build.
- **`Pawse-Setup-<version>.exe`** (standard) - bundles both release builds and **asks**
  which to deploy:
  - **Full** - self-contained `Pawse.exe` (~63 MB, runtime bundled, needs nothing).
  - **Minimal** - `Pawse-min.exe` (~0.2 MB); needs the **.NET 8 Desktop Runtime (x64)**.

Wherever the minimal build is deployed, the installer ensures the .NET 8 Desktop Runtime
via `winget` (else points to the download page); the all-in-one build skips that code
entirely. All three offer per-user vs per-machine install, optional
Start Menu / Desktop shortcuts, and a "launch now" finish option; the installed exe is
always named `Pawse.exe`. (Start-at-login is *not* offered here - an elevated per-machine
install would write the admin's `HKCU`. The app owns that setting; see the comment above
`Function .onInit` in `pawse.nsi`.)

## Per-user vs per-machine, and elevation

**Nothing elevates unless per-machine is actually chosen.** The installer manifests as
`asInvoker`, so opening it never raises a UAC prompt — not even for an administrator — and
the default is a per-user install into `$LOCALAPPDATA\Programs\Pawse`, which needs no
privileges at all.

Two script details make that work, and both look odd out of context:

- `RequestExecutionLevel user` sits **after** `!include "MultiUser.nsh"`, overriding the
  `highest` that `MULTIUSER_EXECUTIONLEVEL Highest` emits. `highest` would elevate any
  administrator at launch, before they have chosen anything. The `Highest` define has to
  stay, because `MULTIUSER_PAGE_INSTALLMODE` refuses to compile without it.
- `MULTIUSER_INSTALLMODE_DEFAULT_CURRENTUSER` forces per-user as the preselected option;
  MultiUser otherwise preselects per-machine for anyone holding an admin token.

Stock `MultiUser.nsh` also doesn't merely grey out the per-machine option for a standard
user, it **skips the whole page**, so someone who knows an admin password could never
install machine-wide. `.onInit` works around that by recording the real account type in
`$RealPrivileges` and then telling MultiUser it is `Admin` purely so the choice renders.

If all-users is actually picked, `ElevateForAllUsers` (the page's leave hook) re-launches
with `ExecShell "runas" "$EXEPATH" "/AllUsers"` and quits — that is the only UAC prompt in
the whole flow. The elevated copy holds a genuine admin token, so it never re-enters that
path. Declining UAC falls back to a per-user install and says so.

The uninstaller is `asInvoker` for the same reason, so a machine-wide uninstall — where
even an administrator arrives unelevated — re-launches `$INSTDIR\uninstall.exe` through
`runas` and quits. It targets `$INSTDIR` rather than `$EXEPATH` because NSIS runs
uninstallers from a copy in `$TEMP`, which is not the one to re-launch. If UAC is declined
it explains what is needed instead of failing one delete at a time.

Silent runs never elevate: `/S` with a non-admin token simply installs per-user.

## Closing a running Pawse

Pawse is a tray app with no window, so nothing can send it a `WM_CLOSE`: plain `taskkill`
does nothing, and `taskkill /F` skips `App.OnExit` - the only code that reverts the Win+L
policy value and the Keyboard Filter rules. Both installers therefore:

1. Detect a running instance (the single-instance mutex, plus `tasklist` on both exe names
   so another session or a renamed portable copy is still found).
2. Ask, then signal a named event that the app listens on (`src/Pawse/Core/QuitSignal.cs`),
   and wait up to 10 s for it to exit on its own.
3. Offer Retry (you quit it from the tray) or Ignore (force it) if it is still there.
   Cancel/Abort stops before anything is written or deleted.
4. If even `taskkill` is refused — Pawse was restarted as administrator from its tray menu
   and this installer is not elevated — offer to run just the kill behind a UAC prompt
   (`ExecShellWait "runas"`). There is no elevated way to send the polite quit signal, so
   that path stays a force-close.

Silent runs (`/S`, and the registered `QuietUninstallString`) never show a dialog: they try
the clean quit and then force it, which is what the script always did.

The event and mutex names are hard-coded in **both** `pawse.nsi` and the app - change them
together or the installer silently falls back to asking the user to force the close. A
build older than this channel simply doesn't answer, which lands the user on the Retry
dialog by design.

## Build it

**Releases build themselves.** `.github/workflows/release.yml` publishes both exes,
installs NSIS on the Windows runner, runs the same three `makensis` lines below at the
release version, and attaches all three installers to the GitHub Release next to the zips
- nothing here has to be built or uploaded by hand. `ci.yml` guards it from two sides on
every push: a Linux job compiles all three variants with `-WX`, and a Windows job builds a
real installer and runs the round trip (silent per-user install → start the installed app
→ silent uninstall → assert nothing is left in the registry or on disk).

The steps below are for building one locally - to try a change to `pawse.nsi` without
cutting a release.

1. Install **NSIS** (provides `makensis`) - https://nsis.sourceforge.io (or
   `winget install NSIS.NSIS`). On Linux: `apt install nsis`.
2. From the matching GitHub Release, download **both** zips and extract them into
   this folder so it contains `Pawse.exe` **and** `Pawse-min.exe`:
   - `Pawse-<version>.zip`      -> `Pawse.exe`
   - `Pawse-<version>-min.zip`  -> `Pawse-min.exe`
3. Build **all three** installers from this folder (each script just runs `makensis`
   three times: the standard build, `-DFULL_ONLY` and `-DMINIMAL_ONLY`):

   ```
   build.bat <version>       (Windows)      e.g.  build.bat 0.1.4
   ./build.sh <version>      (Linux/macOS)
   ```

   -> produces `Pawse-Setup-<version>.exe`, `Pawse-Setup-<version>-full.exe` and
   `Pawse-Setup-<version>-min.exe` here. (Or run the three `makensis` lines by hand -
   see the top of `pawse.nsi`.) A single-build installer only needs its own exe present:
   `-DFULL_ONLY` wants `Pawse.exe`, `-DMINIMAL_ONLY` wants `Pawse-min.exe`.
4. Nothing to upload - the release workflow builds and attaches these itself. Do this
   only to try a `pawse.nsi` change locally.

`Pawse.exe`, `Pawse-min.exe`, and `Pawse-Setup-*.exe` are git-ignored - only the
sources (`pawse.nsi`, `build.bat`, `build.sh`, `pawse.ico`, `pawse-icon.py`) are tracked.

## Icon

`pawse.ico` is original artwork (a paw print), generated by `pawse-icon.py`
(requires Pillow: `pip install pillow`; run `python3 pawse-icon.py`). It is not
derived from any vendor emoji font, so there are no attribution/licensing
constraints. It is used for the installer UI and the shortcuts only; the app's
in-tray icon is still drawn at runtime by the app itself.

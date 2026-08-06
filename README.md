# Pawse 🐾

A cat-proof keyboard lock for Windows. One click (or a hotkey) freezes the
keyboard so a cat walking across it can't wreak havoc, close your work, or leave
a modifier "stuck". Unlock it deliberately - a chord, a passphrase, a
hold-to-unlock button, or an auto-timer.

This is a native Windows app (C# / .NET 8, WPF + a WinForms tray).

## Install

Two ways to install, both from the [latest release](https://github.com/phoen-ix/pawse/releases)
(while the repo is private that link needs access - see [pawse.at](https://pawse.at) otherwise):

- **Installer** - run a `Pawse-Setup-<version>*.exe`. Installs per-user by default (no
  admin), offers Start Menu/Desktop shortcuts, and uninstalls cleanly from Windows'
  "Installed apps" - or machine-wide if you'd rather (it asks for administrator rights
  only if that's what you pick). If Pawse is running, installing or uninstalling asks
  before closing it and lets the app shut itself down properly, so its Win+L and
  media-key blocks are always undone.
- **Portable** - grab one zip, unzip it, and run the exe inside; no admin needed.

A paw appears in the system tray either way.

**If you're not sure, take `Pawse-Setup-<version>-full.exe`** - it carries everything
and asks you nothing about builds.

| Installer | Size | Needs anything installed? |
| --- | --- | --- |
| **`Pawse-Setup-<version>-full.exe`** | ~58 MB | **No** - all-in-one, the runtime is inside. |
| **`Pawse-Setup-<version>-min.exe`** | ~0.5 MB | Yes - the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64), fetched via `winget` if it's missing. |
| **`Pawse-Setup-<version>.exe`** | ~58 MB | It asks which of the two builds above to install. |

| Portable | Size | Needs anything installed? |
| --- | --- | --- |
| **`Pawse-<version>.zip`** → `Pawse.exe` | ~58 MB zipped (63 MB unzipped) | **No** - the runtime is bundled. Just run it. |
| **`Pawse-<version>-min.zip`** → `Pawse-min.exe` | ~0.2 MB | Yes - the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64), installed once. |

Same app in every row. `Pawse.exe` is self-contained (bundled + compressed);
`Pawse-min.exe` is a tiny launcher that reuses a runtime you install once. Every
download is built and attached by CI, with `SHA256SUMS.txt` alongside them; the
installers are built from [`packaging/`](packaging/).
(Windows may flag a downloaded file - if an exe won't start, right-click it or the zip →
**Properties → Unblock** first.)

## Use

- **Left-click** the tray paw to lock. While locked a single click only shows a
  balloon hint - **double-click** to unlock, so a stray paw-click can't undo the
  lock. (Prefer the classic single-click toggle? Turn the double-click guard off
  in Settings.)
- **Right-click** for the menu: Lock/Unlock, Settings…, Open config file, Quit
  (Quit asks for confirmation while locked).
- **Settings → Updates** is the only thing in Pawse that goes online. **Check now** looks
  once; if there's a newer release and you agree, an installed Pawse downloads the matching
  installer, verifies its checksum and runs it (a portable copy points you at the download
  page instead). **Check for updates once a day** is off by default, and when on it only
  tells you - installing still takes a deliberate yes. See [Privacy](#privacy).
- Default **lock hotkey**: `Ctrl+L`. Default **unlock chord**: `Ctrl+L` (the same chord toggles lock / unlock).

While locked, a small floating popup shows on your chosen monitor (you can turn it
off, move it, and set its opacity). The desktop stays visible around it.

### Unlock methods (each independently toggleable in Settings)

| Method | Default | Notes |
| --- | --- | --- |
| Keyboard chord | on (`Ctrl+L`) | Any combination you like. |
| Passphrase | off | Type a word; a wrong key restarts it. |
| Mouse hold | on | Hold the popup button (~1.2 s). |
| Auto timer | off | Unlock automatically after N seconds. |

Mouse blocking is **off by default**; enable it in Settings if the cat uses the
mouse too. (With it on, unlock with the keyboard - the hold button needs the mouse.)
Blocking **on-screen keyboards** is off by default as well - see
[What it can and can't block](#what-it-can-and-cant-block).

## Configuration & logs

Both live **next to `Pawse.exe`** (falling back to `%APPDATA%\Pawse` only if that
folder isn't writable):

- `pawse.json` - settings (edit via the Settings window or by hand).
- `pawse.log` - a plain, timestamped log of what the app did.

## What it can and can't block

The global low-level keyboard hook swallows **all physical keys while locked**,
including the F-keys and normal typing, and it clears any stuck modifier on lock
(fixing the "Ctrl seems held → the browser zooms when I scroll" problem).

**On-screen keyboards keep working** - the lock is about the hardware keyboard, so
`osk.exe`, the Windows touch keyboard and third-party ones type as usual while locked,
and the unlock chord can be tapped on them. Turn on "Also block on-screen keyboards
while locked" (**Settings → General**) if you want them frozen too. One caveat worth
knowing: Windows only tells the hook that a keystroke was *simulated*, never which
program simulated it, so leaving this off also lets macro tools type while locked.

**`Win+L`** travels *around* that hook (winlogon locks the screen below the hook),
so Pawse can optionally block it **only while locked** - off by default; turn it on
under **Settings → System keys**. It sets the per-user `DisableLockWorkstation`
policy on lock and restores its previous state on unlock - a value that was
already there before Pawse (e.g. set by an admin) is left exactly as found.
Needs no admin on a normal PC, but on
**managed / corporate** machines the policy key is ACL-locked and needs elevation -
enabling it prompts to **Restart as administrator** (also available in the tray menu,
shown only when Pawse isn't already elevated).

The **browser / calculator / media / volume keys** are already swallowed by the lock on any
edition. The "Block browser / calculator / media keys" option (**Settings → System keys**)
additionally engages the Windows **Keyboard Filter** to catch the few consumer keys that reach
Windows as `WM_APPCOMMAND` and bypass the hook - that part needs **Windows Enterprise /
Education / IoT** with the Keyboard Filter feature and Pawse running as administrator.

**Touch is not the mouse**: even with mouse blocking on, native touch / pen input
reaches pointer-aware apps (browsers, UWP, Office) through the `WM_POINTER`
pipeline, which the low-level mouse hook never sees - on a touchscreen, screen
taps still land.

Still out of reach: input to windows running **as administrator** (unless Pawse
itself runs elevated), and `Ctrl+Alt+Del` and vendor-driver hotkeys, which would
need a signed kernel driver (out of scope). If Pawse is killed while locked, it
sweeps its own leftover `DisableLockWorkstation` state on next start (the
uninstaller does the same), so nothing Pawse blocked stays blocked - while a
block it didn't set is never touched. That sweep is the safety net, not the normal
path: the installer and uninstaller ask Pawse to quit and let it undo its own
blocks, and only force it if you tell them to.

## Privacy

Out of the box Pawse makes **no network connections at all** - no telemetry, no background
check, no phone-home. Exactly one request exists: **Settings → Updates → Check now** fetches
`https://pawse.at/latest.json` and sends nothing but a `Pawse/<version>` user agent (that
request isn't logged on the server). Turning on **Check for updates once a day** makes that
same request run by itself while Pawse is open, at most once every 24 hours - it's off
unless you switch it on, and it never does more than notify.

If it finds a newer release and you say yes, Pawse downloads the matching installer from
GitHub Releases, checks it against the SHA-256 listed in the feed, and only then runs it -
the checksum comes from a different host than the download, so it's a real cross-check.
Decline, or never open that menu item, and Pawse never touches the network at all. Your
settings, log and keystrokes stay on the machine either way.

## Build from source

Built on Windows by CI (`.github/workflows/`). To build yourself:

```powershell
dotnet publish src/Pawse/Pawse.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The project compiles on Linux too (for type-checking) via `EnableWindowsTargeting`;
the runnable exe is produced on Windows. The installers are built from the same
`makensis` lines CI runs - see [`packaging/README.md`](packaging/README.md).

## License

MIT - see [LICENSE](LICENSE).

# Pawse 🐾

A cat-proof keyboard lock for Windows. One click (or a hotkey) freezes the
keyboard so a cat walking across it can't wreak havoc, close your work, or leave
a modifier "stuck". Unlock it deliberately - a chord, a passphrase, a
hold-to-unlock button, or an auto-timer.

This is a native Windows app (C# / .NET 8, WPF + a WinForms tray).

## Install

Two ways to install, both from the [latest release](https://github.com/phoen-ix/pawse/releases)
(or via [pawse.at](https://pawse.at)):

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
| **`Pawse-<version>-min.zip`** → `Pawse-min.exe` | ~0.3 MB | Yes - the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64), installed once. |

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
- **Starting a second copy** - a portable build you just unzipped, say - offers to close the
  one already running and take over, rather than simply refusing. It asks first, and closing
  a Pawse that currently has the keyboard locked hands the keyboard back, so the prompt says
  so. Answer No and the running copy is left alone.
- **Settings → About** is the only thing in Pawse that goes online, and it offers three
  levels: **Only when I ask** (the default - nothing leaves the machine until you press
  **Check now**), **Tell me when an update is ready**, and **Download and install updates
  automatically**. An update is downloaded from GitHub Releases and its checksum
  cross-checked against pawse.at before anything runs; an installed copy runs the matching
  installer, a portable one replaces its own exe and restarts. Automatic installs decline
  anything that would need administrator rights, a runtime download, or a guess about which
  build you have - those still ask. See [Privacy](#privacy).
- Default **lock hotkey**: `Ctrl+L`. Default **unlock chord**: `Ctrl+L` (the same chord toggles lock / unlock).

While locked, a small floating popup shows on the displays you choose - one, several, or
**All displays**, which picks up a monitor you plug in later without you touching anything.
You can also turn it off, move it up or down, and set its opacity. The desktop stays visible
around it.

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
- `pawse.log` - a plain, timestamped log of what the app did. **Off by default** - switch on
  "Write a log file next to Pawse" in **Settings → General** when something needs explaining.
  It never leaves the machine, but it is a plain file on disk, and while diagnosing a stuck
  key it records which keys were held when a lock engaged - so it is opt-in rather than
  something to discover later.

## What it can and can't block

The global low-level keyboard hook swallows **all physical keys while locked**,
including the F-keys and normal typing, and it clears any stuck modifier on lock
(fixing the "Ctrl seems held → the browser zooms when I scroll" problem).

**On-screen keyboards keep working** - the lock is about the hardware keyboard, so
`osk.exe`, the Windows touch keyboard and third-party ones type as usual while locked,
and the unlock chord can be tapped on them. Turn on "Also block on-screen keyboards
while locked" (**Settings → Locking**) if you want them frozen too. One caveat worth
knowing: Windows only tells the hook that a keystroke was *simulated*, never which
program simulated it, so leaving this off also lets macro tools type while locked.

**`Win+L`** travels *around* that hook (winlogon locks the screen below the hook),
so Pawse can optionally block it **only while locked** - off by default; turn it on
under **Settings → Locking**. It sets the per-user `DisableLockWorkstation`
policy on lock and restores its previous state on unlock - a value that was
already there before Pawse (e.g. set by an admin) is left exactly as found.
Needs no admin on a normal PC, but on
**managed / corporate** machines the policy key is ACL-locked and needs elevation -
enabling it prompts to **Restart as administrator** (also available in the tray menu,
shown only when Pawse isn't already elevated).

The **browser / calculator / media / volume keys** are already swallowed by the lock on any
edition. The "Block browser / calculator / media keys" option (**Settings → Locking**)
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
check, no phone-home. The update setting starts at **Only when I ask**, so nothing leaves the
machine until you press **Check now** or move it up a level. Whatever Pawse sends is a
`Pawse/<version>` user agent and nothing else.

A check asks `https://github.com/phoen-ix/pawse/releases/latest` which release is newest -
just the redirect, no API - and reads `https://pawse.at/latest.json` for the checksum. That
request to pawse.at isn't logged on the server; GitHub, being GitHub, logs what it likes.

If there's something newer and you agree, Pawse downloads it from GitHub Releases, checks it
against the SHA-256 pawse.at published, and only then runs it. Two hosts is the point: a
hash served alongside the binary only tells you the transfer wasn't corrupted. Be clear-eyed
about how far that goes, though - this repository serves both the release and the feed, so
the split protects against a compromised CDN edge or a MITM, not against a compromised
source. (Signed binaries would; Pawse doesn't have them yet.) When pawse.at can't vouch for
a release, Pawse falls back to the release's own `SHA256SUMS.txt`, says so before installing,
and refuses to do it unattended.

Decline, or leave the setting alone, and Pawse never touches the network at all. Your
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

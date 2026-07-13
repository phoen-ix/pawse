# Pawse 🐾

A cat-proof keyboard lock for Windows. One click (or a hotkey) freezes the
keyboard so a cat walking across it can't wreak havoc, close your work, or leave
a modifier "stuck". Unlock it deliberately - a chord, a passphrase, a
hold-to-unlock button, or an auto-timer.

This is a native Windows app (C# / .NET 8, WPF + a WinForms tray).

## Install

Grab one zip from the [latest release](https://github.com/phoen-ix/pawse/releases),
unzip it, and run the exe inside - no installer, no admin. A padlock appears in the
system tray.

| Download | Size | Needs anything installed? |
| --- | --- | --- |
| **`Pawse-<version>.zip`** → `Pawse.exe` | ~58 MB zipped (63 MB unzipped) | **No** - the runtime is bundled. Just run it. |
| **`Pawse-<version>-min.zip`** → `Pawse-min.exe` | ~0.2 MB | Yes - the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (x64), installed once. |

Same app either way. `Pawse.exe` is self-contained (bundled + compressed);
`Pawse-min.exe` is a tiny launcher that reuses a runtime you install once.
(Windows may flag a downloaded zip - if the exe won't start, right-click the zip →
**Properties → Unblock** before extracting.)

## Use

- **Left-click** the tray padlock to lock / unlock.
- **Right-click** for the menu: Lock/Unlock, Settings…, Open config file, Quit.
- Default **lock hotkey**: `Ctrl+Alt+L`. Default **unlock chord**: `Ctrl+Shift+U`.

While locked, a small floating popup shows on your chosen monitor (you can turn it
off, move it, and set its opacity). The desktop stays visible around it.

### Unlock methods (each independently toggleable in Settings)

| Method | Default | Notes |
| --- | --- | --- |
| Keyboard chord | on (`Ctrl+Shift+U`) | Any combination you like. |
| Passphrase | off | Type a word; a wrong key restarts it. |
| Mouse hold | on | Hold the popup button (~1.2 s). |
| Auto timer | off | Unlock automatically after N seconds. |

Mouse blocking is **off by default**; enable it in Settings if the cat uses the
mouse too. (With it on, unlock with the keyboard - the hold button needs the mouse.)

## Configuration & logs

Both live **next to `Pawse.exe`** (falling back to `%APPDATA%\Pawse` only if that
folder isn't writable):

- `pawse.json` - settings (edit via the Settings window or by hand).
- `pawse.log` - a plain, timestamped log of what the app did.

## What it can and can't block

The global low-level keyboard hook swallows **all physical keys while locked**,
including the F-keys and normal typing, and it clears any stuck modifier on lock
(fixing the "Ctrl seems held → the browser zooms when I scroll" problem).

**`Win+L`** travels *around* that hook (winlogon locks the screen below the hook),
so Pawse can optionally block it **only while locked** - off by default; turn it on
under **Settings → System keys**. It toggles the per-user `DisableLockWorkstation`
policy on lock and removes it on unlock. Needs no admin on a normal PC, but on
**managed / corporate** machines the policy key is ACL-locked and needs elevation -
enabling it prompts to **Restart as administrator** (also available in the tray menu,
shown only when Pawse isn't already elevated).

Still out of reach: input to windows running **as administrator** (unless Pawse
itself runs elevated), and `Ctrl+Alt+Del` and vendor-driver hotkeys, which would
need a signed kernel driver (out of scope). If Pawse is killed while locked, it
sweeps any leftover `DisableLockWorkstation` state on next start, so nothing stays
blocked.

## Privacy

Pawse is **fully local**. It makes **no network connections** - no telemetry, no
update checks, no phone-home. Updates are manual (download a new exe). Any future
network feature would be strictly opt-in.

## Build from source

Built on Windows by CI (`.github/workflows/`). To build yourself:

```powershell
dotnet publish src/Pawse/Pawse.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

The project compiles on Linux too (for type-checking) via `EnableWindowsTargeting`;
the runnable exe is produced on Windows.

## License

MIT - see [LICENSE](LICENSE).

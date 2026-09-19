# Pawse for the Microsoft Store (MSIX)

The third build, next to the installers and the portable zips: the same app as an MSIX package
for the Microsoft Store. It is the Store flavor of the project (`-p:PawseStore=true`), and it
differs from the other two builds where the Store or packaging requires it:

| | Installer / portable | Store build |
| --- | --- | --- |
| Updates | Pawse's own updater (Settings -> About) | The Store. The updater is **not compiled in** - `UpdateCheck.cs`, `SelfReplace.cs`, `App.Updates.cs` and `SettingsWindow.Updates.cs` are left out of the build (`Pawse.csproj`), and About says "Updates come from the Microsoft Store". |
| Start at sign-in | `HKCU\...\Run` value | The package's StartupTask (`Core/Autostart.Store.cs`). If the user turns it off in Windows (Settings -> Apps -> Startup, or Task Manager), only they can turn it back on there; Pawse says so. |
| Settings and log | Next to the exe, or `%APPDATA%\Pawse` | The package's own data folder, `%LOCALAPPDATA%\Packages\<package family>\LocalState`. It is a real path, so "Open config file" works, and Windows removes it on uninstall. |
| Block Win+L | Writes the HKCU policy value | Same, because `AppxManifest.xml` turns off registry virtualization for HKCU. This build writes nothing else there. |
| Restart as administrator | `runas` on the exe | `runas` on the app execution alias `pawse.exe`, so the elevated copy still runs as the package. |
| Uninstall | The uninstaller reverts Win+L | MSIX runs no uninstall code. The Win+L value is only set while locked. A value left behind by a crash or an uninstall mid-lock is swept by the next start of **any** Pawse, because the marker lives in the exempt `Software\Pawse` key. |

## Build and try it locally

Needs Windows, the .NET SDK from `global.json`, and the Windows SDK, which provides `makepri`
and `makeappx`.

```powershell
.\packaging\msix\build-msix.ps1 -Register
```

This publishes the Store flavor, stages it with `AppxManifest.xml` and `Assets\`, and packs
`packaging\msix\out\Pawse-0.0.0.msix`.

With `-Register` it also installs the staged layout for your account. That needs Developer
Mode (Settings -> System -> For developers) but no certificate. Pawse then appears in Start,
and `pawse` works in Win+R. It runs from `out\stage`, so leave that folder alone while
testing. Running the script again removes that test install (and its settings) before
rebuilding.

To remove it: Settings -> Apps -> Pawse -> Uninstall, or
`Get-AppxPackage Pawse.Dev | Remove-AppxPackage`.

The logos in `Assets\` come from `packaging/pawse-icon.py`, so the icon has one source.

## CI and releases

- **`ci.yml` → `store` job.** Builds the package on every push. It then installs it on the
  runner and checks three things:
  - the package identity and data folder;
  - the Win+L policy value being visible outside the package;
  - the next-start sweep after a hard kill while locked.
- **`release.yml` → `store` job.** Builds `Pawse-<version>.msix` for every release and attaches
  it to the run as the artifact `Pawse-<version>-msix`. It is not a release asset, because
  it's unsigned and only the Store can install it. It is also not in the feed or
  `SHA256SUMS.txt`, because Store copies update through the Store.

## Submitting to the Store

1. **Partner Center:** create the app and reserve the name "Pawse". Under **Product identity**
   it lists three values. Put them in the repo's **variables** (Settings -> Secrets and variables
   -> Actions -> Variables). They are public, so they don't need to be secrets:
   - `STORE_IDENTITY_NAME` = Package/Identity/Name
   - `STORE_PUBLISHER` = Package/Identity/Publisher (`CN=...`)
   - `STORE_PUBLISHER_DISPLAY_NAME` = Package/Properties/PublisherDisplayName

   Without them the release job warns and builds the development identity, which the Store
   refuses.
2. Download the `.msix` from the release run's artifacts and upload it as a new submission. It
   doesn't need signing: Partner Center signs what it publishes.
3. **Restricted capabilities.** The submission asks why each one is needed. Suggested answers:
   - **runFullTrust** - Pawse is a desktop app (WPF). Its purpose, locking the keyboard, is a
     low-level keyboard hook (`WH_KEYBOARD_LL`), plus optionally a mouse hook, which only a
     full-trust process can install. Keystrokes never leave the machine.
   - **allowElevation** - "Restart as administrator", which the user chooses from the tray.
     Two opt-in features need it: blocking Win+L on PCs where the policy key is locked down,
     and blocking browser/media keys through the Windows Keyboard Filter, which is an
     administrator-only WMI provider. Pawse never elevates on its own.
   - **unvirtualizedResources** - Block Win+L (opt-in) sets the documented per-user policy value
     `HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System\DisableLockWorkstation`
     while the keyboard is locked, and removes it on unlock. Windows only honours it in the
     real HKCU, not in the app's virtualized hive, so HKCU write virtualization is off. Pawse
     writes nothing else there: only that value (and the keys it needs, removed again), plus
     `HKCU\Software\Pawse`, which holds the marker used to undo the value after a crash and is
     deleted once empty.

   The schema docs say `unvirtualizedResources` is "intended to be used only by certain types
   of desktop PC games", so this is the capability most likely to be questioned. If it's
   refused, the fallback is to drop it from `AppxManifest.xml` and hide Block Win+L in the
   Store build. The rest of Pawse doesn't depend on it.
4. **Privacy policy URL:** `https://www.pawse.at/privacy.html`. It covers every build,
   including the Store build's "never goes online", and the website's server logs.
5. Screenshots, description, age rating. Keep the listing as plain as the website.

## Test pass on a real machine

This checks what CI can't. Turn logging on first (Settings -> General), then send back
`pawse.log` from the data folder (tray -> Open config file shows where) after:

1. Installing with `build-msix.ps1 -Register` and starting Pawse from Start. The log has a
   `store build:` line with the package name, a `...\Packages\...\LocalState` data folder and
   the startup task state.
2. All four unlock methods.
3. Settings -> About shows the Store line and no Updates section.
4. Tray -> Open config file opens it in the editor.
5. Start at sign-in:
   - Tick it, sign out and back in: Pawse starts.
   - Turn it off in Task Manager -> Startup apps, then tick it in Settings again: a notice
     explains it has to be turned on in Windows.
6. Block Win+L:
   - Lock the keyboard and press Win+L: nothing happens.
   - In a normal command prompt, `reg query HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System /v DisableLockWorkstation` shows `0x1`.
   - Unlock: the value is gone.
7. Tray -> Restart as administrator: UAC appears, then the new `store build:` line shows the
   same package and data folder with `elevated True`.
8. Uninstall while locked with Block Win+L on (Settings -> Apps, using the mouse). The log goes
   with the package, so the registry answers instead:
   - Run the `reg query` from step 6. If the value is gone, Windows let Pawse shut down
     cleanly. If it's still `0x1`, Pawse was ended outright.
   - Then start any Pawse build: the startup sweep removes the value.

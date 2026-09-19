# Pawse on winget

The manifests for `phoen-ix.Pawse` in the Windows Package Manager community repository
([microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs)). Once they're merged, a user
can install and upgrade Pawse like this:

```powershell
winget install phoen-ix.Pawse        # or: winget install pawse
winget upgrade phoen-ix.Pawse
```

It also shows up in front-ends that read winget, such as UniGetUI.

The three manifest files live in `manifest/`, apart from this README: `winget validate` and
`winget install --manifest` read every file in the folder they're given as part of the manifest.

## What the manifest says

- **Installer:** the all-in-one installer `Pawse-Setup-<v>-full.exe`, taken straight from the
  GitHub release. It needs nothing else installed. winget allows one installer per
  architecture and scope, so the minimal build (which needs the .NET runtime) isn't listed.
- **Scope:** both scopes use the same exe with the installer's own switches.
  - `user` (the default) runs `/S /CurrentUser` into `%LocalAppData%\Programs\Pawse`.
  - `machine` runs `/S /AllUsers` into `%ProgramFiles%\Pawse`, with `elevationRequired`,
    because a silent `/AllUsers` run without admin rights exits with code 2 by design.
- **Upgrades:** they only add `/RESTART`. The installer asks a running Pawse to quit (the quit
  channel, so the Win+L block is reverted), and `/RESTART` brings the tray icon back, exactly
  as Pawse's own updater does. A fresh install doesn't start Pawse.
- **ProductCode `Pawse`:** the uninstall key the installer writes. winget uses it to recognise
  an installed copy, including one installed without winget.

## Test on Windows before submitting

```powershell
winget settings --enable LocalManifestFiles     # once, from an admin prompt
winget validate --manifest packaging\winget\manifest
winget install --manifest packaging\winget\manifest      # add --scope machine to try that path
winget uninstall pawse
```

The winget-pkgs repo also has `Tools\SandboxTest.ps1`, which runs the same install inside
Windows Sandbox.

## Submitting

The manifest goes in as a pull request to microsoft/winget-pkgs, at
`manifests/p/phoen-ix/Pawse/<version>/`, with one version per pull request. The PR is public and
permanent. On a first contribution a bot asks you to accept Microsoft's CLA with a comment.
The simplest route is `wingetcreate submit --token <GitHub PAT> packaging\winget\manifest`, which forks
the repo, pushes the files and opens the PR.

Then automated validation runs. Labels on the PR show progress, and after that a moderator
approves it. What could come up:

- **Antivirus heuristics.** Every submission is scanned by several antivirus engines. A global
  keyboard hook is also what keyloggers use, so a false "potentially unwanted" flag is possible.
  It gets disputed with the vendor (the PR label says which).
- **Uninstall check.** Nothing should come up here. Uninstalling removes everything Pawse
  created, settings and registry entries included, for every account (packaging/README.md).

## Later versions

Each release needs a new manifest version. Two options:

- By hand: `wingetcreate update phoen-ix.Pawse --version <v> --urls <installer url> --submit`
  reads the current manifest from winget-pkgs, so nothing here needs editing.
- Automatically: add the [WinGet Releaser](https://github.com/vedantmgoyal9/winget-releaser)
  action to `release.yml`. It opens the update PR under your account on every release. It
  needs your own fork of winget-pkgs and a GitHub token with `public_repo` scope as a secret.

The files in `manifest/` describe v0.15.2, the first release whose uninstaller leaves nothing
behind (and leaves a second, side-by-side install alone), and are what the first submission uses. Later versions don't need them.

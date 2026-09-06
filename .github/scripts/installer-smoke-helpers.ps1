# Shared by the installer-smoke legs in ci.yml (dot-sourced). Kept as a file rather than a
# here-string in the workflow so it reads, lints and diffs like PowerShell.

function Wait-PawseStarted([string]$log, [int]$expectedStarts) {
    # "startup complete" is the last line App.StartupCore logs, so reaching it means the tray
    # icon, the hooks and the config all came up on a machine that has never seen Pawse
    # before. The log is appended to, so a relaunch is the Nth occurrence, not the first.
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        if ((Test-Path $log) -and ((Select-String -Path $log -Pattern 'startup complete' -AllMatches).Matches.Count -ge $expectedStarts)) {
            if (-not (Get-Process Pawse -ErrorAction SilentlyContinue)) { throw "Pawse logged startup then exited" }
            "Pawse reached 'startup complete' (start #$expectedStarts) - log at $log"
            return
        }
        Start-Sleep -Milliseconds 500
    }
    if (Test-Path $log) { Get-Content $log } else { "no pawse.log was written at $log" }
    throw "Pawse did not reach 'startup complete' (start #$expectedStarts)"
}

function Wait-PawseGone([int]$maxSeconds) {
    # The installer and uninstaller ask a running Pawse to quit over the named event
    # (src/Pawse/Core/QuitSignal.cs, QUIT_EVENT in pawse.nsi) and fall back to taskkill only
    # after polling for ten seconds. A clean quit therefore ends the app within a couple of
    # seconds and the fallback only after ten-plus - so the ceiling tells the two apart, and a
    # renamed event turns this job red instead of passing on the fallback.
    $started = Get-Date
    while (Get-Process Pawse -ErrorAction SilentlyContinue) {
        if (((Get-Date) - $started).TotalSeconds -gt 60) { throw "Pawse is still running after 60 s" }
        Start-Sleep -Milliseconds 250
    }
    $took = [math]::Round(((Get-Date) - $started).TotalSeconds, 1)
    "Pawse was gone after $took s"
    if ($took -gt $maxSeconds) { throw "Pawse took $took s to go - that is the taskkill fallback, not the quit channel" }
}

function Wait-Removed([string]$key, [string]$dir) {
    # An NSIS uninstaller re-launches itself from %TEMP%, so the process we started returns
    # at once - poll for the result. The script also removes its own folder through a
    # delayed cmd, a second or two after the rest.
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        if (-not (Test-Path $key) -and -not (Test-Path $dir)) { return }
        Start-Sleep -Milliseconds 500
    }
    if (Test-Path $key) { throw "the uninstall left $key in the registry" }
    if (Test-Path $dir) { throw "the uninstall left $dir behind: $((Get-ChildItem $dir -Force).Name -join ', ')" }
}

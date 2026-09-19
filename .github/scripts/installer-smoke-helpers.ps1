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

# Used by the store job: Block Win+L's policy value as seen from OUTSIDE the package - this
# shell is not the package, so it reads the real HKCU. Null when the value is absent.
function Get-WinLockPolicy {
    (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies\System' `
        -Name DisableLockWorkstation -ErrorAction SilentlyContinue).DisableLockWorkstation
}

function Wait-WinLockPolicy($expected, [int]$seconds) {
    # The lock engages (and the value is written) on a dispatcher turn after "startup
    # complete" is logged, and the next start's sweep likewise - so poll rather than read once.
    $deadline = (Get-Date).AddSeconds($seconds)
    while ((Get-Date) -lt $deadline) {
        if ((Get-WinLockPolicy) -eq $expected) { "DisableLockWorkstation is $(if ($null -eq $expected) { 'absent' } else { $expected })"; return }
        Start-Sleep -Milliseconds 500
    }
    throw "DisableLockWorkstation is '$(Get-WinLockPolicy)', expected '$expected' after $seconds s"
}

# Leave behind what a Pawse killed while locked would (the Win+L value, its markers, the policy
# keys it had to create), plus settings in %APPDATA% and a download in %TEMP% - so an uninstall
# has real traces to remove. %TEMP%\.net\Pawse is there already from the runs before.
# Returns what the policy-key marker says was created (0 = the key was there before).
function Set-PawseTraces {
    $policies = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies'
    $system = "$policies\System"
    $created = if (Test-Path $system) { 0 } elseif (Test-Path $policies) { 1 } else { 2 }
    New-Item 'HKCU:\Software\Pawse' -Force | Out-Null
    if ($created) { New-ItemProperty 'HKCU:\Software\Pawse' -Name PolicyKeysCreated -Value $created -PropertyType DWord -Force | Out-Null }
    New-ItemProperty 'HKCU:\Software\Pawse' -Name PrevDisableLockWorkstation -Value 2 -PropertyType DWord -Force | Out-Null
    if (-not (Test-Path $system)) { New-Item $system | Out-Null }   # -Force would wipe an existing key's values
    New-ItemProperty $system -Name DisableLockWorkstation -Value 1 -PropertyType DWord -Force | Out-Null
    New-Item -ItemType Directory "$env:APPDATA\Pawse" -Force | Out-Null
    Set-Content "$env:APPDATA\Pawse\pawse.json" '{}'
    New-Item -ItemType Directory "$env:TEMP\Pawse-update-smoke" -Force | Out-Null
    Set-Content "$env:TEMP\Pawse-update-smoke\leftover.txt" 'x'
    $created
}

# Everything Pawse can leave in an account, checked from outside it. $policyKeysCreated is what
# Set-PawseTraces returned: keys that exist only because Pawse (or the seeding) created them.
function Assert-NoPawseTraces([int]$policyKeysCreated = 0) {
    $policies = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Policies'
    $left = @()
    if (Test-Path "$env:APPDATA\Pawse") { $left += '%APPDATA%\Pawse' }
    if (Test-Path "$env:TEMP\.net\Pawse") { $left += '%TEMP%\.net\Pawse' }
    if (Get-ChildItem $env:TEMP -Directory -Filter 'Pawse-update-*' -ErrorAction SilentlyContinue) { $left += '%TEMP%\Pawse-update-*' }
    if (Test-Path 'HKCU:\Software\Pawse') { $left += 'HKCU\Software\Pawse' }
    if ($null -ne (Get-WinLockPolicy)) { $left += 'the DisableLockWorkstation value' }
    if (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name Pawse -ErrorAction SilentlyContinue) { $left += 'the Run value' }
    if ($policyKeysCreated -ge 1 -and (Test-Path "$policies\System")) { $left += 'the Policies\System key it created' }
    if ($policyKeysCreated -ge 2 -and (Test-Path $policies)) { $left += 'the Policies key it created' }
    if ($left) { throw "Pawse left behind: $($left -join ', ')" }
    "nothing of Pawse left in this account"
}

<#
.SYNOPSIS
    Builds the Microsoft Store package: publishes the Store flavor of Pawse, stages it with the
    manifest and logos, and packs Pawse-<version>.msix. The same script runs locally and in CI.

.DESCRIPTION
    Needs the .NET SDK (global.json) and the Windows SDK, for makepri.exe and makeappx.exe.
    Output goes to packaging\msix\out: stage\ is the package's loose layout, and the .msix is
    packed from it. The .msix is UNSIGNED - Partner Center signs what it publishes, so this is
    the file you upload. Windows won't install an unsigned .msix directly, so -Register
    installs the loose layout instead (Developer Mode, no certificate needed).

.PARAMETER Version
    x.y.z, as release.yml computes it. Becomes x.y.z.0 in the manifest - the Store reserves the
    fourth part and requires it to be 0. A -dev or other suffix is dropped.

.PARAMETER IdentityName
    Package identity name. For a Store upload it must be the one Partner Center reserved
    (Product identity -> Package/Identity/Name); anything else suits a local test install.

.PARAMETER Publisher
    Package/Identity/Publisher from Partner Center ("CN=..."), for a Store upload.

.PARAMETER PublisherDisplayName
    Package/Properties/PublisherDisplayName from Partner Center, for a Store upload.

.PARAMETER Register
    After packing, install the loose layout for the current user (Add-AppxPackage -Register).
    Needs Developer Mode: Settings -> System -> For developers. Pawse then runs from
    out\stage, so leave that folder in place while testing.

.EXAMPLE
    .\packaging\msix\build-msix.ps1 -Register
#>
[CmdletBinding()]
param(
    [string]$Version = '0.0.0',
    [string]$IdentityName = 'Pawse.Dev',
    [string]$Publisher = 'CN=Pawse Dev',
    [string]$PublisherDisplayName = 'Pawse (development build)',
    [switch]$Register
)

$ErrorActionPreference = 'Stop'

function Invoke-Native([string]$what, [scriptblock]$command) {
    & $command
    if ($LASTEXITCODE -ne 0) { throw "$what failed (exit code $LASTEXITCODE)" }
}

function Find-SdkTool([string]$name) {
    # Newest Windows SDK first. The GitHub windows runners ship several; so may a dev box.
    $bin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin'
    $hit = Get-ChildItem $bin -Directory -Filter '10.*' -ErrorAction SilentlyContinue |
        Sort-Object { [version]$_.Name } -Descending |
        ForEach-Object { Join-Path $_.FullName "x64\$name" } |
        Where-Object { Test-Path $_ } |
        Select-Object -First 1
    if (-not $hit) { throw "$name not found under $bin - install the Windows SDK (it ships makeappx and makepri)" }
    $hit
}

# x.y.z for the app (App.Version), x.y.z.0 for the package.
$semver = ($Version -split '[-+]')[0]
if ($semver -notmatch '^\d+\.\d+\.\d+$') { throw "Version must be x.y.z (got '$Version')" }
$packageVersion = "$semver.0"

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$out = Join-Path $PSScriptRoot 'out'
$stage = Join-Path $out 'stage'
$msix = Join-Path $out "Pawse-$semver.msix"
$makepri = Find-SdkTool 'makepri.exe'
$makeappx = Find-SdkTool 'makeappx.exe'

# PowerShell 7 cannot load the Appx cmdlets natively on every Windows build; the Windows
# PowerShell compatibility session always can. Windows PowerShell 5.1 has them built in.
if ($PSVersionTable.PSEdition -eq 'Core') {
    Import-Module Appx -UseWindowsPowerShell -WarningAction SilentlyContinue
}

# A test install registered from out\stage runs from there - rebuilding underneath it would
# leave a registration pointing at files that changed or vanished. Remove it first. This also
# removes its data folder (settings and log): a registered layout can't be swapped in place.
$registered = Get-AppxPackage -Name $IdentityName -ErrorAction SilentlyContinue |
    Where-Object { $_.InstallLocation -and ([IO.Path]::GetFullPath($_.InstallLocation) -eq $stage) }
if ($registered) {
    Write-Host "Removing the test install registered from $stage"
    $registered | ForEach-Object { Remove-AppxPackage -Package $_.PackageFullName }
}

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory $stage | Out-Null

# Not single-file, unlike the other two builds: a package wants loose files (nothing to unpack
# to %TEMP% at every start), and the .msix is compressed anyway.
Write-Host "Publishing the Store build ($semver)"
Invoke-Native 'dotnet publish' {
    dotnet publish (Join-Path $root 'src\Pawse\Pawse.csproj') -c Release -r win-x64 --self-contained true `
        -p:PawseStore=true -p:DebugType=none "-p:Version=$semver" -o $stage
}

Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $stage 'Assets') -Recurse

$manifest = Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml') -Raw
$manifest = $manifest.Replace('$IDENTITY_NAME$', [Security.SecurityElement]::Escape($IdentityName))
$manifest = $manifest.Replace('$PUBLISHER$', [Security.SecurityElement]::Escape($Publisher))
$manifest = $manifest.Replace('$PUBLISHER_DISPLAY_NAME$', [Security.SecurityElement]::Escape($PublisherDisplayName))
$manifest = $manifest.Replace('$VERSION$', $packageVersion)
[IO.File]::WriteAllText((Join-Path $stage 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

# resources.pri is the index Windows uses to pick a logo by scale and size (the .scale-200 and
# targetsize-N files). Indexing only the logos: pointed at the whole stage, makepri would list
# every DLL of the runtime as a resource too.
$priRoot = Join-Path $out 'pri'
New-Item -ItemType Directory $priRoot | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'Assets') (Join-Path $priRoot 'Assets') -Recurse
$priConfig = Join-Path $out 'priconfig.xml'
Invoke-Native 'makepri createconfig' { & $makepri createconfig /cf $priConfig /dq en-US /pv 10.0.0 /o }
# By default createconfig moves each language and scale into a .pri of its own
# (<packaging><autoResourcePackage/>), which only the resource packs of an .msixbundle load.
# This is a single .msix, where a resources.scale-200.pri is dead weight and the 200% logos
# are never found - so keep one index.
$config = [xml](Get-Content $priConfig -Raw)
$packaging = $config.resources.SelectSingleNode('packaging')
if ($packaging) { [void]$config.resources.RemoveChild($packaging) }
$config.Save($priConfig)
$pri = Join-Path $stage 'resources.pri'
Invoke-Native 'makepri new' {
    & $makepri new /pr $priRoot /cf $priConfig /mn (Join-Path $stage 'AppxManifest.xml') /of $pri /o
}
$split = Get-ChildItem $stage -Filter '*.pri' | Where-Object Name -ne 'resources.pri'
if ($split) { throw "makepri split the resource index ($($split.Name -join ', ')) - a single .msix loads only resources.pri" }
$dump = Join-Path $out 'resources.pri.xml'
Invoke-Native 'makepri dump' { & $makepri dump /if $pri /of $dump /dt Detailed /o }
if (-not (Select-String -Path $dump -Pattern 'scale-200' -Quiet)) { throw "resources.pri does not list the 200% logos" }

# makeappx validates the manifest against the schema as it packs.
Invoke-Native 'makeappx pack' { & $makeappx pack /d $stage /p $msix /o }
$mb = [math]::Round((Get-Item $msix).Length / 1MB, 1)
Write-Host "Packed $msix ($mb MB, unsigned) as $IdentityName $packageVersion"

if ($Register) {
    Add-AppxPackage -Register (Join-Path $stage 'AppxManifest.xml')
    Write-Host "Installed for this user from $stage - start Pawse from the Start menu, or 'pawse' in Win+R."
}

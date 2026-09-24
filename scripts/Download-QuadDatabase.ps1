<#
.SYNOPSIS
    Downloads and installs the Watney quad database packs that FreePolarAlign
    bundles (D13): watneyqdb-00-07-20-v3 and watneyqdb-08-09-20-v3.

.DESCRIPTION
    About 759 MB to download and about 1.55 GB once extracted -- the packs
    roughly double on extraction (D13), so the free-space check below uses the
    larger figure.

    By default the packs go into a "quaddb" folder beside this script, which in
    a published build is beside FreePolarAlign.App.exe. The application looks
    there first, then in %USERPROFILE%\.free-polar-align\quaddb, unless the
    FPA_QUADDB_DIR environment variable says otherwise.

    A pack is only counted as installed when its .qdbindex sidecar is present
    along with every .qdb file its zip contained. Watney refuses a pack without
    the index, and the error it gives names a *version* problem rather than a
    missing file (D13), which sends people looking in the wrong place.

    Written for Windows PowerShell 5.1, which every Windows machine has, as well
    as PowerShell 7.

.PARAMETER Destination
    Where to install. Defaults to "quaddb" beside this script.

.PARAMETER UserProfile
    Install into %USERPROFILE%\.free-polar-align\quaddb instead, where every
    build of the application finds it.

.PARAMETER Force
    Download and extract again even if a pack already looks complete.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Download-QuadDatabase.ps1

.EXAMPLE
    .\Download-QuadDatabase.ps1 -UserProfile
#>
[CmdletBinding()]
param(
    [string] $Destination,
    [switch] $UserProfile,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 redraws its progress bar for every block received,
# which makes a large download many times slower than the link allows.
$ProgressPreference = 'SilentlyContinue'

# Windows PowerShell 5.1 does not offer TLS 1.2 by default, and GitHub refuses
# anything older.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

# The v3 generation: newest, and about 20% smaller than v1 for identical
# coverage (D13). Nothing newer has been published since February 2022.
$ReleaseBase = 'https://github.com/Jusas/WatneyAstrometry/releases/download/watneyqdb3'
$Packs = @(
    @{ Name = 'watneyqdb-00-07-20-v3'; Index = 'gaia2-00-07-20.qdbindex'; Pattern = 'gaia2-*-00-07-20.qdb' },
    @{ Name = 'watneyqdb-08-09-20-v3'; Index = 'gaia2-08-09-20.qdbindex'; Pattern = 'gaia2-*-08-09-20.qdb' }
)

# Measured for the two packs above (D13): about 1.55 GB extracted, plus room for
# the zip being extracted from.
$RequiredFreeBytes = 2.0GB

if ($UserProfile -and $Destination) {
    throw 'Give either -Destination or -UserProfile, not both.'
}

if ($UserProfile) {
    $Destination = Join-Path $env:USERPROFILE '.free-polar-align\quaddb'
}
elseif (-not $Destination) {
    $Destination = Join-Path $PSScriptRoot 'quaddb'
}

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$Destination = (Resolve-Path $Destination).Path
Write-Host "Installing the quad database into $Destination"

function Test-PackComplete([hashtable] $Pack, [int] $ExpectedCells) {
    if (-not (Test-Path (Join-Path $Destination $Pack.Index))) {
        return $false
    }

    $cells = @(Get-ChildItem -Path $Destination -Filter $Pack.Pattern -File).Count
    if ($ExpectedCells -gt 0) {
        return $cells -eq $ExpectedCells
    }

    return $cells -gt 0
}

$missing = @($Packs | Where-Object { $Force -or -not (Test-PackComplete $_ 0) })
if ($missing.Count -eq 0) {
    Write-Host 'Both packs are already installed. Use -Force to download them again.'
    exit 0
}

$drive = Get-PSDrive -Name ($Destination.Substring(0, 1)) -ErrorAction SilentlyContinue
if ($drive -and $drive.Free -lt $RequiredFreeBytes) {
    throw ('{0:N1} GB free on {1}:, but the packs need about {2:N1} GB once extracted.' -f `
        ($drive.Free / 1GB), $drive.Name, ($RequiredFreeBytes / 1GB))
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

foreach ($pack in $missing) {
    $url = "$ReleaseBase/$($pack.Name).zip"
    $zip = Join-Path $Destination "$($pack.Name).zip"

    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing

    # Counted from the zip itself, so "complete" means every cell the pack
    # actually contains rather than a number written down here.
    $archive = [IO.Compression.ZipFile]::OpenRead($zip)
    try {
        $expectedCells = @($archive.Entries | Where-Object { $_.Name -like $pack.Pattern }).Count
    }
    finally {
        $archive.Dispose()
    }

    Write-Host "Extracting $($pack.Name) ($expectedCells cells)"
    Expand-Archive -Path $zip -DestinationPath $Destination -Force

    if (-not (Test-PackComplete $pack $expectedCells)) {
        throw ("$($pack.Name) did not extract completely: expected $($pack.Index) and $expectedCells .qdb files " +
               "in $Destination. The zip has been left there to inspect.")
    }

    Remove-Item $zip
    Write-Host "$($pack.Name) installed."
}

Write-Host 'Done. Plate solving can now run without an internet connection.'

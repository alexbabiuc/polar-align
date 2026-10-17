<#
.SYNOPSIS
    Downloads and installs Watney quad database packs for FreePolarAlign (D13,
    D39). By default the two wide-field packs, 00-07 and 08-09.

.DESCRIPTION
    The installer runs this to install the packs chosen on its download page,
    from zips it has already downloaded (-ZipDirectory). Run by hand, it
    downloads them itself.

    | Pack  | Frames (diagonal)  | Download | Extracted |
    |-------|--------------------|----------|-----------|
    | 00-07 | about 1.6 deg up   | 369 MB   | 768 MB    |
    | 08-09 | about 1.2-1.6 deg  | 390 MB   | ~0.8 GB   |
    | 10-11 | about 0.8-1.2 deg  | 780 MB   | ~1.6 GB   |
    | 12-13 | about 0.6-0.8 deg  | 1.56 GB  | ~3.2 GB   |

    The packs roughly double on extraction (measured 2.08x, D13), so the
    free-space check uses the extracted size plus the zip.

    By default the packs go into a "quaddb" folder beside this script, which in
    a published build is beside FreePolarAlign.App.exe. The application looks
    there first, then in %USERPROFILE%\.free-polar-align\quaddb, unless the
    FPA_QUADDB_DIR environment variable says otherwise.

    A pack is only counted as installed when its .qdbindex sidecar is present
    along with every .qdb file its zip contained. Watney refuses a pack without
    the index, and the error it gives names a *version* problem rather than a
    missing file (D13), which sends people looking in the wrong place.

    Written for Windows PowerShell 5.1, which every Windows machine has, as well
    as PowerShell 7. Exits non-zero when any pack fails to install.

.PARAMETER Pack
    Which packs to install: any of 00-07, 08-09, 10-11, 12-13.
    Defaults to 00-07 and 08-09.

.PARAMETER Destination
    Where to install. Defaults to "quaddb" beside this script.

.PARAMETER UserProfile
    Install into %USERPROFILE%\.free-polar-align\quaddb instead, where every
    build of the application finds it.

.PARAMETER ZipDirectory
    Take each pack's zip from this folder, under its published name (for
    example watneyqdb-10-11-20-v3.zip), instead of downloading it. The zips are
    left where they are.

.PARAMETER Force
    Install again even if a pack already looks complete.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\Download-QuadDatabase.ps1

.EXAMPLE
    .\Download-QuadDatabase.ps1 -UserProfile -Pack 08-09,10-11
#>
[CmdletBinding()]
param(
    [ValidateSet('00-07', '08-09', '10-11', '12-13')]
    [string[]] $Pack = @('00-07', '08-09'),
    [string] $Destination,
    [switch] $UserProfile,
    [string] $ZipDirectory,
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

# Download sizes from Watney's release (D13).
$DownloadBytes = @{ '00-07' = 369MB; '08-09' = 390MB; '10-11' = 780MB; '12-13' = 1560MB }

function Get-PackInfo([string] $Range) {
    @{
        Range   = $Range
        Name    = "watneyqdb-$Range-20-v3"
        Index   = "gaia2-$Range-20.qdbindex"
        Pattern = "gaia2-*-$Range-20.qdb"
    }
}

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

function Test-PackComplete([hashtable] $Info, [int] $ExpectedCells) {
    if (-not (Test-Path (Join-Path $Destination $Info.Index))) {
        return $false
    }

    $cells = @(Get-ChildItem -Path $Destination -Filter $Info.Pattern -File).Count
    if ($ExpectedCells -gt 0) {
        return $cells -eq $ExpectedCells
    }

    return $cells -gt 0
}

$missing = @($Pack | Select-Object -Unique | ForEach-Object { Get-PackInfo $_ } |
    Where-Object { $Force -or -not (Test-PackComplete $_ 0) })
if ($missing.Count -eq 0) {
    Write-Host 'Every pack asked for is already installed. Use -Force to install again.'
    exit 0
}

# Extracted at about twice the download (D13), and the zip itself when it is
# downloaded here.
$requiredBytes = 0
foreach ($info in $missing) {
    $zipBytes = $DownloadBytes[$info.Range]
    $requiredBytes += [long](2.1 * $zipBytes)
    if (-not $ZipDirectory) {
        $requiredBytes += $zipBytes
    }
}

$drive = Get-PSDrive -Name ($Destination.Substring(0, 1)) -ErrorAction SilentlyContinue
if ($drive -and $drive.Free -lt $requiredBytes) {
    throw ('{0:N1} GB free on {1}:, but the packs need about {2:N1} GB.' -f `
        ($drive.Free / 1GB), $drive.Name, ($requiredBytes / 1GB))
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# Entry by entry rather than Expand-Archive, which in Windows PowerShell 5.1 is
# many times slower, and with every path checked to stay inside the
# destination, since the zip comes from the internet.
function Expand-Pack([string] $Zip) {
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            if ([string]::IsNullOrEmpty($entry.Name)) {
                continue
            }

            $target = [IO.Path]::GetFullPath((Join-Path $Destination $entry.FullName))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
                throw "$Zip contains '$($entry.FullName)', which would land outside $Destination."
            }

            New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $archive.Dispose()
    }
}

$failed = @()
foreach ($info in $missing) {
    try {
        if ($ZipDirectory) {
            $zip = Join-Path $ZipDirectory "$($info.Name).zip"
            if (-not (Test-Path $zip)) {
                throw "$zip is not there."
            }
        }
        else {
            $url = "$ReleaseBase/$($info.Name).zip"
            $zip = Join-Path $Destination "$($info.Name).zip"
            Write-Host "Downloading $url"
            Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
        }

        # Counted from the zip itself, so "complete" means every cell the pack
        # actually contains rather than a number written down here.
        $archive = [IO.Compression.ZipFile]::OpenRead($zip)
        try {
            $expectedCells = @($archive.Entries | Where-Object { $_.Name -like $info.Pattern }).Count
        }
        finally {
            $archive.Dispose()
        }

        Write-Host "Extracting $($info.Name) ($expectedCells cells)"
        Expand-Pack $zip

        if (-not (Test-PackComplete $info $expectedCells)) {
            throw ("$($info.Name) did not extract completely: expected $($info.Index) and $expectedCells .qdb files " +
                   "in $Destination. The zip has been left at $zip to inspect.")
        }

        if (-not $ZipDirectory) {
            Remove-Item $zip
        }

        Write-Host "$($info.Name) installed."
    }
    catch {
        Write-Warning "$($info.Name) was not installed: $($_.Exception.Message)"
        $failed += $info.Name
    }
}

if ($failed.Count -gt 0) {
    Write-Error "Not installed: $($failed -join ', ')." -ErrorAction Continue
    exit 1
}

Write-Host 'Done. Plate solving can now run without an internet connection.'

<#
.SYNOPSIS
    Compiles the Windows installer (D39) from a scripts/publish-windows.sh
    output, with the version in Directory.Build.props.

.DESCRIPTION
    Shared by the release and CI workflows, so the installer CI checks is the
    one a release ships. Installs Inno Setup through Chocolatey when the
    machine does not have it.

.EXAMPLE
    .\scripts\build-installer.ps1 -PublishDir out\app -OutputDir out\installer
#>
param(
    [Parameter(Mandatory)] [string] $PublishDir,
    [Parameter(Mandatory)] [string] $OutputDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$version = ([xml](Get-Content (Join-Path $root 'Directory.Build.props'))).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) {
    throw 'No <Version> in Directory.Build.props.'
}

$iscc = Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'
if (-not (Test-Path $iscc)) {
    choco install innosetup --no-progress -y
    if ($LASTEXITCODE -ne 0) { throw "Installing Inno Setup failed ($LASTEXITCODE)." }
}

& $iscc "/DAppVersion=$version" "/DPublishDir=$((Resolve-Path $PublishDir).Path)" "/DOutputDir=$OutputDir" `
    (Join-Path $root 'installer\FreePolarAlign.iss')
if ($LASTEXITCODE -ne 0) { throw "ISCC failed ($LASTEXITCODE)." }

$installer = Join-Path $OutputDir "FreePolarAlign-$version-win-x64-setup.exe"
if (-not (Test-Path $installer)) { throw "ISCC reported success but $installer is not there." }
Write-Output $installer

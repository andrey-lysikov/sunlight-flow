#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$Version = '',
    [string]$Msix = '',
    [string]$Certificate = ''
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string]$text) { Write-Host "`n$text" -ForegroundColor Cyan }
function Write-Detail([string]$text) { Write-Host "  $text" }

function Install-WixToolset {
    if (Get-Command wix -ErrorAction SilentlyContinue) {
        Write-Detail "WiX already installed"
    }
    else {
        Write-Detail "installing WiX"
        dotnet tool install --global wix --version 6.0.2 | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "could not install WiX" }
        $env:PATH = "$env:USERPROFILE\.dotnet\tools;$env:PATH"
    }

    foreach ($extension in @('WixToolset.UI.wixext', 'WixToolset.Util.wixext')) {
        $installed = & wix extension list --global 2>&1 | Out-String
        if ($installed -notmatch [regex]::Escape($extension)) {
            Write-Detail "adding extension $extension"
            & wix extension add --global "$extension/6.0.2" | Out-Null
        }
    }
}

$root    = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src\Sunlight-Flow.csproj'
$outDir  = Join-Path $root 'build'
$msi     = Join-Path $outDir 'Sunlight-Flow.msi'
$icon    = Join-Path $root 'src\sun.ico'
$license = Join-Path $PSScriptRoot 'License.rtf'

if (-not $Msix)        { $Msix = Join-Path $outDir 'Sunlight-Flow.msix' }
if (-not $Certificate) { $Certificate = Join-Path $outDir 'Sunlight-Flow.cer' }

if (-not (Test-Path $Msix))        { throw "$Msix is missing, run build.ps1 first" }
if (-not (Test-Path $Certificate)) { throw "$Certificate is missing, run build.ps1 first" }

[xml]$csproj = Get-Content $project
$properties = $csproj.Project.PropertyGroup

if (-not $Version) {
    $Version = $properties.Version | Where-Object { $_ } | Select-Object -First 1
}
if (-not $Version) { throw "$project has no <Version>" }
if ($Version -notmatch '^\d+\.\d+$') { throw "version '$Version' must have two parts, like 1.1" }

$parts = @($Version.Split('.'))
while ($parts.Count -lt 3) { $parts += '0' }
$msiVersion = ($parts[0..2]) -join '.'

$manufacturer = $properties.Company | Where-Object { $_ } | Select-Object -First 1
if (-not $manufacturer) { $manufacturer = 'lysnet.ru' }

$publisher = ([xml](Get-Content (Join-Path $PSScriptRoot 'AppxManifest.xml'))).Package.Identity.Publisher

Write-Host "Sunlight-Flow $Version (MSI $msiVersion)" -ForegroundColor Cyan
Write-Detail "publisher $publisher"

Write-Step "[1/2] WiX Toolset"
Install-WixToolset

Write-Step "[2/2] wix build"
New-Item -ItemType Directory -Path $outDir -Force | Out-Null
if (Test-Path $msi) { Remove-Item $msi -Force }

& wix build -arch x64 `
    -culture en-US `
    -ext WixToolset.UI.wixext `
    -ext WixToolset.Util.wixext `
    -d "Version=$msiVersion" `
    -d "DisplayVersion=$Version" `
    -d "Manufacturer=$manufacturer" `
    -d "Msix=$Msix" `
    -d "Certificate=$Certificate" `
    -d "Icon=$icon" `
    -d "License=$license" `
    -o $msi `
    (Join-Path $PSScriptRoot 'Package.wxs') `
    (Join-Path $PSScriptRoot 'UpdateDlg.wxs')

if ($LASTEXITCODE -ne 0) { throw "wix build exited with code $LASTEXITCODE" }

Get-ChildItem $outDir -Filter *.wixpdb -ErrorAction SilentlyContinue | Remove-Item -Force

Write-Host "`nDone." -ForegroundColor Green
Write-Detail "installer $msi"

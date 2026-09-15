#Requires -Version 5.1

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$CertPassword = 'Sunlight-Flow',
    [switch]$SkipInstaller,
    [switch]$KeepIntermediate
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root    = $PSScriptRoot
$project = Join-Path $root 'src\Sunlight-Flow.csproj'
$staging = Join-Path $root 'build\staging'
$outDir  = Join-Path $root 'build'
$msix    = Join-Path $outDir 'Sunlight-Flow.msix'
$pfx     = Join-Path $outDir 'Sunlight-Flow.pfx'
$cer     = Join-Path $outDir 'Sunlight-Flow.cer'

$subject = 'CN=lysnet.ru'

function Write-Step([string]$text) { Write-Host "`n$text" -ForegroundColor Cyan }
function Write-Detail([string]$text) { Write-Host "  $text" }

function Remove-Intermediate {
    if ($KeepIntermediate) {
        Write-Detail 'intermediate files kept'
        return
    }

    $paths = @(
        (Join-Path $root 'build\staging'),
        (Join-Path $root 'build\publish'),
        (Join-Path $root 'src\bin'),
        (Join-Path $root 'src\obj'),
        (Join-Path $root 'build\Sunlight-Flow.pfx')
    )
    Get-ChildItem (Join-Path $root 'build') -Filter *.wixpdb -ErrorAction SilentlyContinue |
        ForEach-Object { $paths += $_.FullName }

    $removed = 0
    foreach ($path in $paths) {
        if ($path -and (Test-Path $path)) {
            Remove-Item $path -Recurse -Force -ErrorAction SilentlyContinue
            $removed++
        }
    }

    if ($removed) { Write-Detail "cleaned $removed intermediate item(s)" }
}

function Find-SdkTool([string]$name) {
    $pkg = Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools'

    if (-not (Test-Path $pkg)) {
        Write-Detail "downloading Microsoft.Windows.SDK.BuildTools"
        $tmp = Join-Path ([IO.Path]::GetTempPath()) "sdktools-$(New-Guid)"
        New-Item -ItemType Directory -Path $tmp | Out-Null
        try {
            Push-Location $tmp
            dotnet new console -n fetch --force | Out-Null
            dotnet add fetch\fetch.csproj package Microsoft.Windows.SDK.BuildTools | Out-Null
        }
        finally {
            Pop-Location
            Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    $tool = Get-ChildItem $pkg -Recurse -Filter $name -File -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1

    if (-not $tool) { throw "$name not found in Microsoft.Windows.SDK.BuildTools" }
    return $tool.FullName
}

function New-LogoSet([string]$icoPath, [string]$assetsDir) {
    Add-Type -AssemblyName System.Drawing

    $bytes = [IO.File]::ReadAllBytes($icoPath)
    $count = [BitConverter]::ToUInt16($bytes, 4)

    $best = $null
    for ($i = 0; $i -lt $count; $i++) {
        $entry  = 6 + 16 * $i
        $width  = if ($bytes[$entry] -eq 0) { 256 } else { [int]$bytes[$entry] }
        $length = [BitConverter]::ToInt32($bytes, $entry + 8)
        $offset = [BitConverter]::ToInt32($bytes, $entry + 12)

        if (-not $best -or $width -gt $best.Width) {
            $best = [pscustomobject]@{ Width = $width; Offset = $offset; Length = $length }
        }
    }

    if (-not $best) { throw "$icoPath contains no frames" }

    $frame = New-Object byte[] $best.Length
    [Array]::Copy($bytes, $best.Offset, $frame, 0, $best.Length)

    if ($frame[0] -ne 0x89 -or $frame[1] -ne 0x50) { throw "the icon frame is not a PNG" }

    $stream = New-Object IO.MemoryStream(,$frame)
    $source = [Drawing.Image]::FromStream($stream)

    New-Item -ItemType Directory -Path $assetsDir -Force | Out-Null

    $logos = @{
        'Square150x150Logo.png' = 150
        'Square44x44Logo.png'   = 44
        'StoreLogo.png'         = 50
        'Square44x44Logo.targetsize-24_altform-unplated.png' = 24
    }

    foreach ($name in $logos.Keys) {
        $size = $logos[$name]
        $bmp = New-Object Drawing.Bitmap($size, $size, [Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.InterpolationMode = [Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $g.PixelOffsetMode   = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([Drawing.Color]::Transparent)
        $g.DrawImage($source, 0, 0, $size, $size)
        $g.Dispose()
        $bmp.Save((Join-Path $assetsDir $name), [Drawing.Imaging.ImageFormat]::Png)
        $bmp.Dispose()
    }

    $source.Dispose()
    $stream.Dispose()
    Write-Detail "logos built from $(Split-Path $icoPath -Leaf)"
}

trap { Remove-Intermediate; break }

[xml]$csproj = Get-Content $project
$version = $csproj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "$project has no <Version>" }
if ($version -notmatch '^\d+\.\d+$') { throw "version '$version' must have two parts, like 1.1" }

$parts = @($version.Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$packageVersion = ($parts[0..3]) -join '.'

Write-Host "Sunlight-Flow $version (package $packageVersion)" -ForegroundColor Cyan

$makeappx = Find-SdkTool 'makeappx.exe'
$signtool = Find-SdkTool 'signtool.exe'

Write-Step "[1/5] dotnet publish"
$publish = Join-Path $outDir 'publish'
dotnet publish $project -c $Configuration -o $publish --nologo
if ($LASTEXITCODE -ne 0) { throw "publish exited with code $LASTEXITCODE" }

Write-Step "[2/5] Staging"
if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }

$app    = Join-Path $staging 'app'
$package = Join-Path $staging 'package'
New-Item -ItemType Directory -Path $app, $package | Out-Null

Copy-Item (Join-Path $publish '*') $app -Recurse -Force
New-LogoSet (Join-Path $root 'src\sun.ico') (Join-Path $app 'Assets')

$public = Join-Path $app 'Public'
New-Item -ItemType Directory -Path $public -Force | Out-Null
Set-Content (Join-Path $public 'readme.txt') `
    'PublicFolder for the com.microsoft.windows.lighting app extension.' -Encoding utf8

Get-ChildItem $app -Filter *.pdb -Recurse | Remove-Item -Force

$manifest = Get-Content (Join-Path $root 'installer\AppxManifest.xml') -Raw
$manifest = $manifest -replace 'Version="0\.0\.0\.0"', "Version=`"$packageVersion`""
[IO.File]::WriteAllText((Join-Path $package 'AppxManifest.xml'), $manifest, [Text.UTF8Encoding]::new($false))

Write-Step "[3/5] MakeAppx pack"
if (Test-Path $msix) { Remove-Item $msix -Force }
& $makeappx pack /d $package /p $msix /nv /o | Out-Null
if ($LASTEXITCODE -ne 0) { throw "MakeAppx exited with code $LASTEXITCODE" }

Write-Step "[4/5] Signing certificate"
$cert = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $subject -and $_.NotAfter -gt (Get-Date) } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1

if (-not $cert) {
    Write-Detail "creating a self-signed certificate $subject"
    $cert = New-SelfSignedCertificate `
        -Type Custom `
        -Subject $subject `
        -KeyUsage DigitalSignature `
        -FriendlyName 'Sunlight-Flow (code signing)' `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
}
else {
    Write-Detail "reusing the existing one, thumbprint $($cert.Thumbprint)"
}

$secure = ConvertTo-SecureString -String $CertPassword -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
Export-Certificate  -Cert $cert -FilePath $cer -Type CERT      | Out-Null

Write-Step "[5/5] SignTool sign"
& $signtool sign /fd SHA256 /a /f $pfx /p $CertPassword $msix | Out-Null
if ($LASTEXITCODE -ne 0) { throw "SignTool exited with code $LASTEXITCODE" }

Write-Detail "package $msix"

if (-not $SkipInstaller) {
    & (Join-Path $root 'installer\installer.ps1') -Version $version -Payload $app -Msix $msix -Certificate $cer
}

Write-Step 'Cleanup'
Remove-Intermediate

Write-Host "`nDone." -ForegroundColor Green
Get-ChildItem $outDir -File | ForEach-Object { Write-Detail "$($_.Name)  $([math]::Round($_.Length / 1KB)) KB" }

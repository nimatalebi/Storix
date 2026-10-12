<#
.SYNOPSIS
    Publishes KeepstoneBackup, builds the MSI installer and a portable ZIP, and optionally signs the binaries.
.EXAMPLE
    ./build/package.ps1 -Version 1.2.0
.NOTES
    Code signing is enabled when the environment variables KEEPSTONEBACKUP_SIGN_CERT (base64 .pfx) and
    KEEPSTONEBACKUP_SIGN_PASSWORD are set.
#>
param(
    [string]$Version = '1.0.0',
    [string]$Runtime = 'win-x64',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts')
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
New-Item -ItemType Directory -Force -Path $Output | Out-Null
$Output = (Resolve-Path $Output).Path
$publish = Join-Path $Output 'publish'
if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }

foreach ($project in 'src/NT.KeepstoneBackup.Service/NT.KeepstoneBackup.Service.csproj', 'src/NT.KeepstoneBackup.WinForms/NT.KeepstoneBackup.WinForms.csproj', 'src/NT.KeepstoneBackup.Cli/NT.KeepstoneBackup.Cli.csproj') {
    dotnet publish (Join-Path $root $project) -c Release -r $Runtime --self-contained true -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

function Invoke-Sign([string[]]$Files) {
    if (-not $env:KEEPSTONEBACKUP_SIGN_CERT) { return }
    $pfx = Join-Path $env:TEMP 'keepstonebackup-sign.pfx'
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:KEEPSTONEBACKUP_SIGN_CERT))
    try {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
        & $signtool.FullName sign /f $pfx /p $env:KEEPSTONEBACKUP_SIGN_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $Files
        if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
    }
    finally {
        Remove-Item $pfx -Force
    }
}

Invoke-Sign @((Join-Path $publish 'KeepstoneBackup.Service.exe'), (Join-Path $publish 'KeepstoneBackup.Manager.exe'), (Join-Path $publish 'NT.KeepstoneBackup.Core.dll'))

# MSI versions must be numeric (major.minor.build): drop any pre-release suffix.
$msiVersion = ($Version -split '[-+]')[0]
dotnet build (Join-Path $root 'installer/KeepstoneBackup.Installer.wixproj') -c Release -p:PublishDir="$publish\" -p:ProductVersion=$msiVersion -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Building the MSI failed.' }
$msi = Join-Path $Output "KeepstoneBackup-$msiVersion-x64.msi"
Invoke-Sign @($msi)

# Standalone restore tool: one self-contained file, no installation or database needed.
$standalone = Join-Path $Output 'standalone'
dotnet publish (Join-Path $root 'src/NT.KeepstoneBackup.Cli/NT.KeepstoneBackup.Cli.csproj') -c Release -r $Runtime --self-contained true -p:Version=$Version `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $standalone
if ($LASTEXITCODE -ne 0) { throw 'Publishing the standalone CLI failed.' }
Copy-Item (Join-Path $standalone 'keepstonebackup.exe') (Join-Path $Output "keepstonebackup-$Version-x64.exe") -Force
Remove-Item $standalone -Recurse -Force
Invoke-Sign @((Join-Path $Output "keepstonebackup-$Version-x64.exe"))

$zip = Join-Path $Output "KeepstoneBackup-$Version-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip

Get-ChildItem $Output -File | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()) *$($_.Name)"
} | Where-Object { $_ -notmatch '\.sha256$|SHA256SUMS' } | Set-Content (Join-Path $Output 'SHA256SUMS.txt')

Write-Host "Artifacts in $Output" -ForegroundColor Green

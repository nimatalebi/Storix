<#
.SYNOPSIS
    Publishes Storix, builds the MSI installer and a portable ZIP, and optionally signs the binaries.
.EXAMPLE
    ./build/package.ps1 -Version 1.2.0
.NOTES
    Code signing is enabled when the environment variables STORIX_SIGN_CERT (base64 .pfx) and
    STORIX_SIGN_PASSWORD are set.
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

foreach ($project in 'src/NT.Storix.Service/NT.Storix.Service.csproj', 'src/NT.Storix.WinForms/NT.Storix.WinForms.csproj') {
    dotnet publish (Join-Path $root $project) -c Release -r $Runtime --self-contained true -p:Version=$Version -o $publish
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

function Invoke-Sign([string[]]$Files) {
    if (-not $env:STORIX_SIGN_CERT) { return }
    $pfx = Join-Path $env:TEMP 'storix-sign.pfx'
    [IO.File]::WriteAllBytes($pfx, [Convert]::FromBase64String($env:STORIX_SIGN_CERT))
    try {
        $signtool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" | Sort-Object FullName -Descending | Select-Object -First 1
        & $signtool.FullName sign /f $pfx /p $env:STORIX_SIGN_PASSWORD /fd SHA256 /tr http://timestamp.digicert.com /td SHA256 $Files
        if ($LASTEXITCODE -ne 0) { throw 'Signing failed.' }
    }
    finally {
        Remove-Item $pfx -Force
    }
}

Invoke-Sign @((Join-Path $publish 'Storix.Service.exe'), (Join-Path $publish 'Storix.Manager.exe'), (Join-Path $publish 'NT.Storix.Core.dll'))

# MSI versions must be numeric (major.minor.build): drop any pre-release suffix.
$msiVersion = ($Version -split '[-+]')[0]
dotnet build (Join-Path $root 'installer/Storix.Installer.wixproj') -c Release -p:PublishDir="$publish\" -p:ProductVersion=$msiVersion -o $Output
if ($LASTEXITCODE -ne 0) { throw 'Building the MSI failed.' }
$msi = Join-Path $Output "Storix-$msiVersion-x64.msi"
Invoke-Sign @($msi)

$zip = Join-Path $Output "Storix-$Version-x64-portable.zip"
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath $zip

Get-ChildItem $Output -File | ForEach-Object {
    "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLower()) *$($_.Name)"
} | Where-Object { $_ -notmatch '\.sha256$|SHA256SUMS' } | Set-Content (Join-Path $Output 'SHA256SUMS.txt')

Write-Host "Artifacts in $Output" -ForegroundColor Green

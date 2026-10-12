<#
.SYNOPSIS
    Publishes the KeepstoneBackup service and manager into a single folder.
.EXAMPLE
    ./build/publish.ps1 -Output C:\Tools\KeepstoneBackup
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\KeepstoneBackup'),
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

foreach ($project in 'src/NT.KeepstoneBackup.Service/NT.KeepstoneBackup.Service.csproj', 'src/NT.KeepstoneBackup.WinForms/NT.KeepstoneBackup.WinForms.csproj', 'src/NT.KeepstoneBackup.Cli/NT.KeepstoneBackup.Cli.csproj') {
    dotnet publish (Join-Path $root $project) -c $Configuration -r $Runtime --self-contained true -o $Output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

Write-Host "KeepstoneBackup published to $Output" -ForegroundColor Green
Write-Host "Run KeepstoneBackup.Manager.exe as administrator and use the Service tab to install the Windows service."

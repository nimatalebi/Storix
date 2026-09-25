<#
.SYNOPSIS
    Publishes the Storix service and manager into a single folder.
.EXAMPLE
    ./build/publish.ps1 -Output C:\Tools\Storix
#>
param(
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts\Storix'),
    [string]$Runtime = 'win-x64',
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$root = Resolve-Path (Join-Path $PSScriptRoot '..')

foreach ($project in 'src/NT.Storix.Service/NT.Storix.Service.csproj', 'src/NT.Storix.WinForms/NT.Storix.WinForms.csproj', 'src/NT.Storix.Cli/NT.Storix.Cli.csproj') {
    dotnet publish (Join-Path $root $project) -c $Configuration -r $Runtime --self-contained true -o $Output
    if ($LASTEXITCODE -ne 0) { throw "Publishing $project failed." }
}

Write-Host "Storix published to $Output" -ForegroundColor Green
Write-Host "Run Storix.Manager.exe as administrator and use the Service tab to install the Windows service."

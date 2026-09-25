<#
.SYNOPSIS
    Installs (or removes) the Storix Windows service without the manager UI. Run as administrator.
.EXAMPLE
    ./install-service.ps1 -Path C:\Tools\Storix\Storix.Service.exe
    ./install-service.ps1 -Uninstall
#>
param(
    [string]$Path = (Join-Path $PSScriptRoot 'Storix.Service.exe'),
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$name = 'Storix'

if ($Uninstall) {
    if (Get-Service $name -ErrorAction SilentlyContinue) {
        Stop-Service $name -ErrorAction SilentlyContinue
        sc.exe delete $name | Out-Null
        Write-Host "Service '$name' removed."
    }
    return
}

$exe = Resolve-Path $Path
New-Service -Name $name -BinaryPathName "`"$exe`"" -DisplayName 'Storix Backup Agent' `
    -Description 'Storix scheduled backup agent (files, SQL Server, MongoDB).' -StartupType Automatic | Out-Null
sc.exe config $name start= delayed-auto | Out-Null
sc.exe failure $name reset= 86400 actions= restart/60000/restart/60000/restart/300000 | Out-Null
Start-Service $name
Write-Host "Service '$name' installed and started."

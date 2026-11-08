# GWLite installer: copies the apps to %LOCALAPPDATA%\GWLite, registers the
# logon scheduled task (elevated - ETW requires admin) and starts it.
# Re-runnable: safe to use for upgrades over an existing install.

$ErrorActionPreference = 'Stop'

# self-elevate (ETW kernel sessions need admin)
$id = [Security.Principal.WindowsIdentity]::GetCurrent()
$admin = [Security.Principal.WindowsPrincipal]::new($id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList @(
        '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    )
    exit
}

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here

$dest = Join-Path $env:LOCALAPPDATA 'GWLite'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

Copy-Item (Join-Path $root 'gwlite.exe') $dest -Force
Copy-Item (Join-Path $root 'gwlite-ui.exe') $dest -Force
Copy-Item (Join-Path $root 'gwlite.ico') $dest -Force

Write-Host "Installed to $dest"

# registers (or refreshes) the 'GWLite' logon task pointing at the installed exe
& (Join-Path $dest 'gwlite.exe') --install-autostart
if ($LASTEXITCODE -ne 0) {
    Write-Warning "autostart registration returned $LASTEXITCODE (is the task already fine?)"
}

schtasks /run /tn GWLite | Out-Null
Write-Host ''
Write-Host 'GWLite is starting - the dashboard window opens automatically.'
Write-Host 'It will auto-start at logon; a tray icon lets you pause or exit.'

# Packetly installer: copies the apps to %LOCALAPPDATA%\Packetly, registers the
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

$dest = Join-Path $env:LOCALAPPDATA 'Packetly'
New-Item -ItemType Directory -Force -Path $dest | Out-Null

# upgrade cleanup: stop any running pre-rename build, remove the old task
# and old shortcuts (data is migrated by the collector on first start)
Get-Process gwlite, gwlite-ui, packetly, packetly-ui -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
cmd /c "schtasks /Delete /tn GWLite /f >nul 2>&1"
$smPrograms = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
foreach ($old in @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'GWLite Dashboard.lnk'),
    (Join-Path $smPrograms 'GWLite Dashboard.lnk')
)) { if (Test-Path $old) { Remove-Item $old -Force } }

# full collector payload (dlls, native libs, wwwroot) + UI + icon
Get-ChildItem $root -Exclude 'packetly-ui.exe', 'packetly.ico', 'INSTALL.txt' |
    Copy-Item -Destination $dest -Recurse -Force
Copy-Item (Join-Path $root 'packetly-ui.exe') $dest -Force
Copy-Item (Join-Path $root 'packetly.ico') $dest -Force

Write-Host "Installed to $dest"

# registers (or refreshes) the 'Packetly' logon task pointing at the installed exe
& (Join-Path $dest 'packetly.exe') --install-autostart
if ($LASTEXITCODE -ne 0) {
    Write-Warning "autostart registration returned $LASTEXITCODE (is the task already fine?)"
}

cmd /c "schtasks /run /tn Packetly >nul 2>&1"
Write-Host ''
Write-Host 'Packetly is starting - the dashboard window opens automatically.'
Write-Host 'It will auto-start at logon; a tray icon lets you pause or exit.'

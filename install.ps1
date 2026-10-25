# Installs VRAM Meter for this user (no admin needed): builds VramMeter.exe, puts it in
# %LOCALAPPDATA%\Programs\VRAM Meter (the per-user Program Files), adds it to the Start menu and the desktop, starts
# it at logon in the tray, and lists it in Settings > Apps > Installed apps.
#   powershell -ExecutionPolicy Bypass -File install.ps1          (install / update)
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Remove  (uninstall)
param([switch]$Remove)
$ErrorActionPreference = 'Stop'

$name = 'VRAM Meter'
$target = Join-Path $env:LOCALAPPDATA "Programs\$name"
$exe = Join-Path $target 'VramMeter.exe'
$startMenu = Join-Path ([Environment]::GetFolderPath('Programs')) "$name.lnk"
$desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) "$name.lnk"
$runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
$uninstallKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\VramMeter"

Get-Process VramMeter -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Remove) {
    Remove-Item $startMenu, $desktop -Force -ErrorAction SilentlyContinue
    Remove-ItemProperty $runKey $name -ErrorAction SilentlyContinue
    Remove-Item $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $env:LOCALAPPDATA $name) -Recurse -Force -ErrorAction SilentlyContinue
    # This script may be running from the folder it removes: delete it once the script has ended.
    Start-Process cmd.exe "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$target`"" -WindowStyle Hidden
    "$name removed. The GPU choices you made stay in Windows' Graphics settings."
    return
}

$src = Join-Path $PSScriptRoot 'src'
$out = Join-Path $PSScriptRoot 'publish'
dotnet publish $src -c Release -o $out -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }

New-Item $target -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $out 'VramMeter.exe'), (Join-Path $PSScriptRoot 'VramMeter.ico'), $PSCommandPath $target -Force

$shell = New-Object -ComObject WScript.Shell
foreach ($link in $startMenu, $desktop) {
    $s = $shell.CreateShortcut($link)
    $s.TargetPath = $exe; $s.WorkingDirectory = $target; $s.IconLocation = "$exe,0"
    $s.Description = 'See which apps use your GPU memory, end them, and pick the GPU they run on'
    $s.Save()
}

Set-ItemProperty $runKey $name "`"$exe`" --tray"

New-Item $uninstallKey -Force | Out-Null
$size = [int]((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
@{
    DisplayName = $name; DisplayVersion = '2.0'; Publisher = 'Bart Matusiak'
    DisplayIcon = "$exe,0"; InstallLocation = $target; InstallDate = (Get-Date -Format 'yyyyMMdd')
    Comments = 'Shows which apps use the GPU''s memory, ends them, and moves them between GPUs'
    UninstallString = "powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File `"$target\install.ps1`" -Remove"
}.GetEnumerator() | ForEach-Object { Set-ItemProperty $uninstallKey $_.Key $_.Value }
Set-ItemProperty $uninstallKey EstimatedSize $size -Type DWord
Set-ItemProperty $uninstallKey NoModify 1 -Type DWord
Set-ItemProperty $uninstallKey NoRepair 1 -Type DWord

Start-Process $exe
"$name installed in $target`: Start menu and desktop, starts with Windows in the tray, and listed in Installed apps."

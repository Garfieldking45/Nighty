@echo off
setlocal
title Nighty installer
echo.
echo  Nighty installer
echo  ----------------
echo  Downloads the latest Nighty from GitHub, installs it for your Windows
echo  account (no administrator needed) and adds Start menu and Desktop shortcuts.
echo.

set "NIGHTY_DIR=%LOCALAPPDATA%\Programs\Nighty"

tasklist /FI "IMAGENAME eq Nighty.exe" 2>NUL | find /I "Nighty.exe" >NUL
if not errorlevel 1 echo  Nighty is running right now. Close it, then run this installer again.
if not errorlevel 1 pause
if not errorlevel 1 exit /b 1

if not exist "%NIGHTY_DIR%" mkdir "%NIGHTY_DIR%"

powershell -NoProfile -ExecutionPolicy Bypass -Command "$ErrorActionPreference='Stop'; $ProgressPreference='SilentlyContinue'; [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12; try { $r = Invoke-RestMethod 'https://api.github.com/repos/Garfieldking45/Nighty/releases/latest' -Headers @{'User-Agent'='Nighty-installer'}; $a = $r.assets | Where-Object { $_.name -eq 'Nighty.exe' } | Select-Object -First 1; if (-not $a) { throw 'The latest release has no Nighty.exe.' }; Write-Host (' Downloading Nighty ' + $r.tag_name + ' (' + [math]::Round($a.size/1MB) + ' MB), please wait...'); $tmp = Join-Path $env:TEMP 'Nighty-download.exe'; Invoke-WebRequest $a.browser_download_url -OutFile $tmp -UseBasicParsing; if ((Get-Item $tmp).Length -ne $a.size) { throw 'The download was incomplete. Run the installer again.' }; if ($a.digest -like 'sha256:*') { $h = (Get-FileHash $tmp -Algorithm SHA256).Hash; if ($h -ne $a.digest.Substring(7)) { throw 'The download did not match its checksum. Nothing was installed.' } }; $exe = Join-Path $env:NIGHTY_DIR 'Nighty.exe'; Move-Item $tmp $exe -Force; Unblock-File $exe; $ws = New-Object -ComObject WScript.Shell; $links = @((Join-Path ([Environment]::GetFolderPath('Programs')) 'Nighty.lnk'), (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Nighty.lnk')); foreach ($l in $links) { $s = $ws.CreateShortcut($l); $s.TargetPath = $exe; $s.WorkingDirectory = $env:NIGHTY_DIR; $s.IconLocation = $exe; $s.Description = 'Nighty'; $s.Save() }; Write-Host (' Installed to ' + $env:NIGHTY_DIR) } catch { Write-Host (' Install failed: ' + $_.Exception.Message) -ForegroundColor Red; exit 1 }"

if errorlevel 1 echo.
if errorlevel 1 echo  Nothing was installed.
if errorlevel 1 pause
if errorlevel 1 exit /b 1

echo.
echo  Done. Shortcuts were added to your Start menu and Desktop.
echo  To remove Nighty later: delete the shortcuts and the folder above.
echo  Your settings live in %%APPDATA%%\Nighty and are kept.
echo.
choice /C YN /M " Start Nighty now"
if not errorlevel 2 start "" "%NIGHTY_DIR%\Nighty.exe"
endlocal

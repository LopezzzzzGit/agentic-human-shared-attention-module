@echo off
setlocal

rem Always run from this project folder, even when double-clicked in Explorer.
cd /d "%~dp0"

rem A running Explorer or terminal can predate the Groq setup. Read the saved
rem user values explicitly so every fresh ASHA process receives them.
for /f "usebackq delims=" %%R in (`powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('ASHA_GROQ_KEYS', 'User')"`) do set "ASHA_GROQ_KEYS=%%R"
for /f "usebackq delims=" %%K in (`powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('ASHA_GROQ_API_KEY', 'User')"`) do set "ASHA_GROQ_API_KEY=%%K"
for /f "usebackq delims=" %%M in (`powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('ASHA_GROQ_MODEL', 'User')"`) do set "ASHA_GROQ_MODEL=%%M"
for /f "usebackq delims=" %%V in (`powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('ASHA_MODEL_SUPPORTS_VISION', 'User')"`) do set "ASHA_MODEL_SUPPORTS_VISION=%%V"

rem Build output for the current cloud-and-glass voice orb.
set "ASHA_EXE=interactive\bin\ASHA\asha-live.exe"
set "ASHA_OVERLAY=overlay\bin\Release\net8.0-windows\asha-overlay.exe"

rem Never start a second orb. Quit the running ASHA first when installing a new build.
tasklist /FI "IMAGENAME eq asha-live.exe" 2>nul | find /I "asha-live.exe" >nul
if not errorlevel 1 exit /b 0

if not exist "%ASHA_EXE%" goto build_asha
for /f "usebackq delims=" %%B in (`powershell -NoProfile -Command "$exe=Get-Item -LiteralPath '%ASHA_EXE%'; $newer=Get-ChildItem -LiteralPath 'interactive' -Recurse -File | Where-Object { $_.FullName -notmatch '\\(?:bin|obj)\\' -and $_.Extension -in '.cs','.xaml','.csproj' -and $_.LastWriteTime -gt $exe.LastWriteTime }; if($newer){'yes'}else{'no'}"`) do set "ASHA_REBUILD=%%B"
if /I "%ASHA_REBUILD%"=="yes" goto build_asha
if not exist "%ASHA_OVERLAY%" goto build_overlay
goto start_asha

:build_asha
echo Building the current ASHA orb...
dotnet build interactive\AshaLive.csproj --configuration Release --output interactive\bin\ASHA
if errorlevel 1 goto build_failed
if exist "%ASHA_OVERLAY%" goto start_asha

:build_overlay
echo Building the visual overlay...
dotnet build overlay\AshaOverlay.csproj --configuration Release
if errorlevel 1 goto build_failed

:start_asha
start "ASHA" "%ASHA_EXE%"
exit /b 0

:build_failed
echo.
echo ASHA could not be built. See the error above.
pause
exit /b 1

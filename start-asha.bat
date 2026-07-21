@echo off
setlocal

rem Always run from this project folder, even when double-clicked in Explorer.
cd /d "%~dp0"

rem A running Explorer or terminal can predate the Groq setup. Read the saved
rem user values explicitly so every fresh ASHA process receives them.
for /f "usebackq delims=" %%R in (`powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('ASHA_GROQ_KEYS', 'User')"`) do set "ASHA_GROQ_KEYS=%%R"
for /f "usebackq delims=" %%K in (`powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('ASHA_GROQ_API_KEY', 'User')"`) do set "ASHA_GROQ_API_KEY=%%K"
for /f "usebackq delims=" %%M in (`powershell -NoProfile -Command "[Environment]::GetEnvironmentVariable('ASHA_GROQ_MODEL', 'User')"`) do set "ASHA_GROQ_MODEL=%%M"

rem Build output for the current cloud-and-glass voice orb.
set "ASHA_EXE=interactive\bin\ASHA\asha-live.exe"

if not exist "%ASHA_EXE%" (
    echo ASHA needs to be built once. Building now...
    dotnet build interactive\AshaLive.csproj --configuration Release --output interactive\bin\ASHA
    if errorlevel 1 (
        echo.
        echo ASHA could not be built. See the error above.
        pause
        exit /b 1
    )
)

start "ASHA" "%ASHA_EXE%"

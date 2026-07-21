@echo off
setlocal

echo.
echo ASHA uses your Groq API keys only from this Windows user account.
echo Enter one key, or several keys separated by commas.
echo They are saved as the user environment variable ASHA_GROQ_KEYS.
echo No key is written into this project folder.
echo.
set /p "ASHA_GROQ_KEYS=Paste your Groq API key or comma-separated keys, then press Enter: "

if "%ASHA_GROQ_KEYS%"=="" (
    echo.
    echo No key was entered. Nothing was changed.
    pause
    exit /b 1
)

powershell -NoProfile -Command "[Environment]::SetEnvironmentVariable('ASHA_GROQ_KEYS', $env:ASHA_GROQ_KEYS, 'User')"
if errorlevel 1 (
    echo.
    echo ASHA could not save the key.
    pause
    exit /b 1
)

echo.
echo Groq rotation is configured. Close this window, then restart ASHA.
pause

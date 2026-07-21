@echo off
setlocal

echo.
echo ASHA uses your Groq API key only from this Windows user account.
echo It is saved as the user environment variable ASHA_GROQ_API_KEY.
echo The key is not written into this project folder.
echo.
set /p "ASHA_GROQ_API_KEY=Paste your Groq API key, then press Enter: "

if "%ASHA_GROQ_API_KEY%"=="" (
    echo.
    echo No key was entered. Nothing was changed.
    pause
    exit /b 1
)

powershell -NoProfile -Command "[Environment]::SetEnvironmentVariable('ASHA_GROQ_API_KEY', $env:ASHA_GROQ_API_KEY, 'User')"
if errorlevel 1 (
    echo.
    echo ASHA could not save the key.
    pause
    exit /b 1
)

echo.
echo Groq is configured. Close this window, then restart ASHA.
pause

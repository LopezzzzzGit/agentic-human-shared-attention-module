@echo off
setlocal

echo.
echo This selects Llama 3.3 70B for ASHA only.
echo It does not change Scriviteo or any of its Groq keys.
echo.

powershell -NoProfile -Command "[Environment]::SetEnvironmentVariable('ASHA_GROQ_MODEL', 'llama-3.3-70b-versatile', 'User')"
if errorlevel 1 (
    echo.
    echo ASHA could not save the model choice.
    pause
    exit /b 1
)

echo.
echo ASHA will use llama-3.3-70b-versatile the next time it is started.
echo Close this window, then start ASHA with start-asha.bat.
pause

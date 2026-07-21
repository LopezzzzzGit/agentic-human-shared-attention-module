@echo off
setlocal

echo.
echo This older shortcut selects the Qwen 3.6 27B model available to ASHA.
echo It does not change Scriviteo or any of its Groq keys.
echo.

powershell -NoProfile -Command "[Environment]::SetEnvironmentVariable('ASHA_GROQ_MODEL', 'qwen/qwen3.6-27b', 'User')"
if errorlevel 1 (
    echo.
    echo ASHA could not save the model choice.
    pause
    exit /b 1
)

echo.
echo ASHA will use qwen/qwen3.6-27b the next time it is started.
echo Close this window, then start ASHA with start-asha.bat.
pause

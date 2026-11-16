@echo off
chcp 65001 >nul
setlocal
rem Keep the window open, always log, and never crash on PowerShell startup:
rem POWERSHELL_UPDATECHECK=Off defeats the zh-CN update-banner FailFast bug.
set "POWERSHELL_UPDATECHECK=Off"

rem Pick the single .ps1 next to this launcher (avoids embedding CJK bytes in a .cmd).
set "PSSCRIPT="
for %%f in ("%~dp0*.ps1") do set "PSSCRIPT=%%~ff"

set "PWEXE=%ProgramFiles%\PowerShell\7\pwsh.exe"
if not exist "%PWEXE%" set "PWEXE=powershell.exe"

if not defined PSSCRIPT (
    echo [ERROR] No .ps1 found next to this launcher.
    goto :end
)

echo Running: "%PSSCRIPT%"
echo Host     : "%PWEXE%"
echo.
"%PWEXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PSSCRIPT%" 2>&1
set "RC=%ERRORLEVEL%"
echo.
echo [exit code: %RC%]
if not "%RC%"=="0" echo [WARN] Non-zero exit, see messages above.

:end
echo.
pause

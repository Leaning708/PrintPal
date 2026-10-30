@echo off
setlocal
rem One-click installer build. Delegates to the tested publish script, which
rem publishes app + service, stages the installer scripts and builds the MSI.
rem Extra arguments are forwarded, e.g.  Build-Installer.cmd -Zip
echo Building PrintPal installer ...
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish\Publish-PrintPal.ps1" %*
set "rc=%ERRORLEVEL%"

echo.
if not "%rc%"=="0" (
  echo [FAILED] exit code %rc% - see the log above.
  pause
  exit /b %rc%
)
echo [OK] Installer built - the MSI path is printed above.
pause

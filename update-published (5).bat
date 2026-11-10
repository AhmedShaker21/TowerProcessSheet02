@echo off
REM ===== Publish the latest code AND restart the running Tower System service =====
REM Run from the project folder (the one with TowerProcessSheet.csproj):
REM right-click > "Run as administrator".
net session >nul 2>&1
if errorlevel 1 (
  echo Please right-click this file and choose "Run as administrator".
  pause
  exit /b 1
)
cd /d "%~dp0"

echo Stopping the Tower System service (it holds port 5080 and locks the exe)...
sc stop TowerSystem >nul 2>&1
timeout /t 5 /nobreak >nul

echo.
echo Publishing (this can take a minute)...
dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true
if errorlevel 1 (
  echo.
  echo Publish FAILED - see the messages above. The service was NOT restarted.
  pause
  exit /b 1
)

echo.
echo Starting the service again...
sc start TowerSystem
timeout /t 8 /nobreak >nul
sc query TowerSystem | findstr STATE
echo.
echo Done. Refresh the site with Ctrl+F5:  http://YOUR-PC-IP:5080
pause

@echo off
REM ===== Build a self-contained, single-file Windows app =====
REM Produces ONE TowerSystem.exe (single file - no wwwroot folder) that runs
REM on any Windows 10/11 x64 PC WITHOUT installing .NET.

echo Building Process Sheet System (this can take a minute)...
echo.

dotnet publish -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true

echo.
if exist "bin\Release\net8.0\win-x64\publish\TowerSystem.exe" (
  echo ============================================================
  echo  Done!  Your app is in:
  echo    bin\Release\net8.0\win-x64\publish\
  echo.
  echo  Copy just TowerSystem.exe to any PC and double-click it.
  echo  The browser opens automatically. No wwwroot folder needed.
  echo ============================================================
) else (
  echo Publish did not complete - check the messages above.
)
echo.
pause

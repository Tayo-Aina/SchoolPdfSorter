@echo off
setlocal
title Publish School PDF Sorter

rem Prefer the per-user .NET 10 SDK used by this project; fall back to PATH.
set "DOTNET=%USERPROFILE%\.dotnet10\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

rem The published exe cannot be overwritten while the app is running.
tasklist /FI "IMAGENAME eq SchoolPdfSorterApp.exe" 2>nul | find /I "SchoolPdfSorterApp.exe" >nul
if not errorlevel 1 (
    echo Close the running School PDF Sorter window before publishing.
    pause
    exit /b 1
)

cd /d "%~dp0"

echo Publishing School PDF Sorter as a self-contained Windows executable...
echo No .NET runtime is needed on the target PC.
echo.

"%DOTNET%" publish "src\SchoolPdfSorter.App\SchoolPdfSorter.App.csproj" ^
  -c Release ^
  -r win-x64 ^
  --self-contained true ^
  -p:PublishSingleFile=true ^
  -p:IncludeNativeLibrariesForSelfExtract=true ^
  -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=None ^
  -p:DebugSymbols=false ^
  -o "publish"

if errorlevel 1 (
    echo.
    echo Publish failed. Check the errors above.
    pause
    exit /b 1
)

echo.
echo Done. The portable app is:
echo   %~dp0publish\SchoolPdfSorterApp.exe
echo.
echo Copy that single exe anywhere on a 64-bit Windows PC. It does not need .NET.
pause

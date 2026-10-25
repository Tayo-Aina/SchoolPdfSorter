@echo off
setlocal
title Usage - School PDF Sorter

rem Prefer the self-contained build, which runs without a system .NET runtime.
set "EXE=%~dp0publish\UsageDashboard.exe"
if exist "%EXE%" (
    start "" "%EXE%" %*
    exit /b 0
)

rem Otherwise run from source with the per-user .NET 10 SDK.
set "DOTNET=%USERPROFILE%\.dotnet10\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

"%DOTNET%" run --project "%~dp0UsageDashboard.csproj" %*
if errorlevel 1 pause

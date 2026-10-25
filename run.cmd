@echo off
setlocal
set "PUBLISHED=%~dp0publish\SchoolPdfSorterApp.exe"
set "DEBUG=%~dp0src\SchoolPdfSorter.App\bin\Debug\net10.0-windows\SchoolPdfSorterApp.exe"

rem Prefer the portable, self-contained build when it exists.
if exist "%PUBLISHED%" (
    set "APP=%PUBLISHED%"
) else (
    set "APP=%DEBUG%"
)

if not exist "%APP%" (
    echo School PDF Sorter is not built yet.
    echo.
    echo For a portable exe that needs no .NET installed, run:
    echo   publish.cmd
    echo.
    echo Or build the debug version with:
    echo   "%USERPROFILE%\.dotnet10\dotnet.exe" build "%~dp0SchoolPdfSorter.sln"
    pause
    exit /b 1
)

rem The debug apphost needs the per-user .NET 10 runtime.
rem The published app is self-contained, so it does not.
if /I "%APP%"=="%DEBUG%" if exist "%USERPROFILE%\.dotnet10\dotnet.exe" set "DOTNET_ROOT=%USERPROFILE%\.dotnet10"

start "" /normal "%APP%"

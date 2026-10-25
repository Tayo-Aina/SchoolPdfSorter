@echo off
setlocal
title Release School PDF Sorter

rem Builds the portable exe, then writes latest.json from the same binary.
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0release.ps1" %*
if errorlevel 1 pause

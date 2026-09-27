@echo off
rem Debug + Release build and tests. Arguments are passed to build.ps1 (e.g. build.cmd -Configuration Release).
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1" %*
if "%~1"=="" pause

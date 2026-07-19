@echo off
rem gen.bat — wrapper around the gallery-builder script.
rem Avoids typing the .py extension in chat clients that auto-link it.
rem
rem Usage:
rem   Tools\gen.bat "<data-folder>" [--gallery-id "<id>"]
rem
rem Example:
rem   Tools\gen.bat "_results~/noSymSplit_2026-04-28" --gallery-id "noSymSplit_2026-04-28"

setlocal
set SCRIPT=%~dp0build_gallery.py
if not exist "%SCRIPT%" (
  echo [gen.bat] Cannot find %SCRIPT%
  exit /b 1
)
python "%SCRIPT%" %*
endlocal

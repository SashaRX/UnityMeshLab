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
set "SCRIPT=%~dp0build_gallery.py"
if not exist "%SCRIPT%" (
  echo [gen.bat] Cannot find "%SCRIPT%"
  exit /b 1
)

rem Forward only the argument forms supported by build_gallery.py.  Expanding
rem %%* here would cause cmd.exe to parse metacharacters in the original
rem command line a second time.
if "%~2"=="" goto run_basic
if "%~4"=="" goto run_one_option
if not "%~6"=="" goto usage
if /i "%~2"=="--out" if /i "%~4"=="--gallery-id" (
  python "%SCRIPT%" "%~1" --out "%~3" --gallery-id "%~5"
  goto end
)
if /i "%~2"=="--gallery-id" if /i "%~4"=="--out" (
  python "%SCRIPT%" "%~1" --gallery-id "%~3" --out "%~5"
  goto end
)
goto usage

:run_one_option
if /i "%~2"=="--out" (
  python "%SCRIPT%" "%~1" --out "%~3"
  goto end
)
if /i "%~2"=="--gallery-id" (
  python "%SCRIPT%" "%~1" --gallery-id "%~3"
  goto end
)
goto usage

:run_basic
python "%SCRIPT%" "%~1"
goto end

:usage
echo [gen.bat] Usage: gen.bat "data-folder" [--out "output-folder"] [--gallery-id "id"]
exit /b 2

:end
endlocal

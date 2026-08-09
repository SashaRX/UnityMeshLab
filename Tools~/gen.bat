@echo off
rem gen.bat — wrapper around the gallery-builder script.
rem Avoids typing the .py extension in chat clients that auto-link it.
rem
rem Usage:
rem   Tools\gen.bat "<data-folder>" [--gallery-id "<id>"]
rem
rem Example:
rem   Tools\gen.bat "_results~/noSymSplit_2026-04-28" --gallery-id "noSymSplit_2026-04-28"

setlocal EnableExtensions EnableDelayedExpansion
set "SCRIPT=%~dp0build_gallery.py"
if not exist "%SCRIPT%" (
  echo [gen.bat] Cannot find "%SCRIPT%"
  exit /b 1
)

rem ── Validate the forwarded values ──────────────────────────────────────
rem The option whitelist below constrains only the option NAMES (%~2, %~4).
rem The values — %~1 (data folder), %~3 and %~5 (option arguments) — were
rem forwarded untouched, and cmd.exe substitutes them into the python line
rem before it parses that line, so a value carrying a double quote could
rem close the quoting and turn the rest into commands.
rem
rem Check each value against a strict charset: letters, digits, underscore,
rem dash, dot, tilde, colon, both path separators and space. Quotes and cmd
rem metacharacters (& | < > ^ % ! ( ) ; ,) are rejected. The test runs through
rem delayed expansion (!VAR!), which substitutes after the line has been
rem parsed, so the value under test cannot be read as syntax.
set "ARG1=%~1"
set "ARG3=%~3"
set "ARG5=%~5"
for %%V in (ARG1 ARG3 ARG5) do (
  if defined %%V (
    echo(!%%V!| findstr /r /c:"^[A-Za-z0-9_.~:/\\ -][A-Za-z0-9_.~:/\\ -]*$" >nul || goto badarg
  )
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

:badarg
echo [gen.bat] Rejected argument. The data folder and the --out / --gallery-id
echo [gen.bat] values may contain only letters, digits and _ - . ~ : \ / space.
exit /b 3

:usage
echo [gen.bat] Usage: gen.bat "data-folder" [--out "output-folder"] [--gallery-id "id"]
exit /b 2

:end
endlocal

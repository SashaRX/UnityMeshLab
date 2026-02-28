@echo off
setlocal

:: ── Locate Visual Studio ──
set VSWHERE="%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe"
for /f "usebackq tokens=*" %%i in (`%VSWHERE% -latest -property installationPath`) do set VS_PATH=%%i

if not defined VS_PATH (
    echo ERROR: Visual Studio not found
    exit /b 1
)

echo Using Visual Studio: %VS_PATH%

:: ── Setup build directory ──
set SCRIPT_DIR=%~dp0
set BUILD_DIR=%SCRIPT_DIR%build
set NATIVE_DIR=%SCRIPT_DIR%Native

echo Build dir: %BUILD_DIR%
echo Native dir: %NATIVE_DIR%

:: ── Configure ──
echo.
echo === Configuring CMake ===
cmake -S "%NATIVE_DIR%" -B "%BUILD_DIR%" -G "Visual Studio 17 2022" -A x64
if errorlevel 1 (
    echo ERROR: CMake configure failed
    exit /b 1
)

:: ── Build Release ──
echo.
echo === Building Release ===
cmake --build "%BUILD_DIR%" --config Release
if errorlevel 1 (
    echo ERROR: Build failed
    exit /b 1
)

echo.
echo === Done ===
echo DLL copied to Plugins\x86_64\xatlas-unity.dll

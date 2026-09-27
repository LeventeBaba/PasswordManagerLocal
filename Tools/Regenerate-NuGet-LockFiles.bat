@echo off
setlocal EnableExtensions

rem -----------------------------------------------------------------------------
rem Regenerates NuGet packages.lock.json files used by the GitHub Actions build.
rem
rem Place this file in:
rem   PasswordManagerLocal\Tools\Regenerate-NuGet-LockFiles.bat
rem
rem The script resolves the repository root as the parent of the Tools folder.
rem -----------------------------------------------------------------------------

set "TOOLS_DIR=%~dp0"
for %%I in ("%TOOLS_DIR%..") do set "ROOT_DIR=%%~fI"

set "BACKEND_PROJECT=Common\Backend\PasswordManagerLocal.Common.Backend.csproj"
set "FRONTEND_PROJECT=Common\Frontend\PasswordManagerLocal.Common.Frontend.csproj"
set "TESTS_PROJECT=Common\Tests\PasswordManagerLocal.Common.Tests.csproj"

echo.
echo ============================================================
echo  PasswordManagerLocal NuGet lock-file regeneration
echo ============================================================
echo Repository root: "%ROOT_DIR%"
echo.

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: The dotnet command was not found in PATH.
    echo Install the SDK required by global.json and try again.
    exit /b 1
)

if not exist "%ROOT_DIR%\global.json" (
    echo ERROR: global.json was not found at:
    echo        "%ROOT_DIR%\global.json"
    echo Make sure this script is inside the repository's Tools folder.
    exit /b 1
)

call :RequireProject "%BACKEND_PROJECT%"
if errorlevel 1 exit /b 1
call :RequireProject "%FRONTEND_PROJECT%"
if errorlevel 1 exit /b 1
call :RequireProject "%TESTS_PROJECT%"
if errorlevel 1 exit /b 1

pushd "%ROOT_DIR%" || (
    echo ERROR: Could not enter the repository root.
    exit /b 1
)

echo Using .NET SDK:
dotnet --version
if errorlevel 1 goto :Failed

echo.
echo [1/2] Regenerating lock files...
echo.

call :Regenerate "%BACKEND_PROJECT%"
if errorlevel 1 goto :Failed
call :Regenerate "%FRONTEND_PROJECT%"
if errorlevel 1 goto :Failed
call :Regenerate "%TESTS_PROJECT%"
if errorlevel 1 goto :Failed

echo.
echo [2/2] Verifying the regenerated lock files in locked mode...
echo.

call :Verify "%BACKEND_PROJECT%"
if errorlevel 1 goto :Failed
call :Verify "%FRONTEND_PROJECT%"
if errorlevel 1 goto :Failed
call :Verify "%TESTS_PROJECT%"
if errorlevel 1 goto :Failed

echo.
echo ============================================================
echo  Lock files regenerated and verified successfully.
echo ============================================================
echo.
echo Review the changed packages.lock.json files, then commit them.
echo You can inspect them with:
echo   git status --short
echo   git diff -- "**/packages.lock.json"
echo.

popd
exit /b 0

:RequireProject
if not exist "%ROOT_DIR%\%~1" (
    echo ERROR: Required project was not found:
    echo        "%ROOT_DIR%\%~1"
    exit /b 1
)
exit /b 0

:Regenerate
echo ------------------------------------------------------------
echo Regenerating: %~1
dotnet restore "%~1" --use-lock-file --force-evaluate
if errorlevel 1 (
    echo ERROR: Lock-file regeneration failed for %~1
    exit /b 1
)
exit /b 0

:Verify
echo ------------------------------------------------------------
echo Verifying: %~1
dotnet restore "%~1" --locked-mode
if errorlevel 1 (
    echo ERROR: Locked-mode verification failed for %~1
    exit /b 1
)
exit /b 0

:Failed
echo.
echo ============================================================
echo  FAILED: NuGet lock files were not regenerated successfully.
echo ============================================================
echo Review the error output above. No commit was made automatically.
echo.
popd
exit /b 1

@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem Run every Common.Tests category that is opt-in during an ordinary test run.
rem Both flags are required: security stress uses a different gate from sync stress.
rem Usage: Run-Common-Stress-Tests.bat [Debug|Release] [--no-pause]
rem dotnet test restores/builds Common.Tests and its dependencies before testing.

set "PML_STRESS_CONFIGURATION=Debug"
set "PML_STRESS_PAUSE=1"
set "PML_STRESS_EXIT_CODE=1"
set "PML_STRESS_PUSHED="
for %%I in ("%~dp0..") do set "PML_STRESS_ROOT=%%~fI"
set "PML_STRESS_PROJECT=%PML_STRESS_ROOT%\Common\Tests\PasswordManagerLocal.Common.Tests.csproj"
set "PML_STRESS_RESULTS=%PML_STRESS_ROOT%\artifacts\test-results\common-stress"

:ParseArguments
if "%~1"=="" goto Run
if /i "%~1"=="Debug" (
    set "PML_STRESS_CONFIGURATION=Debug"
    shift
    goto ParseArguments
)
if /i "%~1"=="Release" (
    set "PML_STRESS_CONFIGURATION=Release"
    shift
    goto ParseArguments
)
if /i "%~1"=="--no-pause" (
    set "PML_STRESS_PAUSE=0"
    shift
    goto ParseArguments
)
echo Usage: %~nx0 [Debug^|Release] [--no-pause]
set "PML_STRESS_EXIT_CODE=2"
goto Finish

:Run
echo.
echo PasswordManagerLocal Common.Tests - all opt-in stress tests
echo Configuration: %PML_STRESS_CONFIGURATION%
echo Groups: SyncStress, SyncStressExtended, SecurityStress
echo Results: "%PML_STRESS_RESULTS%"
echo All declared seeds and data rows are included. The complete run can take a long time.
echo.

if not exist "%PML_STRESS_PROJECT%" (
    echo ERROR: Common.Tests was not found. Keep this file inside the solution's Tools folder.
    goto Finish
)
where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: dotnet was not found. Install the SDK required by global.json, then try again.
    goto Finish
)
pushd "%PML_STRESS_ROOT%"
if errorlevel 1 (
    echo ERROR: Could not enter the solution directory.
    goto Finish
)
set "PML_STRESS_PUSHED=1"

rem Explicitly override any inherited opt-in values in test-host processes only.
rem Keep the filter quoted: CMD must not interpret its category OR operators as pipes.
dotnet test "%PML_STRESS_PROJECT%" -c "%PML_STRESS_CONFIGURATION%" ^
    --environment "PASSWORDMANAGERLOCAL_SYNC_STRESS=1" ^
    --environment "PASSWORDMANAGERLOCAL_SECURITY_STRESS=1" ^
    --filter "TestCategory=SyncStress|TestCategory=SyncStressExtended|TestCategory=SecurityStress" ^
    --results-directory "%PML_STRESS_RESULTS%" ^
    --logger "console;verbosity=normal" ^
    --logger "trx;LogFilePrefix=common-stress"
set "PML_STRESS_EXIT_CODE=%ERRORLEVEL%"
echo.
if "%PML_STRESS_EXIT_CODE%"=="0" (
    echo The test command completed successfully.
) else (
    echo The test command failed. Exit code: %PML_STRESS_EXIT_CODE%
)
echo Review the test summary and TRX files for failures or skipped/inconclusive cases.
echo One security test requires multiple loopback source addresses on this machine.

:Finish
if defined PML_STRESS_PUSHED popd
if "%PML_STRESS_PAUSE%"=="1" (
    echo.
    echo Press any key to close...
    pause >nul
)
exit /b %PML_STRESS_EXIT_CODE%

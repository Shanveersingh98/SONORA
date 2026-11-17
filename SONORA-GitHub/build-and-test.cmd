@echo off
REM Builds every project and runs both test suites.
REM Exit code 0 means the build succeeded and all tests passed.
setlocal

echo === Building the solution ===
dotnet build AudioEnhancer.sln -c Release
if errorlevel 1 goto :failed

echo.
echo === DSP tests (Part 2A) ===
dotnet run --project AudioEnhancer\tests\AudioEnhancer.Dsp.Tests -c Release --no-build
if errorlevel 1 goto :failed

echo.
echo === Audio backend tests (Part 2B) ===
dotnet run --project AudioEnhancer\tests\AudioEnhancer.Audio.Tests -c Release --no-build
if errorlevel 1 goto :failed

echo.
echo === UI view-model tests (Part 4 Step 6) ===
dotnet run --project AudioEnhancer\tests\AudioEnhancer.UI.Tests -c Release --no-build
if errorlevel 1 goto :failed

echo.
echo === Playback devices detected on this machine ===
dotnet run --project tools\AudioEnhancer.DeviceProbe -c Release --no-build

echo.
echo All builds and tests passed.
exit /b 0

:failed
echo.
echo BUILD OR TESTS FAILED.
exit /b 1

@echo off
setlocal
cd /d "%~dp0"

REM Builds a framework-dependent, single-folder publish output for Windows x64.
REM Requires .NET Desktop Runtime on the target machine.
REM Output folder: .\publish\win-x64-fd\

if exist "publish\win-x64-fd" (
  rmdir /s /q "publish\win-x64-fd"
)

REM Wipe bin/obj for EVERY project in the solution, not a hand-maintained pair.
REM The previous list named only the two original projects, so once
REM tests\MatrixDesktop.Tests was added a stale assembly there could survive a
REM run of this script and still be picked up by a solution-wide build. Drive it
REM off the project folder list instead, so adding a project means adding one
REM token on the line below.
set "PROJECT_DIRS=MatrixDesktop MatrixDesktopConfigurator tests\MatrixDesktop.Tests"

for %%P in (%PROJECT_DIRS%) do (
  for %%S in (bin obj) do (
    if exist "%%~P\%%S" (
      rmdir /s /q "%%~P\%%S"
    )
  )
)

dotnet publish "MatrixDesktop\MatrixDesktop.csproj" -c Release /p:PublishProfile=Portable-win-x64-framework-dependent /p:UseAppHost=true
if errorlevel 1 (
  echo.
  echo Publish failed.
  exit /b 1
)

dotnet publish "MatrixDesktopConfigurator\MatrixDesktopConfigurator.csproj" -c Release /p:PublishProfile=Portable-win-x64-framework-dependent /p:UseAppHost=true
if errorlevel 1 (
  echo.
  echo Configurator publish failed.
  exit /b 1
)

echo.
echo Publish complete.
echo Output: "%~dp0publish\win-x64-fd\"
endlocal

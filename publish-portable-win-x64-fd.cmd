@echo off
setlocal
cd /d "%~dp0"

REM Builds a framework-dependent, single-folder publish output for Windows x64.
REM Requires .NET Desktop Runtime on the target machine.
REM Output folder: .\publish\win-x64-fd\

dotnet publish "MatrixDesktop\MatrixDesktop.csproj" -c Release /p:PublishProfile=Portable-win-x64-framework-dependent
if errorlevel 1 (
  echo.
  echo Publish failed.
  exit /b 1
)

echo.
echo Publish complete.
echo Output: "%~dp0publish\win-x64-fd\"
endlocal

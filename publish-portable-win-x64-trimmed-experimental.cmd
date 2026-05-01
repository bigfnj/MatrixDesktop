@echo off
setlocal
cd /d "%~dp0"

REM EXPERIMENTAL: self-contained, single-folder publish output for Windows x64 with IL trimming.
REM Output folder: .\publish\win-x64-trimmed\

dotnet publish "MatrixDesktop\MatrixDesktop.csproj" -c Release /p:PublishProfile=Portable-win-x64-trimmed-experimental
if errorlevel 1 (
  echo.
  echo Publish failed.
  exit /b 1
)

echo.
echo Publish complete.
echo Output: "%~dp0publish\win-x64-trimmed\"
endlocal

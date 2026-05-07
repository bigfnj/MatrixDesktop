@echo off
setlocal
cd /d "%~dp0"

REM Builds a framework-dependent, single-folder publish output for Windows x64.
REM Requires .NET Desktop Runtime on the target machine.
REM Output folder: .\publish\win-x64-fd\

if exist "publish\win-x64-fd" (
  rmdir /s /q "publish\win-x64-fd"
)

for %%D in ("MatrixDesktop\bin" "MatrixDesktop\obj" "MatrixDesktopConfigurator\bin" "MatrixDesktopConfigurator\obj") do (
  if exist "%%~D" (
    rmdir /s /q "%%~D"
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

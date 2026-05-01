@echo off
chcp 65001 >nul
echo ╔══════════════════════════════════════════════════════════════════════════╗
echo ║  MatrixDesktop - Single File Publish (Framework-Dependent)               ║
echo ╚══════════════════════════════════════════════════════════════════════════╝
echo.
echo Configuration:
echo   - Single-file executable: All managed code bundled into MatrixDesktop.exe
echo   - Framework-dependent: Requires .NET 10 Runtime installed
echo   - Output: bin\Release\publish\singlefile-fd\
echo.
echo NOTE: Due to WebView2 requirements, the following will still be separate files:
echo   - web\ folder (HTML/JS assets - required for the app to function)
echo   - runtimes\win-x64\native\WebView2Loader.dll (native WebView2 component)
echo.

:: Check for .NET SDK
dotnet --version >nul 2>&1
if %errorlevel% neq 0 (
    echo ERROR: .NET SDK not found. Please install .NET 10 SDK.
    exit /b 1
)

echo Publishing...
dotnet publish MatrixDesktop\MatrixDesktop.csproj -p:PublishProfile=SingleFile-FD -v quiet

if %errorlevel% neq 0 (
    echo.
    echo ERROR: Publish failed!
    pause
    exit /b 1
)

echo.
echo ╔══════════════════════════════════════════════════════════════════════════╗
echo ║  Publish Complete!                                                       ║
echo ╚══════════════════════════════════════════════════════════════════════════╝
echo.
echo Output location:
echo   MatrixDesktop\bin\Release\publish\singlefile-fd\
echo.
echo Files created:
echo   [EXE] MatrixDesktop.exe          (single-file bundle with all managed code)
echo   [DIR] web\                       (required HTML/JS/assets content)
echo   [DIR] runtimes\win-x64\native\   (WebView2 native dependencies)
echo.

:: Show file sizes
set "PUBLISH_DIR=MatrixDesktop\bin\Release\publish\singlefile-fd"
if exist "%PUBLISH_DIR%\MatrixDesktop.exe" (
    for %%I in ("%PUBLISH_DIR%\MatrixDesktop.exe") do (
        set /a "SIZE_KB=%%~zI/1024"
        echo Executable size: !SIZE_KB! KB
    )
)

echo.
echo Requirements on target machine:
echo   - .NET 10 Desktop Runtime (download from dotnet.microsoft.com)
echo   - Windows 10 version 1809+ or Windows 11
echo   - WebView2 Runtime (usually pre-installed on Windows)
echo.
pause

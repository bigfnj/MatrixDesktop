@echo off
setlocal EnableExtensions

rem ============================================================
rem MatrixDesktop launcher - self-minimizing batch file
rem Put this .bat in the SAME folder as MatrixDesktop.exe
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "EXE=%SCRIPT_DIR%MatrixDesktop.exe"

rem --- Minimized run continues here ---

shift
pushd "%SCRIPT_DIR%" >nul

rem ---- Main launch line ----
start "" /min "%EXE%" --hidecursor font=resurrections fps=30 animationSpeed=0.5 forwardSpeed=0.05 numColumns=220 density=2 dropLength=0.25 effect=stripes renderer=webgpu effect=stripes stripeColors=0.5,0,0.5,0,0,1,0,1,0,0,1,0,0,0,1,0.5,0,0.5 raindropLength=0.5 version=3d

rem ---- Alternative presets (uncomment one if you want) ----
rem "%EXE%" --hidecursor font=resurrections fps=60 animationSpeed=0.5 forwardSpeed=0.05 numColumns=150 density=2 dropLength=0.25 effect=stripes renderer=webgpu version=paradise
rem "%EXE%" --hidecursor font=resurrections fps=30 animationSpeed=0.5 forwardSpeed=0.05 numColumns=220 density=2 dropLength=0.25 effect=stripes renderer=webgpu effect=stripes stripeColors=1,0,0,1,0.5,0,1,1,0,0,1,0,0,0,1,0.5,0,0.5 raindropLength=0.5 version=3d

popd >nul
endlocal
exit /b

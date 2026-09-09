@echo off
setlocal EnableExtensions

rem ============================================================
rem MatrixDesktop example launcher
rem Put this .bat in the SAME folder as MatrixDesktop.exe
rem (i.e. inside publish\win-x64-fd\ or an extracted release zip).
rem
rem This is a worked example of a full argument line, kept in the
rem repo because the argument syntax is easy to get wrong. See
rem README.md and MatrixDesktop_Argument_Guide.txt for the flags,
rem or use MatrixDesktopConfigurator.exe to generate a line.
rem ============================================================

set "SCRIPT_DIR=%~dp0"
set "EXE=%SCRIPT_DIR%MatrixDesktop.exe"

pushd "%SCRIPT_DIR%" >nul

rem ---- Main launch line ----
rem
rem NOTE on `start "" /min`: it does NOT keep the window minimized.
rem MainForm calls ForegroundWindow.BestEffortBringToFront on startup,
rem which does SW_RESTORE on an iconic window and then tries
rem SetForegroundWindow. So /min and the app's own foreground enforcer
rem pull in opposite directions, and the app wins. /min is kept only so
rem the console window that launched the batch file does not sit in
rem front of the render. Drop `/min` if you want no ambiguity, or use
rem `start ""` on its own.
rem
rem NOTE on values: no spaces inside a comma-separated list. An
rem unquoted "1,0,0, 1,1,0" is split by the shell and then silently
rem discarded as malformed.
rem
rem NOTE on aliases: raindropLength and dropLength are the SAME
rem parameter (web/js/config.js:549 aliases one onto the other), so
rem pass exactly one of them. Passing both puts two keys in the query
rem string and the winner is decided by Dictionary enumeration order in
rem MatrixArgs.BuildQueryString, which is not a guaranteed order.
rem Likewise pass each key once: a repeated key is last-wins, so a
rem duplicate is at best noise.
start "" /min "%EXE%" --hidecursor font=resurrections fps=30 animationSpeed=0.5 forwardSpeed=0.05 numColumns=220 density=2 effect=stripes renderer=webgpu stripeColors=0.5,0,0.5,0,0,1,0,1,0,0,1,0,0,0,1,0.5,0,0.5 raindropLength=0.5 version=3d

rem ---- Alternative presets (uncomment one if you want) ----
rem "%EXE%" --hidecursor font=resurrections fps=60 animationSpeed=0.5 forwardSpeed=0.05 numColumns=150 density=2 raindropLength=0.25 effect=stripes renderer=webgpu version=paradise
rem "%EXE%" --hidecursor font=resurrections fps=30 animationSpeed=0.5 forwardSpeed=0.05 numColumns=220 density=2 effect=stripes renderer=webgpu stripeColors=1,0,0,1,0.5,0,1,1,0,0,1,0,0,0,1,0.5,0,0.5 raindropLength=0.5 version=3d

popd >nul
endlocal
exit /b

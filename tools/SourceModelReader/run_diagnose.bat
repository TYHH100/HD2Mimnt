@echo off
setlocal
if "%~1"=="" (
  echo Drag a GMA, ZIP, model folder, or MDL onto this file to diagnose it.
  echo For archives with multiple models, use --model in the PowerShell command shown in README.md.
  pause
  exit /b 1
)
set "OUT=%~dpn1_diagnosis.json"
py -3 "%~dp0source_model_reader.py" diagnose "%~1" --json "%OUT%"
echo.
echo Diagnostic report: %OUT%
pause

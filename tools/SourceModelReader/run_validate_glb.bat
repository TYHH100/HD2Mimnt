@echo off
setlocal
if "%~1"=="" (
  echo Drag a .glb file onto this script to validate its mesh, skeleton, and skin weights.
  pause
  exit /b 1
)
set "OUT=%~dpn1_validation.json"
py -3 "%~dp0source_model_reader.py" validate-glb "%~1" --json "%OUT%"
echo.
echo Validation report: %OUT%
pause

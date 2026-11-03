@echo off
setlocal
if "%~1"=="" (
  echo Drag a .gma, .zip, or model folder onto this script.
  pause
  exit /b 1
)
set "INPUT=%~1"
set "MODEL=%~2"
if "%MODEL%"=="" set /p MODEL=MDL filename or relative path (example aes_yz_pm.mdl): 
if "%MODEL%"=="" (
  echo Model path is required.
  pause
  exit /b 1
)
set "OUT=%~dpn1.bone_matrix.json"
py -3 "%~dp0source_model_reader.py" bone-matrix "%INPUT%" --model "%MODEL%" --json "%OUT%"
echo.
echo JSON report: "%OUT%"
pause

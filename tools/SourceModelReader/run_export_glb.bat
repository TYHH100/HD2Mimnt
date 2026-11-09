@echo off
setlocal
if "%~1"=="" (
  echo Drag a GMA, ZIP, model folder, or MDL onto this file to export the default model as GLB.
  echo For BodyGroup choices or archives with multiple models, use the PowerShell command in README.md.
  pause
  exit /b 1
)
set "OUT=%~dpn1_unified.glb"
py -3 "%~dp0source_model_reader.py" export-glb "%~1" -o "%OUT%"
echo.
echo Output: %OUT%
pause

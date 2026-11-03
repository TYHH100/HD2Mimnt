@echo off
setlocal
if "%~1"=="" (
  echo Drag a .mdl file, model folder, ZIP, or GMA onto this file.
  pause
  exit /b 1
)
set "OUT=%~dpn1_export.smd"
py -3 "%~dp0source_model_reader.py" export "%~1" -o "%OUT%"
echo.
echo Output: %OUT%
pause

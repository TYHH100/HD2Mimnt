@echo off
setlocal
if "%~1"=="" (
  echo Drag a .mdl file, model folder, ZIP, or GMA onto this file.
  echo Or run: run_inspect.bat "path\to\model-or-archive"
  pause
  exit /b 1
)
py -3 "%~dp0source_model_reader.py" inspect "%~1"
echo.
pause

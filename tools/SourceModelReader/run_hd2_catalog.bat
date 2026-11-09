@echo off
setlocal
if "%~1"=="" (
  echo Drag the Helldivers 2 data folder onto this file.
  echo Or run:
  echo py -3 source_model_reader.py hd2-catalog "D:\SteamLibrary\steamapps\common\HELLDIVERS 2\data" --json "hd2_package_catalog.json"
  pause
  exit /b 2
)
set "OUT=%~dpn1_hd2_catalog.json"
py -3 "%~dp0source_model_reader.py" hd2-catalog "%~1" --json "%OUT%"
set "RC=%ERRORLEVEL%"
echo.
echo HD2 package catalog: %OUT%
if not "%RC%"=="0" pause
exit /b %RC%

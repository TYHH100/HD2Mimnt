@echo off
setlocal
if "%~1"=="" (
  echo Drag the Helldivers 2 data folder or a legacy/DSAR archive package onto this file.
  echo For a specific package in the game data folder, use:
  echo py -3 source_model_reader.py hd2-inspect "D:\SteamLibrary\steamapps\common\HELLDIVERS 2\data" --package 9ba626afa44a3aa3 --json inventory.json
  pause
  exit /b 2
)
set "OUT=%~dpn1_hd2_inventory.json"
py -3 "%~dp0source_model_reader.py" hd2-inspect "%~1" --json "%OUT%"
set "RC=%ERRORLEVEL%"
echo.
echo HD2 resource report: %OUT%
if not "%RC%"=="0" pause
exit /b %RC%

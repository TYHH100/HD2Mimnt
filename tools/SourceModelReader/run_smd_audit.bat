@echo off
setlocal
if "%~1"=="" (
  echo Usage: run_smd_audit.bat ^<smd-file^|folder^|zip^|gma^> [optional arguments]
  echo Example: run_smd_audit.bat "D:\Models\SourceProject" --mdl "D:\Models\addon.gma" --model aes_yz_pm.mdl --json smd_audit.json
  pause
  exit /b 2
)
py -3 "%~dp0source_model_reader.py" smd-audit "%~1" %2 %3 %4 %5 %6 %7 %8 %9
set "RC=%ERRORLEVEL%"
if not "%RC%"=="0" pause
exit /b %RC%

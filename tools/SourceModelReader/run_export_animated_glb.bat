@echo off
setlocal
cd /d "%~dp0"
echo Source Model Reader - Animated GLB Export
set /p MODEL_INPUT=Model archive or folder (.gma/.zip/folder): 
set /p MODEL_NAME=MDL name inside input (example aes_yz_pm.mdl): 
set /p ANIMATION_SMD=Animation SMD path: 
set /p FPS=FPS from QC/project settings (required): 
set /p OUTPUT_GLB=Output GLB path (example animated.glb): 
if "%MODEL_INPUT%"=="" goto :error
if "%MODEL_NAME%"=="" goto :error
if "%ANIMATION_SMD%"=="" goto :error
if "%FPS%"=="" goto :error
if "%OUTPUT_GLB%"=="" goto :error
py -3 source_model_reader.py export-animated-glb "%MODEL_INPUT%" --model "%MODEL_NAME%" --animation "%ANIMATION_SMD%" --fps "%FPS%" -o "%OUTPUT_GLB%"
if errorlevel 1 goto :failed
py -3 source_model_reader.py validate-glb "%OUTPUT_GLB%"
if errorlevel 1 goto :failed
echo.
echo Animated GLB export and validation completed.
pause
exit /b 0
:error
echo Required input was blank.
:failed
echo Export/validation failed. Review the error above.
pause
exit /b 1

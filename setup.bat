@echo off
setlocal

set "ROOT_DIR=%~dp0"
set "DATA_DIR=%ROOT_DIR%data"
set "MODELS_DIR=%ROOT_DIR%models"
set "MODEL_NAME=Qwen3-Embedding-0.6B-Q8_0.gguf"
set "MODEL_FILE=%MODELS_DIR%\%MODEL_NAME%"
set "MODEL_PART=%MODEL_FILE%.part"
set "MODEL_URL=https://huggingface.co/Qwen/Qwen3-Embedding-0.6B-GGUF/resolve/main/%MODEL_NAME%?download=true"

if not "%~1"=="" if /I not "%~1"=="--directories-only" (
    echo Usage: setup.bat [--directories-only]
    exit /b 2
)

if not exist "%DATA_DIR%" (
    mkdir "%DATA_DIR%" || exit /b 1
    echo Created: %DATA_DIR%
) else (
    echo Exists:  %DATA_DIR%
)

if not exist "%MODELS_DIR%" (
    mkdir "%MODELS_DIR%" || exit /b 1
    echo Created: %MODELS_DIR%
) else (
    echo Exists:  %MODELS_DIR%
)

if /I "%~1"=="--directories-only" goto ready

if exist "%MODEL_FILE%" (
    echo Exists:  %MODEL_FILE%
    goto ready
)

where curl.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: curl.exe is required to download the model.
    echo Download it manually from:
    echo %MODEL_URL%
    exit /b 1
)

echo Downloading the official Qwen GGUF model...
echo Partial downloads are kept at %MODEL_PART% and resumed on the next run.
echo The progress bar shows completion percentage without an unstable early ETA.
curl.exe --location --fail --retry 3 --continue-at - --progress-bar --output "%MODEL_PART%" "%MODEL_URL%"
if errorlevel 1 (
    echo ERROR: Model download failed. Run setup.bat again to resume.
    exit /b 1
)

move /Y "%MODEL_PART%" "%MODEL_FILE%" >nul
if errorlevel 1 (
    echo ERROR: Could not finalize the downloaded model file.
    exit /b 1
)
echo Downloaded: %MODEL_FILE%

:ready
echo.
echo Agent Forum directories are ready.
echo Database: %DATA_DIR%\agent-forum.db
if /I "%~1"=="--directories-only" (
    echo Model download skipped.
) else (
    echo Model:    %MODEL_FILE%
)

exit /b 0

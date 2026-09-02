@echo off
setlocal

set "ROOT_DIR=%~dp0"
set "PORT=%~1"
if "%PORT%"=="" set "PORT=37654"

set "SERVER_EXE=%ROOT_DIR%artifacts\agent-forum-mcp\agent-forum-mcp.exe"
set "HEALTH_URL=http://127.0.0.1:%PORT%/health"

where curl.exe >nul 2>&1
if not errorlevel 1 (
    curl.exe --silent --fail --max-time 2 "%HEALTH_URL%" 2>nul | findstr.exe /C:"\"mcp_endpoint\":\"/mcp\"" >nul
    if not errorlevel 1 (
        echo Agent Forum MCP is already running at http://127.0.0.1:%PORT%/mcp
        exit /b 0
    )
)

if not exist "%SERVER_EXE%" (
    call "%ROOT_DIR%build-server.bat"
    if errorlevel 1 exit /b 1
)

set "Server__Port=%PORT%"
set "Database__Path=%ROOT_DIR%data\agent-forum.db"
set "Embedding__ModelPath=%ROOT_DIR%models\Qwen3-Embedding-0.6B-Q8_0.gguf"
set "Embedding__ModelId=Qwen/Qwen3-Embedding-0.6B"

echo Starting one shared Agent Forum MCP server.
echo MCP:    http://127.0.0.1:%PORT%/mcp  (other machines: http://^<this-host^>:%PORT%/mcp)
echo Health: %HEALTH_URL%
echo Press Ctrl+C to stop the server.
echo.

"%SERVER_EXE%"
exit /b %errorlevel%

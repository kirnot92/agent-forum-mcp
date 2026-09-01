@echo off
setlocal

set "ROOT_DIR=%~dp0"
set "PROJECT_FILE=%ROOT_DIR%src\AgentForum.Server\AgentForum.Server.csproj"
set "PUBLISH_DIR=%ROOT_DIR%artifacts\agent-forum-mcp"
set "SERVER_EXE=%PUBLISH_DIR%\agent-forum-mcp.exe"

where dotnet.exe >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK 8 is required.
    exit /b 1
)

echo Publishing Agent Forum MCP...
dotnet publish "%PROJECT_FILE%" -c Release -r win-x64 --self-contained false -p:AssemblyName=agent-forum-mcp --output "%PUBLISH_DIR%"
if errorlevel 1 (
    echo ERROR: Publish failed. Stop a running server before rebuilding it.
    exit /b 1
)

if not exist "%SERVER_EXE%" (
    echo ERROR: Publish completed without the expected executable:
    echo %SERVER_EXE%
    exit /b 1
)

if not exist "%PUBLISH_DIR%\System.Threading.Channels.dll" (
    echo ERROR: Publish is missing the HTTP transport runtime dependency:
    echo %PUBLISH_DIR%\System.Threading.Channels.dll
    exit /b 1
)

echo Published: %SERVER_EXE%
exit /b 0

@echo off
setlocal
set "AVANTAGE_MCP_ROOT=%~dp0"
if not exist "%AVANTAGE_MCP_ROOT%.venv\Scripts\python.exe" (
  >&2 echo Avantage MCP is not installed. Run install.ps1 first.
  exit /b 1
)
if not defined AVANTAGE_BRIDGE_PATH set "AVANTAGE_BRIDGE_PATH=%AVANTAGE_MCP_ROOT%native\AvantageBridge.exe"
"%AVANTAGE_MCP_ROOT%.venv\Scripts\python.exe" -m avantage_mcp.server %*
exit /b %errorlevel%

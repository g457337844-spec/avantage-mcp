[CmdletBinding()]
param(
    [string]$Python,
    [string]$AvantageHome = $env:AVANTAGE_HOME,
    [switch]$RegisterCodex,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ServerName = 'avantage'
)
$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$venvRoot = Join-Path $projectRoot '.venv'
$venvPython = Join-Path $venvRoot 'Scripts\python.exe'
$bridgePath = Join-Path $projectRoot 'native\AvantageBridge.exe'
if (!$AvantageHome) { $AvantageHome = Join-Path $env:ProgramFiles 'Thermo Scientific\Avantage' }
$AvantageHome = [IO.Path]::GetFullPath($AvantageHome)

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Command failed with exit code $LASTEXITCODE`: $Executable" }
}

if (!(Test-Path -LiteralPath (Join-Path $AvantageHome 'bin\Interop.VGDATASTORELib.dll'))) {
    throw "Installed Avantage interop library not found under: $AvantageHome"
}

if (!$Python) { $Python = $env:AVANTAGE_PYTHON }
if (!$Python) {
    $pythonCommand = Get-Command python.exe -ErrorAction SilentlyContinue
    if ($pythonCommand -and $pythonCommand.Source -notmatch '\\WindowsApps\\') {
        $Python = $pythonCommand.Source
    }
}
if (!$Python) {
    $pythonLauncher = Get-Command py.exe -ErrorAction SilentlyContinue
    if ($pythonLauncher) {
        $resolved = & $pythonLauncher.Source -3 -c 'import sys; print(sys.executable)'
        if ($LASTEXITCODE -eq 0) { $Python = ([string]$resolved).Trim() }
    }
}
if (!$Python) {
    $bundledPython = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
    if (Test-Path -LiteralPath $bundledPython) { $Python = $bundledPython }
}
if (!$Python -or !(Test-Path -LiteralPath $Python)) {
    throw 'Python 3.10+ is required. Supply -Python with the full path to python.exe.'
}
$Python = [IO.Path]::GetFullPath($Python)
Invoke-Checked $Python @('-c', 'import struct,sys; assert sys.version_info >= (3,10), ''Python 3.10+ required''; assert struct.calcsize(''P'')==8, ''64-bit Python required''')

& (Join-Path $projectRoot 'scripts\build-native.ps1') -AvantageHome $AvantageHome
if (!(Test-Path -LiteralPath $bridgePath)) { throw 'Native bridge build did not produce AvantageBridge.exe.' }

if (!(Test-Path -LiteralPath $venvPython)) {
    Invoke-Checked $Python @('-m', 'venv', $venvRoot)
}
Invoke-Checked $venvPython @('-m', 'pip', 'install', '--disable-pip-version-check', '-r', (Join-Path $projectRoot 'requirements.lock.txt'))
Invoke-Checked $venvPython @('-m', 'pip', 'install', '--disable-pip-version-check', '--no-deps', '--editable', $projectRoot)
Invoke-Checked $venvPython @('-m', 'avantage_mcp.server', '--help')

$serverEntry = [ordered]@{
    command = $venvPython
    args = @('-m', 'avantage_mcp.server')
    env = [ordered]@{ AVANTAGE_BRIDGE_PATH = $bridgePath; AVANTAGE_HOME = $AvantageHome; AVANTAGE_TIMEOUT_SECONDS = '120' }
}
$serverMap = [ordered]@{}
$serverMap[$ServerName] = $serverEntry
$config = [ordered]@{ mcpServers = $serverMap } | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText((Join-Path $projectRoot 'mcp-config.json'), $config, [Text.UTF8Encoding]::new($false))

if ($RegisterCodex) {
    $codexCommand = Get-Command codex -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$codexCommand) { throw 'Codex CLI was not found. Installation succeeded; import mcp-config.json into your MCP client.' }
    $listingText = & $codexCommand.Source mcp list --json
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read Codex MCP configuration; no registration changes made.' }
    $listing = $listingText | ConvertFrom-Json
    $existing = @($listing | Where-Object { $_.name -eq $ServerName })
    if ($existing.Count -gt 0) {
        $transport = $existing[0].transport
        $sameCommand = $transport.command -eq $venvPython
        $sameArguments = (@($transport.args) -join "`n") -eq (@('-m', 'avantage_mcp.server') -join "`n")
        $sameBridge = $transport.env.AVANTAGE_BRIDGE_PATH -eq $bridgePath
        $sameHome = $transport.env.AVANTAGE_HOME -eq $AvantageHome
        $sameTimeout = $transport.env.AVANTAGE_TIMEOUT_SECONDS -eq '120'
        if ($transport.type -eq 'stdio' -and $sameCommand -and $sameArguments -and $sameBridge -and $sameHome -and $sameTimeout) {
            Write-Output "Codex MCP '$ServerName' is already registered with this installation."
        } else {
            throw "Codex MCP '$ServerName' already exists with another configuration. Use a different -ServerName; the existing entry was preserved."
        }
    } else {
        & (Join-Path $projectRoot 'scripts\register-codex.ps1') -ConfigJson (Join-Path $projectRoot 'mcp-config.json') -ServerName $ServerName
    }
    & (Join-Path $projectRoot 'scripts\set-codex-timeout.ps1') -ServerName $ServerName -Seconds 180
}
Write-Output "Installed Avantage MCP: $projectRoot"
Write-Output "Client configuration: $(Join-Path $projectRoot 'mcp-config.json')"
if (!$RegisterCodex) { Write-Output 'Codex configuration was not changed. Use -RegisterCodex to register this server.' }

param(
    [Parameter(Mandatory=$true)][string]$ConfigJson,
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ServerName='avantage'
)
$ErrorActionPreference='Stop'
$entry=(Get-Content -LiteralPath $ConfigJson -Raw | ConvertFrom-Json).mcpServers.$ServerName
if(!$entry){throw 'Generated MCP configuration is missing the intended server.'}
$codexConfigRoot=$env:CODEX_HOME
if(!$codexConfigRoot){$codexConfigRoot=Join-Path $env:USERPROFILE '.codex'}
$configPath=[IO.Path]::GetFullPath((Join-Path $codexConfigRoot 'config.toml'))
if(!(Test-Path -LiteralPath $codexConfigRoot)){New-Item -ItemType Directory -Path $codexConfigRoot | Out-Null}
$original=if(Test-Path -LiteralPath $configPath){[IO.File]::ReadAllBytes($configPath)}else{[byte[]]@()}
$text=[Text.Encoding]::UTF8.GetString($original)
$namePattern=[Regex]::Escape($ServerName)
if($text -match "(?m)^\[mcp_servers\.(?:$namePattern|`"$namePattern`")(?:\]|\.)"){
    throw "Codex MCP '$ServerName' already exists. Its configuration was preserved."
}
function TomlString([string]$Value){return ($Value | ConvertTo-Json -Compress)}
$lines=@('',"[mcp_servers.$ServerName]",('command = '+(TomlString $entry.command)),('args = ['+((@($entry.args) | ForEach-Object {TomlString $_}) -join ', ')+']'),'tool_timeout_sec = 180',"[mcp_servers.$ServerName.env]")
foreach($property in $entry.env.PSObject.Properties){
    if($property.Name -notmatch '^[A-Za-z_][A-Za-z0-9_]*$'){throw 'Unexpected environment key in generated configuration.'}
    $lines+=($property.Name+' = '+(TomlString ([string]$property.Value)))
}
$suffix=[Text.Encoding]::UTF8.GetBytes((($lines -join "`n")+"`n"))
$bytes=New-Object byte[] ($original.Length+$suffix.Length)
[Array]::Copy($original,0,$bytes,0,$original.Length)
[Array]::Copy($suffix,0,$bytes,$original.Length,$suffix.Length)
$temporary=Join-Path $codexConfigRoot ('.avantage-registration-'+[Guid]::NewGuid().ToString('N')+'.tmp')
$backup=$configPath+'.avantage-before-register-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss')+'-'+[Guid]::NewGuid().ToString('N')+'.bak'
try{
    [IO.File]::WriteAllBytes($temporary,$bytes)
    $current=if(Test-Path -LiteralPath $configPath){[IO.File]::ReadAllBytes($configPath)}else{[byte[]]@()}
    if([Convert]::ToBase64String($current) -ne [Convert]::ToBase64String($original)){throw 'Codex configuration changed concurrently; it was preserved. Retry installation.'}
    if(Test-Path -LiteralPath $configPath){[IO.File]::Replace($temporary,$configPath,$backup)}else{[IO.File]::Move($temporary,$configPath)}
}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
Write-Output "Registered Codex MCP '$ServerName' while retaining existing configuration bytes."

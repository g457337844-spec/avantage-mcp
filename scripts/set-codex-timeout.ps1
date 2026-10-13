param(
    [ValidatePattern('^[A-Za-z0-9_-]+$')][string]$ServerName = 'avantage',
    [ValidateRange(1,3600)][int]$Seconds = 180
)
$ErrorActionPreference='Stop'
$codexConfigRoot=$env:CODEX_HOME
if(!$codexConfigRoot){$codexConfigRoot=Join-Path $env:USERPROFILE '.codex'}
$configPath=[IO.Path]::GetFullPath((Join-Path $codexConfigRoot 'config.toml'))
if(!(Test-Path -LiteralPath $configPath)){throw 'Codex configuration was not found.'}
$original=[IO.File]::ReadAllBytes($configPath)
$content=[Text.Encoding]::UTF8.GetString($original)
$namePattern=[Regex]::Escape($ServerName)
$table=[Regex]::Match($content,"(?ms)^\[mcp_servers\.(?:$namePattern|`"$namePattern`")\][^\r\n]*(?:\r?\n)(?<body>.*?)(?=^\[|\z)")
if(!$table.Success){throw "Cannot locate the registered Codex MCP '$ServerName'; timeout was not changed."}
$body=$table.Groups['body'].Value
$setting=[Regex]::Match($body,'(?m)^tool_timeout_sec\s*=\s*(?<number>\d+(?:\.\d+)?)\s*(?:#.*)?$')
if($setting.Success -and [double]::Parse($setting.Groups['number'].Value,[Globalization.CultureInfo]::InvariantCulture) -ge $Seconds){return}
if(!$setting.Success -and $body -match '(?m)^tool_timeout_sec\s*='){throw 'Existing timeout format was not recognized; configuration was preserved.'}
$newline=if($content.Contains("`r`n")){"`r`n"}else{"`n"}
if($setting.Success){$newBody=$body.Remove($setting.Index,$setting.Length).Insert($setting.Index,"tool_timeout_sec = $Seconds")}
else{$newBody="tool_timeout_sec = $Seconds$newline$body"}
$updated=$content.Remove($table.Groups['body'].Index,$body.Length).Insert($table.Groups['body'].Index,$newBody)
$temporary=Join-Path (Split-Path -Parent $configPath) ('.avantage-config-'+[Guid]::NewGuid().ToString('N')+'.tmp')
$backup=$configPath+'.avantage-'+[DateTime]::UtcNow.ToString('yyyyMMddHHmmss')+'-'+[Guid]::NewGuid().ToString('N')+'.bak'
try{
    [IO.File]::WriteAllText($temporary,$updated,[Text.UTF8Encoding]::new($false))
    $current=[IO.File]::ReadAllBytes($configPath)
    if([Convert]::ToBase64String($current) -ne [Convert]::ToBase64String($original)){throw 'Codex configuration changed concurrently; retry installation. It was preserved.'}
    [IO.File]::Replace($temporary,$configPath,$backup)
}finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
Write-Output "Codex MCP '$ServerName' tool timeout: $Seconds seconds."

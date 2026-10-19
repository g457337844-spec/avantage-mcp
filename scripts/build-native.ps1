param([string]$AvantageHome = $env:AVANTAGE_HOME, [string]$OutputPath)
$ErrorActionPreference='Stop'
if(!$AvantageHome){$AvantageHome=Join-Path $env:ProgramFiles 'Thermo Scientific\Avantage'}
$projectRoot=Split-Path -Parent $PSScriptRoot
$compiler='C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if(!(Test-Path -LiteralPath $compiler)){$compiler=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'}
$interop=Join-Path $AvantageHome 'bin\Interop.VGDATASTORELib.dll'
if(!(Test-Path -LiteralPath $interop)){throw "Avantage interop library not found: $interop"}
$sourceFiles=@(Get-ChildItem -LiteralPath (Join-Path $projectRoot 'native') -Filter '*.cs' -File | ForEach-Object {$_.FullName})
$output=if($OutputPath){[IO.Path]::GetFullPath($OutputPath)}else{Join-Path $projectRoot 'native\AvantageBridge.exe'}
& $compiler /nologo /target:exe /platform:x64 /optimize+ /debug:pdbonly "/out:$output" /r:System.Web.Extensions.dll /r:Microsoft.CSharp.dll "/r:$interop" @sourceFiles
if($LASTEXITCODE -ne 0){throw 'Native bridge build failed.'}
Write-Output $output

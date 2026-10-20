param(
    [Parameter(Mandatory = $true)]
    [string]$OutputPath,
    [switch]$IncludeNativeIdentity,
    [switch]$IncludePeakRoundtrip,
    [switch]$IncludeModelState,
    [switch]$IncludeEmptyRegion
)
$ErrorActionPreference = 'Stop'

# Build the read-only in-process context probe as a standalone x64 .NET
# Framework COM library. This script only compiles: it never registers the
# assembly, never touches the registry, and never starts Avantage.
#
# It intentionally does NOT reuse scripts/build-native.ps1, because that script
# globs native/*.cs into AvantageBridge.exe. The probe must stay out of the
# bridge build.

$projectRoot = Split-Path -Parent $PSScriptRoot
$source = Join-Path $projectRoot 'native\inprocess\ContextProbe.cs'
if (!(Test-Path -LiteralPath $source)) { throw "Probe source not found: $source" }

$compiler = 'C:\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
if (!(Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
}
if (!(Test-Path -LiteralPath $compiler)) { throw 'No C# compiler found (Roslyn or .NET Framework csc.exe).' }

$output = [IO.Path]::GetFullPath($OutputPath)
if ([IO.Path]::GetExtension($output) -ne '.dll') { throw "OutputPath must end in .dll: $output" }
$outputDirectory = Split-Path -Parent $output
if (!(Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
}

# System.Web.Extensions supplies JavaScriptSerializer, matching the bridge's
# existing convention. No third-party or Avantage assembly is referenced: the
# probe must not link against VGDATASTORELib or any private host type.
# mscorlib (and the default csc.rsp references) are implicit.
$arguments = @(
    '/nologo',
    '/target:library',
    '/platform:x64',
    '/optimize+',
    '/debug:pdbonly',
    "/out:$output",
    '/r:System.Web.Extensions.dll',
    $source
)
if ($IncludeNativeIdentity -or $IncludePeakRoundtrip -or $IncludeModelState -or $IncludeEmptyRegion) {
    $arguments += Join-Path $projectRoot 'native\inprocess\NativeContextIdentity.cs'
    $arguments += Join-Path $projectRoot 'native\inprocess\IdentityProbe.cs'
}
if ($IncludeModelState -or $IncludeEmptyRegion) {
    $arguments += Join-Path $projectRoot 'native\inprocess\NativeDocumentAccess.cs'
    $arguments += Join-Path $projectRoot 'native\inprocess\ModelStateProbe.cs'
    $arguments += Join-Path $projectRoot 'native\inprocess\BackgroundContract.cs'
    $arguments += Join-Path $projectRoot 'native\NativeRawFingerprint.cs'
    $arguments += Join-Path $projectRoot 'native\NativeModelSnapshotCore.cs'
    if (!$IncludePeakRoundtrip) { $arguments += Join-Path $projectRoot 'native\FitInterfaces.cs' }
    $arguments += '/r:C:\Program Files\Thermo Scientific\Avantage\bin\Interop.VGDATASTORELib.dll'
}
if ($IncludeEmptyRegion) { $arguments += Join-Path $projectRoot 'native\inprocess\EmptyRegionProbe.cs' }
if ($IncludePeakRoundtrip) {
    $arguments += Join-Path $projectRoot 'native\inprocess\PeakRoundtripProbe.cs'
    $arguments += Join-Path $projectRoot 'native\inprocess\PeakPersistenceProbe.cs'
    $arguments += Join-Path $projectRoot 'native\FitInterfaces.cs'
}

& $compiler @arguments
if ($LASTEXITCODE -ne 0) { throw 'In-process probe build failed.' }

Write-Output $output

[CmdletBinding()]
param(
    [ValidateSet('all','win-x64','osx-arm64')][string]$Runtime = 'all',
    [string]$OutputRoot
)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..\..')).Path
$project = Join-Path $PSScriptRoot 'OpusMac.Packaging.csproj'
$arguments = @('run','--project',$project,'-c','Release','--','publish',$projectRoot,$Runtime)
if ($OutputRoot) { $arguments += [System.IO.Path]::GetFullPath($OutputRoot) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw "Publication failed with exit code $LASTEXITCODE. Inspect the retained stage logs." }


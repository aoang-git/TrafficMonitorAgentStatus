param([string]$BuildDirectory = (Join-Path $PSScriptRoot '..\build\x64'))
$ErrorActionPreference = 'Stop'
dotnet run --project (Join-Path $PSScriptRoot 'StateTests.csproj') -- --integration $BuildDirectory
if ($LASTEXITCODE) { throw 'Runtime integration checks failed.' }

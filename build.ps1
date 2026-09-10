param(
  [string]$Toolchain = (Join-Path $PSScriptRoot '..\msvc'),
  [switch]$SelfContained,
  # Assemble packaging\ into an installable release. On by default; pass
  # -Package $false to build only.
  [bool]$Package = $true
)
$ErrorActionPreference = 'Stop'
$sdkRoot = Join-Path $Toolchain 'Windows Kits\10'
$vcRoot = Join-Path $Toolchain 'VC\Tools\MSVC'
$sdkInclude = Join-Path $sdkRoot 'Include'
if (!(Test-Path -LiteralPath $vcRoot -PathType Container) -or !(Test-Path -LiteralPath $sdkInclude -PathType Container)) {
  throw "Toolchain not found at '$Toolchain'. Pass -Toolchain with a directory containing VC\Tools\MSVC and Windows Kits\10."
}
$vc = Get-ChildItem -LiteralPath $vcRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdk = Get-ChildItem -LiteralPath $sdkInclude -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (!$vc -or !$sdk) { throw 'MSVC or Windows SDK is incomplete.' }
$currentPath = [Environment]::GetEnvironmentVariable('Path')
$env:Path = (Join-Path $vc.FullName 'bin\Hostx64\x64') + ';' + $currentPath
$env:INCLUDE = "$($vc.FullName)\include;$($sdk.FullName)\ucrt;$($sdk.FullName)\shared;$($sdk.FullName)\um"
$env:LIB = "$($vc.FullName)\lib\x64;$sdkRoot\Lib\$($sdk.Name)\ucrt\x64;$sdkRoot\Lib\$($sdk.Name)\um\x64"
$env:VSCMD_SKIP_SENDTELEMETRY = '1'
$out = Join-Path $PSScriptRoot 'build\x64'
New-Item -ItemType Directory -Path $out -Force | Out-Null
Push-Location $out
try {
  & cl.exe /nologo /std:c++17 /EHsc /MT /O2 /W4 /utf-8 /LD (Join-Path $PSScriptRoot 'plugin\AgentStatus.cpp') /link /OUT:AgentStatus.dll user32.lib advapi32.lib shell32.lib
  if ($LASTEXITCODE) { throw 'Native plugin build failed.' }
  & cl.exe /nologo /std:c++17 /EHsc /MT /O2 /W4 /utf-8 (Join-Path $PSScriptRoot 'tests\PluginProbe.cpp') /link /OUT:PluginProbe.exe
  if ($LASTEXITCODE) { throw 'Native probe build failed.' }
  & cl.exe /nologo /std:c++17 /EHsc /MT /O2 /W4 /utf-8 (Join-Path $PSScriptRoot 'plugin\AgentStatusEmit.cpp') /link /OUT:AgentStatusEmit.exe /SUBSYSTEM:WINDOWS advapi32.lib shell32.lib
  if ($LASTEXITCODE) { throw 'Native emitter build failed.' }
} finally { Pop-Location }
if ($SelfContained) {
  dotnet publish (Join-Path $PSScriptRoot 'bridge\AgentStatusBridge.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'AgentStatus') --nologo
} else {
  dotnet publish (Join-Path $PSScriptRoot 'bridge\AgentStatusBridge.csproj') -c Release --self-contained false -o (Join-Path $out 'AgentStatus') --nologo
}
if ($LASTEXITCODE) { throw 'Bridge build failed.' }
Copy-Item -LiteralPath (Join-Path $out 'AgentStatusEmit.exe') -Destination (Join-Path $out 'AgentStatus\AgentStatusEmit.exe') -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'adapters\AgentStatusJsonHook.cmd') -Destination (Join-Path $out 'AgentStatus\AgentStatusJsonHook.cmd') -Force
Write-Output "Build output: $out"

if ($Package) {
  $pkg = Join-Path $PSScriptRoot 'packaging'
  $pkgBridge = Join-Path $pkg 'AgentStatus'
  New-Item -ItemType Directory -Path $pkgBridge -Force | Out-Null
  # Ship the bridge self-contained: end users must not need a .NET install.
  dotnet publish (Join-Path $PSScriptRoot 'bridge\AgentStatusBridge.csproj') -c Release -r win-x64 --self-contained true -o $pkgBridge --nologo
  if ($LASTEXITCODE) { throw 'Package bridge publish failed.' }
  Copy-Item -LiteralPath (Join-Path $out 'AgentStatus.dll') -Destination (Join-Path $pkg 'AgentStatus.dll') -Force
  Copy-Item -LiteralPath (Join-Path $out 'AgentStatusEmit.exe') -Destination (Join-Path $pkgBridge 'AgentStatusEmit.exe') -Force
  # The JSON wrapper and the adapter installer must travel with the package:
  # Install-AgentHooks.ps1 refuses to run when AgentStatusJsonHook.cmd is not
  # beside it, so leaving these out made Codex CLI / Gemini / Cursor
  # unreachable for anyone installing from the package.
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'adapters\AgentStatusJsonHook.cmd') -Destination (Join-Path $pkgBridge 'AgentStatusJsonHook.cmd') -Force
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'adapters\Install-AgentHooks.ps1') -Destination (Join-Path $pkgBridge 'Install-AgentHooks.ps1') -Force
  Write-Output "Package output: $pkg"
}

param([string]$Toolchain = (Join-Path $PSScriptRoot '..\msvc'), [switch]$SelfContained)
$ErrorActionPreference = 'Stop'
$vc = Get-ChildItem -LiteralPath (Join-Path $Toolchain 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdkRoot = Join-Path $Toolchain 'Windows Kits\10'
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (!$vc -or !$sdk) { throw 'MSVC 或 Windows SDK 不完整，请传入便携工具链目录。' }
$env:PATH = (Join-Path $vc.FullName 'bin\Hostx64\x64') + ';' + $env:PATH
$env:INCLUDE = "$($vc.FullName)\include;$($sdk.FullName)\ucrt;$($sdk.FullName)\shared;$($sdk.FullName)\um"
$env:LIB = "$($vc.FullName)\lib\x64;$sdkRoot\Lib\$($sdk.Name)\ucrt\x64;$sdkRoot\Lib\$($sdk.Name)\um\x64"
$env:VSCMD_SKIP_SENDTELEMETRY = '1'
$out = Join-Path $PSScriptRoot 'build\x64'
New-Item -ItemType Directory -Path $out -Force | Out-Null
Push-Location $out
try {
  & cl.exe /nologo /std:c++17 /EHsc /MT /O2 /W4 /utf-8 /LD (Join-Path $PSScriptRoot 'plugin\AgentStatus.cpp') /link /OUT:AgentStatus.dll user32.lib advapi32.lib shell32.lib
  if ($LASTEXITCODE) { throw '插件编译失败' }
  & cl.exe /nologo /std:c++17 /EHsc /MT /O2 /W4 /utf-8 (Join-Path $PSScriptRoot 'tests\PluginProbe.cpp') /link /OUT:PluginProbe.exe
  if ($LASTEXITCODE) { throw '原生验证程序编译失败' }
  & cl.exe /nologo /std:c++17 /EHsc /MT /O2 /W4 /utf-8 (Join-Path $PSScriptRoot 'plugin\AgentStatusEmit.cpp') /link /OUT:AgentStatusEmit.exe /SUBSYSTEM:WINDOWS advapi32.lib shell32.lib
  if ($LASTEXITCODE) { throw '原生事件入口编译失败' }
} finally { Pop-Location }
if ($SelfContained) {
  dotnet publish (Join-Path $PSScriptRoot 'bridge\AgentStatusBridge.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $out 'AgentStatus') --nologo
} else {
  dotnet publish (Join-Path $PSScriptRoot 'bridge\AgentStatusBridge.csproj') -c Release --self-contained false -o (Join-Path $out 'AgentStatus') --nologo
}
if ($LASTEXITCODE) { throw '接收器构建失败' }
Copy-Item -LiteralPath (Join-Path $out 'AgentStatusEmit.exe') -Destination (Join-Path $out 'AgentStatus\AgentStatusEmit.exe')
Write-Output "Build output: $out"

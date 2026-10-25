param(
  [string]$Toolchain,
  [string]$WindowsSdk,
  [switch]$SelfContained,
  [bool]$Package=$true
)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath($PSScriptRoot)
function Clear-Output([string]$relative) {
  $path = [IO.Path]::GetFullPath((Join-Path $workspace $relative))
  if (!$path.StartsWith($workspace + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Output escaped workspace.' }
  if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Recurse -Force }
}
if (!$Toolchain) {
  $vswhere = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio\Installer\vswhere.exe'
  if (!(Test-Path -LiteralPath $vswhere)) { throw 'Visual Studio C++ tools not found. Install Build Tools or pass -Toolchain.' }
  $Toolchain = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
  if ($LASTEXITCODE -or !$Toolchain) { throw 'MSVC x64 tools not found.' }
  $Toolchain = ([string]$Toolchain).Trim()
}
$Toolchain = (Resolve-Path -LiteralPath $Toolchain).Path
if (!$WindowsSdk) {
  $bundledSdk = Join-Path $Toolchain 'Windows Kits\10'
  $WindowsSdk = if (Test-Path -LiteralPath $bundledSdk) { $bundledSdk } else { Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10' }
}
$sdkRoot = (Resolve-Path -LiteralPath $WindowsSdk).Path
$vc = Get-ChildItem -LiteralPath (Join-Path $Toolchain 'VC\Tools\MSVC') -Directory | Sort-Object Name -Descending | Select-Object -First 1
$sdk = Get-ChildItem -LiteralPath (Join-Path $sdkRoot 'Include') -Directory | Sort-Object Name -Descending | Select-Object -First 1
if (!$vc -or !$sdk) { throw 'MSVC or Windows SDK is incomplete.' }
[xml]$project = Get-Content -Raw -LiteralPath (Join-Path $workspace 'bridge\AgentStatusBridge.csproj')
$version = [string]$project.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a three-part release version.' }
foreach ($versionFile in @('bridge\Transport.cs','plugin\AgentStatus.cpp')) {
  if (!(Select-String -LiteralPath (Join-Path $workspace $versionFile) -SimpleMatch ('"'+$version+'"') -Quiet)) { throw "Version mismatch in $versionFile" }
}
Clear-Output 'build\x64'
$out = Join-Path $workspace 'build\x64'
New-Item -ItemType Directory -Path $out -Force | Out-Null
$env:Path = (Join-Path $vc.FullName 'bin\Hostx64\x64') + ';' + $env:Path
$env:INCLUDE = "$($vc.FullName)\include;$($sdk.FullName)\ucrt;$($sdk.FullName)\shared;$($sdk.FullName)\um"
$env:LIB = "$($vc.FullName)\lib\x64;$sdkRoot\Lib\$($sdk.Name)\ucrt\x64;$sdkRoot\Lib\$($sdk.Name)\um\x64"
$env:VSCMD_SKIP_SENDTELEMETRY = '1'
Push-Location $out
try {
  & cl.exe /nologo /std:c++17 /EHsc /MT /O2 /W4 /utf-8 /LD (Join-Path $workspace 'plugin\AgentStatus.cpp') /link /OUT:AgentStatus.dll user32.lib advapi32.lib shell32.lib
  if ($LASTEXITCODE) { throw 'Native plugin build failed.' }
  & cl.exe /nologo /std:c++17 /EHsc /MT /O2 /W4 /utf-8 (Join-Path $workspace 'tests\PluginProbe.cpp') /link /OUT:PluginProbe.exe
  if ($LASTEXITCODE) { throw 'Native probe build failed.' }
} finally { Pop-Location }
$publishArgs = @('publish', (Join-Path $workspace 'bridge\AgentStatusBridge.csproj'), '-c','Release','-r','win-x64','--self-contained',$SelfContained.IsPresent.ToString().ToLowerInvariant(),'-o',(Join-Path $out 'AgentStatus'),'--nologo')
& dotnet @publishArgs
if ($LASTEXITCODE) { throw 'Bridge publish failed.' }
if (!$Package) { return }
$packageName = "TrafficMonitorAgentStatus-v$version-win-x64"
Clear-Output ('dist\'+$packageName)
$pkg = Join-Path $workspace ('dist\'+$packageName)
New-Item -ItemType Directory -Path (Join-Path $pkg 'AgentStatus'),(Join-Path $pkg 'docs'),(Join-Path $pkg 'licenses\dotnet') -Force | Out-Null
& dotnet publish (Join-Path $workspace 'bridge\AgentStatusBridge.csproj') -c Release -r win-x64 --self-contained true -o (Join-Path $pkg 'AgentStatus') --nologo
if ($LASTEXITCODE) { throw 'Package publish failed.' }
Copy-Item -LiteralPath (Join-Path $out 'AgentStatus.dll') -Destination $pkg
foreach ($file in @('README.md','LICENSE','THIRD_PARTY_NOTICES.md')) { Copy-Item -LiteralPath (Join-Path $workspace $file) -Destination $pkg }
foreach ($file in @('Install-PluginFiles.ps1','Remove-LegacyHooks.ps1')) { Copy-Item -LiteralPath (Join-Path $workspace ('tools\'+$file)) -Destination $pkg }
Get-ChildItem -LiteralPath (Join-Path $workspace 'docs') -Force | Copy-Item -Destination (Join-Path $pkg 'docs') -Recurse
$dotnetRoot = Split-Path -Parent (Get-Command dotnet.exe).Source
foreach ($notice in @('LICENSE.txt','ThirdPartyNotices.txt')) { Copy-Item -LiteralPath (Join-Path $dotnetRoot $notice) -Destination (Join-Path $pkg 'licenses\dotnet') }
$manifest = Get-ChildItem -LiteralPath $pkg -Recurse -File | Sort-Object FullName | ForEach-Object {
  '{0}  {1}' -f (Get-FileHash -Algorithm SHA256 -LiteralPath $_.FullName).Hash.ToLowerInvariant(),$_.FullName.Substring($pkg.Length+1).Replace('\','/')
}
[IO.File]::WriteAllLines((Join-Path $pkg 'SHA256SUMS.txt'),$manifest,[Text.UTF8Encoding]::new($false))
$zip = Join-Path $workspace ('dist\'+$packageName+'.zip')
Compress-Archive -LiteralPath $pkg -DestinationPath $zip -CompressionLevel Optimal -Force
[IO.File]::WriteAllText(($zip+'.sha256'),((Get-FileHash -Algorithm SHA256 -LiteralPath $zip).Hash.ToLowerInvariant()+"  $packageName.zip`n"),[Text.UTF8Encoding]::new($false))
Write-Output "Package: $pkg"
Write-Output "Archive: $zip"

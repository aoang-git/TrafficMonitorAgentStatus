param(
  [Parameter(Mandatory=$true)][string]$TrafficMonitorDirectory,
  [switch]$StartBridge
)
$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $TrafficMonitorDirectory).Path
$exe = Join-Path $target 'TrafficMonitor.exe'
if (!(Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'TrafficMonitor.exe was not found in the specified directory.' }
$pe = [System.IO.File]::ReadAllBytes($exe)
$peOffset = [BitConverter]::ToInt32($pe, 0x3c)
if ([BitConverter]::ToUInt16($pe, $peOffset + 4) -ne 0x8664) { throw 'This package supports x64 TrafficMonitor only.' }
$dllSource = Join-Path $PSScriptRoot 'AgentStatus.dll'
$bridgeSource = Join-Path $PSScriptRoot 'AgentStatus'
if (!(Test-Path -LiteralPath $dllSource) -or !(Test-Path -LiteralPath (Join-Path $bridgeSource 'AgentStatusBridge.exe'))) { throw 'The package is incomplete.' }
$plugins = Join-Path $target 'plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
$pluginTarget = Join-Path $plugins 'AgentStatus.dll'
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
if (Test-Path -LiteralPath $pluginTarget) {
  Copy-Item -LiteralPath $pluginTarget -Destination ($pluginTarget + '.backup-' + $stamp)
}
# Every overwrite is restricted to this plugin's named files. No removals or mirror operations.
Copy-Item -LiteralPath $dllSource -Destination $pluginTarget
$bridgeTarget = Join-Path $plugins 'AgentStatus'
New-Item -ItemType Directory -Path $bridgeTarget -Force | Out-Null
foreach ($file in Get-ChildItem -LiteralPath $bridgeSource -File) {
  $destination = Join-Path $bridgeTarget $file.Name
  if (Test-Path -LiteralPath $destination) { Copy-Item -LiteralPath $destination -Destination ($destination + '.backup-' + $stamp) }
  Copy-Item -LiteralPath $file.FullName -Destination $destination
}
if ($StartBridge) { Start-Process -FilePath (Join-Path $bridgeTarget 'AgentStatusBridge.exe') -ArgumentList 'start' -WindowStyle Hidden }
Write-Output 'Plugin files installed. Restart TrafficMonitor and enable Agent Status in the taskbar display items.'
Write-Output ('Bridge path: ' + (Join-Path $bridgeTarget 'AgentStatusBridge.exe'))
Write-Output 'Existing Agent settings were not modified. Use Configure-Claude.ps1 to configure hooks.'

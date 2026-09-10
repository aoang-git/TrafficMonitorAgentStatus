param(
  [Parameter(Mandatory = $true)]
  [string]$TrafficMonitorDirectory,
  [string]$PackageDirectory = $PSScriptRoot
)

$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $TrafficMonitorDirectory).Path
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$trafficMonitorExe = Join-Path $target 'TrafficMonitor.exe'
if (!(Test-Path -LiteralPath $trafficMonitorExe -PathType Leaf)) {
  throw "TrafficMonitor.exe was not found in '$target'."
}

$pe = [IO.File]::ReadAllBytes($trafficMonitorExe)
$peOffset = [BitConverter]::ToInt32($pe, 0x3c)
if ([BitConverter]::ToUInt16($pe, $peOffset + 4) -ne 0x8664) {
  throw 'This release supports x64 TrafficMonitor only.'
}

$dllSource = Join-Path $package 'AgentStatus.dll'
$bridgeSource = Join-Path $package 'AgentStatus'
if (!(Test-Path -LiteralPath $dllSource -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $bridgeSource 'AgentStatusBridge.exe') -PathType Leaf) -or
    !(Test-Path -LiteralPath (Join-Path $bridgeSource 'AgentStatusEmit.exe') -PathType Leaf)) {
  throw "The release package is incomplete at '$package'."
}

$plugins = Join-Path $target 'plugins'
$bridgeTarget = Join-Path $plugins 'AgentStatus'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
New-Item -ItemType Directory -Path $bridgeTarget -Force | Out-Null

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
$pluginTarget = Join-Path $plugins 'AgentStatus.dll'
if (Test-Path -LiteralPath $pluginTarget -PathType Leaf) {
  Copy-Item -LiteralPath $pluginTarget -Destination "$pluginTarget.backup-$stamp"
}
Copy-Item -LiteralPath $dllSource -Destination $pluginTarget -Force

foreach ($file in Get-ChildItem -LiteralPath $bridgeSource -File) {
  $destination = Join-Path $bridgeTarget $file.Name
  if (Test-Path -LiteralPath $destination -PathType Leaf) {
    Copy-Item -LiteralPath $destination -Destination "$destination.backup-$stamp"
  }
  Copy-Item -LiteralPath $file.FullName -Destination $destination -Force
}

Write-Output "Plugin files copied to '$plugins'."
Write-Output "Bridge path: '$(Join-Path $bridgeTarget 'AgentStatusBridge.exe')'."
Write-Output 'Start the Bridge from the normal desktop user session, configure the required Agent hooks, then restart TrafficMonitor.'

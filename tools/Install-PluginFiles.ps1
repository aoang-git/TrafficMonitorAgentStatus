param(
  [Parameter(Mandatory=$true)][string]$TrafficMonitorDirectory,
  [string]$PackageDirectory = $PSScriptRoot,
  [string]$UserProfileDirectory = [Environment]::GetFolderPath('UserProfile'),
  [string]$DataDirectory = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TrafficMonitorAgentStatus'),
  [switch]$SkipStartup,
  [switch]$SkipLaunch
)
$ErrorActionPreference = 'Stop'
$target = (Resolve-Path -LiteralPath $TrafficMonitorDirectory).Path.TrimEnd('\','/')
$package = (Resolve-Path -LiteralPath $PackageDirectory).Path
$trafficExe = Join-Path $target 'TrafficMonitor.exe'
if (!(Test-Path -LiteralPath $trafficExe -PathType Leaf)) { throw 'TrafficMonitor.exe not found.' }
$pe = [IO.File]::ReadAllBytes($trafficExe)
if ($pe.Length -lt 64) { throw 'Invalid TrafficMonitor executable.' }
$offset = [BitConverter]::ToInt32($pe,0x3c)
if ($offset -lt 0 -or $offset+6 -gt $pe.Length -or [BitConverter]::ToUInt16($pe,$offset+4) -ne 0x8664) { throw 'Only x64 TrafficMonitor is supported.' }
foreach ($relative in @('AgentStatus.dll','AgentStatus\AgentStatusBridge.exe','Remove-LegacyHooks.ps1','SHA256SUMS.txt')) {
  if (!(Test-Path -LiteralPath (Join-Path $package $relative) -PathType Leaf)) { throw "Incomplete package: $relative" }
}
foreach ($line in [IO.File]::ReadAllLines((Join-Path $package 'SHA256SUMS.txt'))) {
  if ($line -notmatch '^([0-9a-f]{64})  (.+)$') { throw 'Invalid package manifest.' }
  $expectedHash = $Matches[1]; $relative = $Matches[2]
  $file = [IO.Path]::GetFullPath((Join-Path $package $relative))
  if (!$file.StartsWith($package.TrimEnd('\','/')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Manifest path escaped package.' }
  if ((Get-FileHash -Algorithm SHA256 -LiteralPath $file).Hash -ne $expectedHash) { throw "Package checksum failed: $relative" }
}
$plugins = Join-Path $target 'plugins'
$bridgeTarget = [IO.Path]::GetFullPath((Join-Path $plugins 'AgentStatus'))
if ([IO.Path]::GetDirectoryName($bridgeTarget) -ne $plugins) { throw 'Invalid plugin target.' }
if ((Test-Path -LiteralPath $bridgeTarget) -and ((Get-Item -LiteralPath $bridgeTarget).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Refusing a linked plugin directory.' }
$pluginTarget = Join-Path $plugins 'AgentStatus.dll'
New-Item -ItemType Directory -Path $plugins,$DataDirectory -Force | Out-Null
$backup = Join-Path $DataDirectory ('backups\'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $backup -Force | Out-Null
$oldDlls = @(Get-ChildItem -LiteralPath $plugins -File | Where-Object { $_.Name -eq 'AgentStatus.dll' -or $_.Name -like 'AgentStatus.dll.backup-*' -or $_.Name -like 'AgentStatus.dll.bak-*' })
foreach ($file in $oldDlls) { Copy-Item -LiteralPath $file.FullName -Destination $backup }
if (Test-Path -LiteralPath $bridgeTarget) { Copy-Item -LiteralPath $bridgeTarget -Destination (Join-Path $backup 'AgentStatus') -Recurse }
$configPath = Join-Path $DataDirectory 'config.json'
if (Test-Path -LiteralPath $configPath) { Copy-Item -LiteralPath $configPath -Destination $backup }
if (!('TmasProcessPath' -as [type])) {
  Add-Type @"
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class TmasProcessPath {
 [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr OpenProcess(uint rights, bool inherit, int pid);
 [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr handle, uint flags, StringBuilder path, ref int length);
 [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
 public static string Get(int pid) { var handle=OpenProcess(0x1000,false,pid); if(handle==IntPtr.Zero) return ""; try { var text=new StringBuilder(32768); int size=text.Capacity; return QueryFullProcessImageName(handle,0,text,ref size)?text.ToString():""; } finally { CloseHandle(handle); } }
}
"@
}
$hostProcesses = @(Get-Process -Name TrafficMonitor -ErrorAction SilentlyContinue | Where-Object { [TmasProcessPath]::Get($_.Id) -eq $trafficExe })
$bridgeProcesses = @(Get-Process -Name AgentStatusBridge -ErrorAction SilentlyContinue | Where-Object { [TmasProcessPath]::Get($_.Id) -eq (Join-Path $bridgeTarget 'AgentStatusBridge.exe') })
foreach ($process in @($hostProcesses)+@($bridgeProcesses)) {
  if (!$process.CloseMainWindow() -or !$process.WaitForExit(3000)) { Stop-Process -Id $process.Id -Force -ErrorAction Stop }
  if (!$process.WaitForExit(5000)) { throw 'A plugin process did not exit.' }
}
try {
  & (Join-Path $package 'Remove-LegacyHooks.ps1') -PluginDirectory $bridgeTarget -BackupDirectory $backup -UserProfileDirectory $UserProfileDirectory
  # Exact owned installation directory, checked above; no other plugin directories are touched.
  if (Test-Path -LiteralPath $bridgeTarget) { Remove-Item -LiteralPath $bridgeTarget -Recurse -Force }
  foreach ($file in $oldDlls) { Remove-Item -LiteralPath $file.FullName -Force }
  Copy-Item -LiteralPath (Join-Path $package 'AgentStatus') -Destination $bridgeTarget -Recurse
  Copy-Item -LiteralPath (Join-Path $package 'AgentStatus.dll') -Destination $pluginTarget
  $configVersion = 0
  if (Test-Path -LiteralPath $configPath) { try { $configVersion = ([IO.File]::ReadAllText($configPath) | ConvertFrom-Json).configVersion } catch { } }
  if ($configVersion -ne 2) {
    $defaults = @{configVersion=2;showSource=$true;showReportedStates=$true;completionToastMs=5000;noticeToastMs=15000;maxWorkingMs=300000;maxWaitingMs=1800000;enabledSources=@{codex=$true;claude=$true;deepseek=$true;pi=$true};displayTexts=@{working='工作中';tool='调用工具';waiting='待确认';taskCompleted='任务完成';taskInterrupted='任务中断';unknown='状态未知'}}
    [IO.File]::WriteAllText($configPath,($defaults | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
  }
  # Persist exactly the current display states, retaining custom text for surviving keys.
  $installedConfig = [IO.File]::ReadAllText($configPath) | ConvertFrom-Json -AsHashtable
  if (!$installedConfig.Contains('showReportedStates')) { $installedConfig.showReportedStates = $true }
  $installedConfig.Remove('runtimeDirectory')
  $installedConfig.Remove('reportedStateLeaseMs')
  $displayDefaults = [ordered]@{working='工作中';tool='调用工具';waiting='待确认';taskCompleted='任务完成';taskInterrupted='任务中断';unknown='状态未知'}
  $displayTexts = [ordered]@{}
  foreach ($key in $displayDefaults.Keys) {
    $value = if ($installedConfig.displayTexts -is [Collections.IDictionary]) { $installedConfig.displayTexts[$key] } else { $null }
    $displayTexts[$key] = if ($value -is [string] -and ![string]::IsNullOrWhiteSpace($value)) { $value } else { $displayDefaults[$key] }
  }
  $installedConfig.displayTexts = $displayTexts
  [IO.File]::WriteAllText($configPath,($installedConfig | ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
  $bridgeExe = Join-Path $bridgeTarget 'AgentStatusBridge.exe'
  if (!$SkipStartup) {
    if (!(Test-Path -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run')) { New-Item -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' | Out-Null }
    New-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TrafficMonitorAgentStatus' -PropertyType String -Value ('"'+$bridgeExe+'" serve') -Force | Out-Null
  }
} catch {
  if (Test-Path -LiteralPath (Join-Path $backup 'AgentStatus')) {
    if (Test-Path -LiteralPath $bridgeTarget) { Remove-Item -LiteralPath $bridgeTarget -Recurse -Force }
    Copy-Item -LiteralPath (Join-Path $backup 'AgentStatus') -Destination $bridgeTarget -Recurse
  }
  foreach ($file in $oldDlls) { Copy-Item -LiteralPath (Join-Path $backup $file.Name) -Destination $plugins -Force }
  if (Test-Path -LiteralPath (Join-Path $backup 'config.json')) { Copy-Item -LiteralPath (Join-Path $backup 'config.json') -Destination $configPath -Force }
  foreach ($relative in @('.claude\settings.json','.cursor\hooks.json','.gemini\settings.json')) {
    $source = Join-Path $backup ('hooks\'+$relative)
    if (Test-Path -LiteralPath $source) { Copy-Item -LiteralPath $source -Destination (Join-Path $UserProfileDirectory $relative) -Force }
  }
  if ($hostProcesses.Count -gt 0 -and !$SkipLaunch) { Start-Process -FilePath $trafficExe -WorkingDirectory $target -WindowStyle Hidden | Out-Null }
  throw
}
if (!$SkipLaunch) {
  Start-Process -FilePath $bridgeExe -ArgumentList 'serve' -WorkingDirectory $bridgeTarget -WindowStyle Hidden | Out-Null
  if ($hostProcesses.Count -gt 0) { Start-Process -FilePath $trafficExe -WorkingDirectory $target -WindowStyle Hidden | Out-Null }
}
Write-Output "Installed: $pluginTarget"
Write-Output "Backup: $backup"

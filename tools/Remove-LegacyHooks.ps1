param(
  [Parameter(Mandatory=$true)][string]$PluginDirectory,
  [Parameter(Mandatory=$true)][string]$BackupDirectory,
  [string]$UserProfileDirectory = [Environment]::GetFolderPath('UserProfile')
)
$ErrorActionPreference = 'Stop'
$pluginRoot = [IO.Path]::GetFullPath($PluginDirectory).TrimEnd('\','/')
function Is-LegacyHook([object]$entry) {
  if ($entry -isnot [Collections.IDictionary] -or $entry.command -isnot [string]) { return $false }
  $command = $entry.command.Replace('\','/')
  # Parse the executable token, rather than matching a project name or arbitrary command text.
  if ($command -match '^"([^"]+)"(?:\s|$)') { $exe = $Matches[1] }
  elseif ($command -match '^([^\s"]+)(?:\s|$)') { $exe = $Matches[1] }
  else { return $false }
  try { $exe = [IO.Path]::GetFullPath($exe) } catch { return $false }
  $parent = [IO.Path]::GetDirectoryName($exe).TrimEnd('\','/')
  return $parent.Equals($pluginRoot,[StringComparison]::OrdinalIgnoreCase) -and [IO.Path]::GetFileName($exe) -in @('AgentStatusEmit.exe','AgentStatusJsonHook.cmd','AgentStatusBridge.exe')
}
$jobs = @(
  @{Relative='.claude\settings.json'; Kind='nested'},
  @{Relative='.cursor\hooks.json'; Kind='direct'},
  @{Relative='.gemini\settings.json'; Kind='nested'}
)
# Parse and prepare every candidate before changing any configuration.
$changes = @()
foreach ($job in $jobs) {
  $path = Join-Path $UserProfileDirectory $job.Relative
  if (!(Test-Path -LiteralPath $path)) { continue }
  $original = [IO.File]::ReadAllText($path)
  $config = ConvertFrom-Json -InputObject $original -AsHashtable
  if ($config.hooks -isnot [Collections.IDictionary]) { continue }
  $removed = 0
  foreach ($eventName in @($config.hooks.Keys)) {
    $keep = @()
    foreach ($group in @($config.hooks[$eventName])) {
      if ($job.Kind -eq 'direct') {
        if (Is-LegacyHook $group) { $removed++ } else { $keep += ,$group }
      } elseif ($group -is [Collections.IDictionary] -and $group.hooks -is [array]) {
        $remaining = @($group.hooks | Where-Object { if (Is-LegacyHook $_) { $script:removed++; $false } else { $true } })
        if ($remaining.Count -eq $group.hooks.Count) { $keep += ,$group }
        elseif ($remaining.Count -gt 0) { $group.hooks = $remaining; $keep += ,$group }
      } else { $keep += ,$group }
    }
    if ($keep.Count) { $config.hooks[$eventName] = $keep } else { $config.hooks.Remove($eventName) }
  }
  if ($removed -gt 0) { $changes += @{Path=$path;Relative=$job.Relative;Original=$original;Config=$config;Removed=$removed} }
}
foreach ($change in $changes) {
  $backup = Join-Path $BackupDirectory ('hooks\'+$change.Relative)
  New-Item -ItemType Directory -Path (Split-Path -Parent $backup) -Force | Out-Null
  [IO.File]::WriteAllText($backup,$change.Original,[Text.UTF8Encoding]::new($false))
}
foreach ($change in $changes) {
  $temporary = $change.Path+'.tmas-'+[guid]::NewGuid().ToString('N')
  try {
    [IO.File]::WriteAllText($temporary,($change.Config | ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $change.Path -Force
  } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Force } }
  Write-Output ("Removed {0} legacy hooks from {1}" -f $change.Removed,$change.Relative)
}

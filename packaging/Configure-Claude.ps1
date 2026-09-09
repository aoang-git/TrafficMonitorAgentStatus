param(
  [Parameter(Mandatory=$true)][string]$BridgePath,
  [Parameter(Mandatory=$true)][string]$SettingsPath,
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$bridge = (Resolve-Path -LiteralPath $BridgePath).Path
if ($bridge -match '["`$\r\n]') { throw 'The bridge path contains unsupported shell characters.' }
$settings = [IO.Path]::GetFullPath($SettingsPath)
$config = if (Test-Path -LiteralPath $settings) { Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($config -isnot [pscustomobject]) { throw 'The existing settings file is not a JSON object.' }
if (!$config.PSObject.Properties['hooks']) { $config | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) }
if ($config.hooks -isnot [pscustomobject]) { throw 'The existing hooks value is invalid; refusing to overwrite it.' }
# Quote as a native executable invocation accepted by the Agent command shell.
$mode = if ([IO.Path]::GetFileName($bridge) -eq 'AgentStatusEmit.exe') { ' --source claude-code' } else { ' emit --source claude-code' }
$command = '"' + $bridge.Replace('\','/') + '"' + $mode
$events = @('SessionStart','UserPromptSubmit','PreToolUse','PostToolUse','PostToolUseFailure','PermissionRequest','PermissionDenied','Elicitation','ElicitationResult','Stop','SessionEnd')
foreach ($eventName in $events) {
  $existing = @($config.hooks.$eventName | Where-Object { $null -ne $_ })
  $ours = $false
  foreach ($group in $existing) {
    foreach ($hook in @($group.hooks)) { if ($hook.command -eq $command) { $ours = $true } }
  }
  if (!$ours) { $existing += @{ matcher=''; hooks=@(@{type='command'; command=$command; timeout=5}) } }
  $config.hooks | Add-Member -NotePropertyName $eventName -NotePropertyValue $existing -Force
}
$json = ConvertTo-Json -InputObject $config -Depth 100
if (!$Apply) { Write-Output $json; Write-Output 'Preview complete. Use -Apply to write the file.'; exit 0 }
$parent = Split-Path -Parent $settings
New-Item -ItemType Directory -Path $parent -Force | Out-Null
if (Test-Path -LiteralPath $settings) {
  Copy-Item -LiteralPath $settings -Destination ($settings + '.tmas-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
[IO.File]::WriteAllText($settings, $json, [Text.UTF8Encoding]::new($false))
Write-Output ('Hook settings saved: ' + $settings)
Write-Output 'Start a new Agent session for the changes to take effect. Permission rules were preserved.'

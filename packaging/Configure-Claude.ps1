param(
  [Parameter(Mandatory=$true)][string]$BridgePath,
  [Parameter(Mandatory=$true)][string]$SettingsPath,
  [switch]$Apply
)
$ErrorActionPreference = 'Stop'
$bridge = (Resolve-Path -LiteralPath $BridgePath).Path
if ($bridge -match '["`$\r\n]') { throw '路径含有不支持的命令解释字符，请使用普通安装路径。' }
$settings = [IO.Path]::GetFullPath($SettingsPath)
$config = if (Test-Path -LiteralPath $settings) { Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json } else { [pscustomobject]@{} }
if ($config -isnot [pscustomobject]) { throw '原有配置不是 JSON 对象。' }
if (!$config.PSObject.Properties['hooks']) { $config | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) }
if ($config.hooks -isnot [pscustomobject]) { throw '原有 hooks 格式异常，停止以避免覆盖。' }
# Quote as a native executable invocation accepted by the Agent command shell.
$mode = if ([IO.Path]::GetFileName($bridge) -eq 'AgentStatusEmit.exe') { ' --source claude-code' } else { ' emit --source claude-code' }
$command = '"' + $bridge.Replace('\','/') + '"' + $mode
$events = @('SessionStart','UserPromptSubmit','PreToolUse','PostToolUse','PostToolUseFailure','PermissionRequest','Stop','SessionEnd')
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
if (!$Apply) { Write-Output $json; Write-Output '预览完成；加 -Apply 才会写入。'; exit 0 }
$parent = Split-Path -Parent $settings
New-Item -ItemType Directory -Path $parent -Force | Out-Null
if (Test-Path -LiteralPath $settings) {
  Copy-Item -LiteralPath $settings -Destination ($settings + '.tmas-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
}
[IO.File]::WriteAllText($settings, $json, [Text.UTF8Encoding]::new($false))
Write-Output ('已保存 Hook 配置：' + $settings)
Write-Output '需要启动新 Agent 会话使配置生效。授权规则保持原样。'

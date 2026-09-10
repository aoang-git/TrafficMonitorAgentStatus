param(
  [string]$ClaudePath,
  [string]$BuildDirectory = (Join-Path $PSScriptRoot '..\build\x64')
)
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'claude-fixture'
if ([string]::IsNullOrWhiteSpace($ClaudePath)) {
  $claude = Get-Command 'claude.exe' -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
  if (!$claude) { throw 'Claude Code executable was not found. Pass -ClaudePath with the executable path.' }
  $ClaudePath = $claude.Source
}
$ClaudePath = (Resolve-Path -LiteralPath $ClaudePath).Path
$emitter = (Resolve-Path -LiteralPath (Join-Path $BuildDirectory 'AgentStatus\AgentStatusEmit.exe')).Path
$command = '"' + $emitter.Replace('\', '/') + '" --source claude-code'
$hook = [ordered]@{ matcher = ''; hooks = @([ordered]@{ type = 'command'; command = $command; timeout = 5 }) }
$hooks = [ordered]@{}
foreach ($eventName in @('SessionStart','UserPromptSubmit','PreToolUse','PostToolUse','PostToolUseFailure','PermissionRequest','PermissionDenied','Elicitation','ElicitationResult','Stop','SessionEnd')) {
  $hooks[$eventName] = @($hook)
}
$temporarySettings = Join-Path ([IO.Path]::GetTempPath()) ('tmas-claude-smoke-' + [guid]::NewGuid().ToString('N') + '.json')
[IO.File]::WriteAllText($temporarySettings, ([ordered]@{ hooks = $hooks } | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
$info = [Diagnostics.ProcessStartInfo]::new($ClaudePath)
foreach($arg in @('-p','Read probe.txt using the Read tool exactly once. Then reply only TMAS_OK. This is a read-only integration test.','--tools','Read','--allowedTools','Read','--setting-sources','','--settings',$temporarySettings,'--no-session-persistence','--max-budget-usd','0.20','--output-format','json')) { $info.ArgumentList.Add($arg) }
$info.WorkingDirectory=$fixture
$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
$info.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
$info.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
# Reuse only the user's configured provider environment, without copying secrets to disk or loading unrelated hooks.
$userSettings = Join-Path ([Environment]::GetFolderPath('UserProfile')) '.claude\settings.json'
if (Test-Path -LiteralPath $userSettings) {
  $configured = Get-Content -LiteralPath $userSettings -Raw | ConvertFrom-Json
  if ($configured.env) { foreach ($property in $configured.env.PSObject.Properties) {
    if ($property.Name -like 'ANTHROPIC_*' -or $property.Name -in @('API_TIMEOUT_MS','CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC')) { $info.Environment[$property.Name] = [string]$property.Value }
  } }
}
try {
  $p = [Diagnostics.Process]::Start($info)
  $outTask=$p.StandardOutput.ReadToEndAsync();$errTask=$p.StandardError.ReadToEndAsync()
  Write-Output ('Claude smoke PID: '+$p.Id)
  if (!$p.WaitForExit(120000)) { throw ('Claude smoke still running, PID '+$p.Id+'; inspect process before retrying.') }
  $out=$outTask.GetAwaiter().GetResult()
  $err=$errTask.GetAwaiter().GetResult()
  if ($p.ExitCode -ne 0) { throw ('Claude smoke failed: ' + $err.Trim()) }
  $result=$out | ConvertFrom-Json
  [pscustomobject]@{exit=$p.ExitCode;subtype=$result.subtype;is_error=$result.is_error;result=$result.result} | ConvertTo-Json
} finally {
  if (Test-Path -LiteralPath $temporarySettings -PathType Leaf) { [IO.File]::Delete($temporarySettings) }
}

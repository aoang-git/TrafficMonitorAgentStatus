param([string]$ClaudePath = 'C:\Users\renxi\.local\bin\claude.exe')
$ErrorActionPreference = 'Stop'
$fixture = Join-Path $PSScriptRoot 'claude-fixture'
$info = [Diagnostics.ProcessStartInfo]::new($ClaudePath)
foreach($arg in @('-p','Read probe.txt using the Read tool exactly once. Then reply only TMAS_OK. This is a read-only integration test.','--tools','Read','--allowedTools','Read','--setting-sources','','--settings',(Join-Path $fixture 'hook-settings-native.json'),'--no-session-persistence','--max-budget-usd','0.20','--output-format','json')) { $info.ArgumentList.Add($arg) }
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
$p = [Diagnostics.Process]::Start($info)
$outTask=$p.StandardOutput.ReadToEndAsync();$errTask=$p.StandardError.ReadToEndAsync()
Write-Output ('Claude smoke PID: '+$p.Id)
if (!$p.WaitForExit(120000)) { throw ('Claude smoke still running, PID '+$p.Id+'; inspect process before retrying.') }
$out=$outTask.GetAwaiter().GetResult()
$err=$errTask.GetAwaiter().GetResult()
$out | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\docs\claude-smoke-result.json') -Encoding UTF8
$err | Set-Content -LiteralPath (Join-Path $PSScriptRoot '..\docs\claude-smoke-stderr.log') -Encoding UTF8
try { $result=$out | ConvertFrom-Json; [pscustomobject]@{exit=$p.ExitCode;subtype=$result.subtype;is_error=$result.is_error;result=$result.result;cost_usd=$result.total_cost_usd;session=$result.session_id} | ConvertTo-Json } catch { Write-Output ('Result parse failed, exit='+$p.ExitCode) }

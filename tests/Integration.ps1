param([string]$BuildDirectory = (Join-Path $PSScriptRoot '..\build\x64'))
$ErrorActionPreference = 'Stop'
$bridge = (Resolve-Path -LiteralPath (Join-Path $BuildDirectory 'AgentStatus\AgentStatusBridge.exe')).Path
function Run-Bridge([string[]]$Arguments, [string]$InputText='') {
  $launch = $bridge
  if ($Arguments[0] -eq 'emit' -and (Test-Path -LiteralPath (Join-Path (Split-Path $bridge) 'AgentStatusEmit.exe'))) {
    $launch = Join-Path (Split-Path $bridge) 'AgentStatusEmit.exe'
    $Arguments = $Arguments[1..($Arguments.Length-1)]
  }
  $startInfo = [Diagnostics.ProcessStartInfo]::new($launch)
  foreach ($arg in $Arguments) { $startInfo.ArgumentList.Add($arg) }
  $startInfo.UseShellExecute=$false; $startInfo.CreateNoWindow=$true
  $startInfo.RedirectStandardInput=$true; $startInfo.RedirectStandardOutput=$true; $startInfo.RedirectStandardError=$true
  $startInfo.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
  $startInfo.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
  $startInfo.StandardErrorEncoding=[Text.UTF8Encoding]::new($false)
  $process = [Diagnostics.Process]::Start($startInfo)
  try { if ($InputText.Length -gt 0) { $process.StandardInput.Write($InputText) } } catch { if ($Arguments -contains '--strict') { throw } }
  try { $process.StandardInput.Close() } catch { if ($Arguments -contains '--strict') { throw } }
  $outTask = $process.StandardOutput.ReadToEndAsync()
  if (!$process.WaitForExit(5000)) { throw 'Bridge test exceeded deadline' }
  if ($process.ExitCode -ne 0) { throw $process.StandardError.ReadToEnd() }
  if (!$outTask.Wait(1000)) { throw 'Child process retained caller output handle' }
  return $outTask.GetAwaiter().GetResult()
}
Run-Bridge @('start') | Out-Null
$bomSession = 'cursor-bom-' + [Guid]::NewGuid().ToString('N')
$bomJson = @{conversation_id=$bomSession;session_id=$bomSession;generation_id='bom-turn';hook_event_name='beforeSubmitPrompt';cursor_version='integration';prompt='中文'} | ConvertTo-Json -Compress
$bomBytes = [Text.UTF8Encoding]::new($true).GetPreamble() + [Text.UTF8Encoding]::new($false).GetBytes($bomJson)
$bomInfo = [Diagnostics.ProcessStartInfo]::new((Join-Path (Split-Path $bridge) 'AgentStatusEmit.exe'))
foreach ($arg in @('--source','cursor','--strict')) { $bomInfo.ArgumentList.Add($arg) }
$bomInfo.UseShellExecute=$false; $bomInfo.CreateNoWindow=$true
$bomInfo.RedirectStandardInput=$true; $bomInfo.RedirectStandardOutput=$true; $bomInfo.RedirectStandardError=$true
$bomProcess = [Diagnostics.Process]::Start($bomInfo)
$bomProcess.StandardInput.BaseStream.Write($bomBytes, 0, $bomBytes.Length)
$bomProcess.StandardInput.BaseStream.Close()
$bomOutput = $bomProcess.StandardOutput.ReadToEnd()
if (!$bomProcess.WaitForExit(5000)) { throw 'BOM emitter test exceeded deadline' }
if ($bomProcess.ExitCode -ne 0 -or $bomOutput.Trim() -ne '{}') { throw 'UTF-8 BOM hook delivery failed' }
$bomSnapshot = Run-Bridge @('snapshot') | ConvertFrom-Json
if (@($bomSnapshot.sessions | Where-Object session_id -eq $bomSession)[0].state -ne '工作中') { throw 'UTF-8 BOM event was not accepted' }
Run-Bridge @('emit','--source','cursor','--strict') (@{conversation_id=$bomSession;session_id=$bomSession;generation_id='bom-turn';hook_event_name='stop';cursor_version='integration';status='completed'} | ConvertTo-Json -Compress) | Out-Null
$session = 'integration-' + [Guid]::NewGuid().ToString('N')
$samples = [Collections.Generic.List[double]]::new()
function Emit([string]$Type, [string]$Call='') {
  $data = @{schema_version=1; event_id=[Guid]::NewGuid().ToString('N');source='integration';instance_id='test';session_id=$session;event_type=$Type;tool_call_id=$Call;tool_name='Read';project_label='集成测试'} | ConvertTo-Json -Compress
  $timer = [Diagnostics.Stopwatch]::StartNew()
  Run-Bridge @('emit','--source','generic','--strict') $data | Out-Null
  $samples.Add($timer.Elapsed.TotalMilliseconds)
}
Emit 'turn.started'
Emit 'tool.started' 'read1'
Emit 'permission.requested' 'read1'
$snapshot = Run-Bridge @('snapshot') | ConvertFrom-Json
$row = @($snapshot.sessions | Where-Object session_id -eq $session)[0]
if ($row.state -ne '待授权') { throw 'Permission event not visible in snapshot' }
Start-Sleep -Milliseconds 250
& (Join-Path $BuildDirectory 'PluginProbe.exe') (Join-Path $BuildDirectory 'AgentStatus.dll') 10
if ($LASTEXITCODE) { throw 'Plugin ABI integration failed' }
Emit 'permission.resolved' 'read1'
Emit 'tool.finished' 'read1'
Emit 'input.requested' 'question1'
Emit 'input.resolved' 'question1'
Emit 'turn.finished'
$snapshot = Run-Bridge @('snapshot') | ConvertFrom-Json
if (@($snapshot.sessions | Where-Object session_id -eq $session)[0].state -ne '本轮完成') { throw 'Completion not visible' }
for ($i=0; $i -lt 20; $i++) { Emit 'turn.started'; Emit 'tool.started' "tool$i"; Emit 'tool.finished' "tool$i" }
Emit 'session.ended'
Run-Bridge @('emit','--source','generic') '{broken' | Out-Null
Run-Bridge @('emit','--source','generic') ('x' * 70000) | Out-Null
Run-Bridge @('ping') | Out-Null
$sorted = @($samples | Sort-Object)
[pscustomobject]@{result='PASS'; session=$session; events=$samples.Count; emit_process_p95_ms=[math]::Round($sorted[[math]::Ceiling($sorted.Count*0.95)-1],1)} | ConvertTo-Json

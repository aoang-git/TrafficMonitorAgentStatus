param([string]$PackageDirectory)
$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (!$PackageDirectory) {
  [xml]$project = Get-Content -Raw -LiteralPath (Join-Path $workspace 'bridge\AgentStatusBridge.csproj')
  $PackageDirectory = Join-Path $workspace ('dist\TrafficMonitorAgentStatus-v'+$project.Project.PropertyGroup.Version+'-win-x64')
}
$fixture = Join-Path $workspace ('build\install-test-'+[guid]::NewGuid().ToString('N'))
$hostDir = Join-Path $fixture 'TrafficMonitor'
$userDir = Join-Path $fixture 'user'
$dataDir = Join-Path $fixture 'data'
$plugins = Join-Path $hostDir 'plugins'
$agentDir = Join-Path $plugins 'AgentStatus'
New-Item -ItemType Directory -Path $agentDir,$userDir,$dataDir -Force | Out-Null
function Check([bool]$condition,[string]$name) { if (!$condition) { throw "FAIL: $name" }; Write-Output "PASS: $name" }
function Write-Config([string]$relative,[object]$data) {
  $path = Join-Path $userDir $relative
  New-Item -ItemType Directory -Path (Split-Path $path -Parent) -Force | Out-Null
  [IO.File]::WriteAllText($path,($data | ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))
}
try {
  Copy-Item -LiteralPath (Join-Path $workspace 'build\x64\PluginProbe.exe') -Destination (Join-Path $hostDir 'TrafficMonitor.exe')
  [IO.File]::WriteAllText((Join-Path $plugins 'AgentStatus.dll'),'old-plugin')
  [IO.File]::WriteAllText((Join-Path $plugins 'AgentStatus.dll.backup-old'),'old-backup')
  [IO.File]::WriteAllText((Join-Path $plugins 'Unrelated.dll'),'keep-other-plugin')
  [IO.File]::WriteAllText((Join-Path $agentDir 'AgentStatusEmit.exe'),'legacy-emitter')
  [IO.File]::WriteAllText((Join-Path $agentDir 'stale.backup-file'),'legacy-backup')
  [IO.File]::WriteAllText((Join-Path $dataDir 'config.json'),'{"showSource":false,"codexPollIntervalMs":750}')
  $legacy = '"'+(Join-Path $agentDir 'AgentStatusEmit.exe')+'" --source claude-code'
  $wrapper = '"'+(Join-Path $agentDir 'AgentStatusJsonHook.cmd')+'" gemini-cli'
  $other = @{type='command';command='"C:/unrelated/AgentStatusEmit.exe" --source other'}
  Write-Config '.claude\settings.json' @{permissions=@{allow=@('Read')};hooks=@{PreToolUse=@(@{matcher='*';hooks=@(@{type='command';command=$legacy},$other)});Stop=@(@{hooks=@(@{type='command';command=$legacy})})}}
  Write-Config '.cursor\hooks.json' @{version=1;custom='keep';hooks=@{beforeSubmitPrompt=@(@{command=$legacy},$other)}}
  Write-Config '.gemini\settings.json' @{theme='keep';hooks=@{BeforeAgent=@(@{hooks=@(@{type='command';command=$wrapper},$other)})}}
  $claudePath = Join-Path $userDir '.claude\settings.json'
  $originalClaude = [IO.File]::ReadAllText($claudePath)
  $geminiPath = Join-Path $userDir '.gemini\settings.json'
  $originalGemini = [IO.File]::ReadAllText($geminiPath)
  [IO.File]::WriteAllText($geminiPath,'{broken')
  $failed = $false
  try { & (Join-Path $workspace 'tools\Remove-LegacyHooks.ps1') -PluginDirectory $agentDir -BackupDirectory (Join-Path $fixture 'parse-failure-backup') -UserProfileDirectory $userDir } catch { $failed = $true }
  Check ($failed -and [IO.File]::ReadAllText($claudePath) -ceq $originalClaude) 'malformed config aborts before any Hook write'
  [IO.File]::WriteAllText($geminiPath,$originalGemini)
  $params = @{TrafficMonitorDirectory=$hostDir;PackageDirectory=$PackageDirectory;UserProfileDirectory=$userDir;DataDirectory=$dataDir;SkipStartup=$true;SkipLaunch=$true}
  & (Join-Path $workspace 'tools\Install-PluginFiles.ps1') @params
  $claude = [IO.File]::ReadAllText($claudePath) | ConvertFrom-Json -AsHashtable
  Check ($claude.permissions.allow[0] -eq 'Read' -and $claude.hooks.PreToolUse[0].hooks.Count -eq 1 -and $claude.hooks.PreToolUse[0].hooks[0].command -eq $other.command -and !$claude.hooks.Contains('Stop')) 'nested Hook cleanup preserves unrelated commands and permissions'
  $cursor = [IO.File]::ReadAllText((Join-Path $userDir '.cursor\hooks.json')) | ConvertFrom-Json
  Check ($cursor.custom -eq 'keep' -and @($cursor.hooks.beforeSubmitPrompt).Count -eq 1 -and $cursor.hooks.beforeSubmitPrompt[0].command -eq $other.command) 'Cursor direct Hook cleanup is exact'
  $gemini = [IO.File]::ReadAllText($geminiPath) | ConvertFrom-Json
  Check ($gemini.theme -eq 'keep' -and @($gemini.hooks.BeforeAgent[0].hooks).Count -eq 1) 'Gemini wrapper Hook cleanup'
  Check (!(Test-Path -LiteralPath (Join-Path $agentDir 'AgentStatusEmit.exe')) -and !(Test-Path -LiteralPath (Join-Path $agentDir 'stale.backup-file')) -and !(Test-Path -LiteralPath (Join-Path $plugins 'AgentStatus.dll.backup-old'))) 'old binaries and scattered backups removed from active install'
  Check ([IO.File]::ReadAllText((Join-Path $plugins 'Unrelated.dll')) -eq 'keep-other-plugin') 'other plugin untouched'
  $backups = @(Get-ChildItem -LiteralPath (Join-Path $dataDir 'backups') -Directory)
  Check ($backups.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $backups[0].FullName 'hooks\.claude\settings.json')) -and (Test-Path -LiteralPath (Join-Path $backups[0].FullName 'AgentStatus\AgentStatusEmit.exe'))) 'original files and Hooks backed up'
  $config = [IO.File]::ReadAllText((Join-Path $dataDir 'config.json')) | ConvertFrom-Json -AsHashtable
  Check ($config.configVersion -eq 2 -and $config.showSource -and !$config.Contains('codexPollIntervalMs')) 'legacy settings replaced with version 2 defaults'
  Check ($config.displayTexts.Count -eq 6 -and $config.displayTexts.taskInterrupted -eq '任务中断' -and !$config.displayTexts.Contains('turnCompleted')) 'installed settings contain exactly six merged states'
  Check ($config.showReportedStates -eq $true -and !$config.Contains('reportedStateLeaseMs') -and !$config.Contains('runtimeDirectory')) 'fresh install enables reports without lease or manual directory'
  $config.showReportedStates=$false
  $config.reportedStateLeaseMs=15000
  $config.runtimeDirectory="old-directory"
  $config.completionToastMs=7000
  $config.noticeToastMs=19000
  $config.maxWorkingMs=600000
  $config.maxWaitingMs=2400000
  $config.showSource=$false
  $config.displayTexts.taskCompleted='完成啦'
  $config.displayTexts.turnCompleted='旧完成'
  $config.displayTexts.error='旧异常'
  [IO.File]::WriteAllText((Join-Path $dataDir 'config.json'),($config | ConvertTo-Json -Depth 10))
  & (Join-Path $workspace 'tools\Install-PluginFiles.ps1') @params
  $config = [IO.File]::ReadAllText((Join-Path $dataDir 'config.json')) | ConvertFrom-Json
  Check (!$config.showSource -and (Get-ChildItem -LiteralPath $agentDir -Filter '*backup*').Count -eq 0) 'repeat install preserves new settings and stays clean'
  Check ($config.displayTexts.taskCompleted -eq '完成啦' -and $config.displayTexts.taskInterrupted -eq '任务中断' -and @($config.displayTexts.PSObject.Properties).Count -eq 6) 'repeat install prunes retired state keys and preserves surviving custom text'
  Check (!$config.showReportedStates -and !$config.PSObject.Properties['reportedStateLeaseMs'] -and !$config.PSObject.Properties['runtimeDirectory'] -and $config.completionToastMs -eq 7000 -and $config.noticeToastMs -eq 19000 -and $config.maxWorkingMs -eq 600000 -and $config.maxWaitingMs -eq 2400000) 'upgrade removes obsolete discovery and lease settings while preserving four timings'
  $previousConfig = [IO.File]::ReadAllText((Join-Path $dataDir 'config.json')) | ConvertFrom-Json -AsHashtable
  $previousConfig.Remove('showReportedStates')
  [IO.File]::WriteAllText((Join-Path $dataDir 'config.json'),($previousConfig | ConvertTo-Json -Depth 10))
  & (Join-Path $workspace 'tools\Install-PluginFiles.ps1') @params
  $upgradedConfig = [IO.File]::ReadAllText((Join-Path $dataDir 'config.json')) | ConvertFrom-Json
  Check ($upgradedConfig.showReportedStates -and !$upgradedConfig.PSObject.Properties['reportedStateLeaseMs'] -and !$upgradedConfig.showSource -and $upgradedConfig.displayTexts.taskCompleted -eq '完成啦') 'previous version-2 install gains report toggle without restoring lease or resetting display preferences'
  Check ((Get-FileHash -LiteralPath (Join-Path $plugins 'AgentStatus.dll')).Hash -eq (Get-FileHash -LiteralPath (Join-Path $PackageDirectory 'AgentStatus.dll')).Hash) 'installed DLL matches release'
  Check (@(Get-ChildItem -LiteralPath $PackageDirectory -Recurse -File | Where-Object {$_.Name -match 'AgentStatusEmit|JsonHook|Install-AgentHooks|Configure-ClaudeHook'}).Count -eq 0) 'release excludes retired emitters and adapters'
} finally {
  $resolved = [IO.Path]::GetFullPath($fixture)
  if (!$resolved.StartsWith((Join-Path $workspace 'build')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe fixture cleanup path' }
  if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

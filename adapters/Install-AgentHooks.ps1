param(
    [Parameter(Mandatory = $true)]
    [string]$AgentStatusDirectory,
    [string]$UserProfileDirectory = [Environment]::GetFolderPath('UserProfile')
)

$ErrorActionPreference = 'Stop'

function Read-JsonObject([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return [pscustomobject]@{} }
    # ReadAllText decodes UTF-8 correctly (and honours a BOM if present).
    # Get-Content without -Encoding would use the ANSI code page and mangle any
    # non-ASCII content in the user's config before it is written back.
    $text = [IO.File]::ReadAllText($Path)
    if ([string]::IsNullOrWhiteSpace($text)) { return [pscustomobject]@{} }
    return $text | ConvertFrom-Json
}

function Get-OrAddObject($Object, [string]$Name) {
    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        $value = [pscustomobject]@{}
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $value -Force
        return $value
    }
    # A JSON [] deserializes to $null or an empty collection depending on the
    # PowerShell version; treat both like a missing key.
    $existing = @($property.Value)
    if ($existing.Count -eq 0) {
        $value = [pscustomobject]@{}
        $Object | Add-Member -NotePropertyName $Name -NotePropertyValue $value -Force
        return $value
    }
    # Anything else that is not an object cannot be merged into. Failing loudly
    # is required: silently returning a scalar would print success while
    # installing nothing, or abort halfway through the other agents.
    if ($existing.Count -eq 1 -and $existing[0] -is [pscustomobject]) { return $existing[0] }
    throw "The existing '$Name' value is invalid; refusing to overwrite it."
}

function Contains-AgentStatusCommand($Value, [string]$Source) {
    if ($null -eq $Value) { return $false }
    $command = $Value.PSObject.Properties['command']
    if ($null -ne $command) {
        $commandText = "$($command.Value)"
        if ($commandText -like "*AgentStatus*--source $Source*") { return $true }
        if ($commandText -like "*AgentStatusJsonHook.cmd*$Source*") { return $true }
    }
    $children = $Value.PSObject.Properties['hooks']
    if ($null -ne $children) {
        foreach ($child in @($children.Value)) {
            if (Contains-AgentStatusCommand $child $Source) { return $true }
        }
    }
    return $false
}

function Set-HookEvent($Hooks, [string]$EventName, $Entry, [string]$Source) {
    $existingProperty = $Hooks.PSObject.Properties[$EventName]
    $existing = if ($null -eq $existingProperty) { @() } else { @($existingProperty.Value) }
    $kept = @($existing | Where-Object { !(Contains-AgentStatusCommand $_ $Source) })
    $Hooks | Add-Member -NotePropertyName $EventName -NotePropertyValue @($kept + $Entry) -Force
}

function Write-JsonObject([string]$Path, $Value) {
    $directory = Split-Path -Parent $Path
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    if (Test-Path -LiteralPath $Path -PathType Leaf) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss-fff'
        Copy-Item -LiteralPath $Path -Destination "$Path.tmas-backup-$stamp"
    }
    $temporary = "$Path.tmas-$([guid]::NewGuid().ToString('N')).tmp"
    # UTF-8 without a BOM. Set-Content -Encoding UTF8 emits a BOM under Windows
    # PowerShell 5.1, and the JSON readers used by Codex, Gemini and Cursor
    # reject a leading U+FEFF by discarding the entire settings file. Matches
    # the deliberate no-BOM write in adapters/Configure-ClaudeHook.ps1.
    $json = $Value | ConvertTo-Json -Depth 100
    [IO.File]::WriteAllText($temporary, $json, [Text.UTF8Encoding]::new($false))
    Move-Item -LiteralPath $temporary -Destination $Path -Force
}

$agentDirectory = [System.IO.Path]::GetFullPath($AgentStatusDirectory)
$emitter = Join-Path $agentDirectory 'AgentStatusEmit.exe'
$jsonWrapper = Join-Path $agentDirectory 'AgentStatusJsonHook.cmd'
if (!(Test-Path -LiteralPath $emitter -PathType Leaf)) { throw "AgentStatusEmit.exe not found in '$agentDirectory'." }
if (!(Test-Path -LiteralPath $jsonWrapper -PathType Leaf)) { throw "AgentStatusJsonHook.cmd not found in '$agentDirectory'." }

$emitCommand = '"' + $emitter.Replace('\', '/') + '"'
$jsonCommand = '"' + $jsonWrapper.Replace('\', '/') + '"'
$codexCommand = "$jsonCommand codex-cli"
$geminiCommand = "$jsonCommand gemini-cli"

# Codex CLI/TUI: use a separate user hooks.json so existing config.toml and plugin hooks remain untouched.
$codexPath = Join-Path $UserProfileDirectory '.codex\hooks.json'
$codex = Read-JsonObject $codexPath
if ($null -eq $codex.PSObject.Properties['description']) {
    $codex | Add-Member -NotePropertyName description -NotePropertyValue 'TrafficMonitor AgentStatus user hooks.'
}
$codexHooks = Get-OrAddObject $codex 'hooks'
foreach ($eventName in @('SessionStart','SessionEnd','UserPromptSubmit','PreToolUse','PermissionRequest','PostToolUse','Stop','Interrupt')) {
    $handler = [pscustomobject]@{ type = 'command'; command = $codexCommand; commandWindows = $codexCommand; timeout = 3 }
    $group = [pscustomobject]@{ hooks = @($handler) }
    Set-HookEvent $codexHooks $eventName $group 'codex-cli'
}
Write-JsonObject $codexPath $codex

# Gemini CLI: its command hook protocol expects JSON on stdout, so use the wrapper that emits {}.
$geminiPath = Join-Path $UserProfileDirectory '.gemini\settings.json'
$gemini = Read-JsonObject $geminiPath
$geminiConfig = Get-OrAddObject $gemini 'hooksConfig'
if ($null -eq $geminiConfig.PSObject.Properties['enabled']) {
    $geminiConfig | Add-Member -NotePropertyName enabled -NotePropertyValue $true
}
$geminiHooks = Get-OrAddObject $gemini 'hooks'
foreach ($eventName in @('SessionStart','SessionEnd','BeforeAgent','BeforeTool','AfterTool','AfterAgent','Notification')) {
    $handler = [pscustomobject]@{ type = 'command'; name = 'traffic-monitor-agent-status'; command = $geminiCommand; timeout = 3000 }
    $group = [pscustomobject]@{ hooks = @($handler) }
    Set-HookEvent $geminiHooks $eventName $group 'gemini-cli'
}
Write-JsonObject $geminiPath $gemini

# Cursor IDE/Agent: command hooks are direct event arrays rather than matcher groups.
$cursorPath = Join-Path $UserProfileDirectory '.cursor\hooks.json'
$cursor = Read-JsonObject $cursorPath
if ($null -eq $cursor.PSObject.Properties['version']) {
    $cursor | Add-Member -NotePropertyName version -NotePropertyValue 1
}
$cursorHooks = Get-OrAddObject $cursor 'hooks'
foreach ($eventName in @('sessionStart','sessionEnd','beforeSubmitPrompt','preToolUse','postToolUse','postToolUseFailure','stop')) {
    $handler = [pscustomobject]@{ type = 'command'; command = "$emitCommand --source cursor"; timeout = 3 }
    Set-HookEvent $cursorHooks $eventName $handler 'cursor'
}
Write-JsonObject $cursorPath $cursor

Write-Output "Codex CLI hooks: $codexPath"
Write-Output "Gemini CLI hooks: $geminiPath"
Write-Output "Cursor hooks: $cursorPath"

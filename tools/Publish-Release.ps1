param(
  [Parameter(Mandatory=$true)][ValidateSet('development','stable')][string]$Channel,
  [Parameter(Mandatory=$true)][ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')][string]$Repository,
  [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-f]{40}$')][string]$Commit,
  [Parameter(Mandatory=$true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
  [string]$AssetDirectory = (Join-Path $PSScriptRoot '..\dist')
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
function Invoke-Gh([string[]]$Arguments) {
  $output = & gh @Arguments
  if ($LASTEXITCODE) { throw ('GitHub CLI failed: '+($Arguments[0..1] -join ' ')) }
  return $output
}
$name = "TrafficMonitorAgentStatus-v$Version-win-x64.zip"
$archive = (Resolve-Path -LiteralPath (Join-Path $AssetDirectory $name)).Path
$checksum = (Resolve-Path -LiteralPath ($archive+'.sha256')).Path
$checksumText = [IO.File]::ReadAllText($checksum).Trim()
if ($checksumText -notmatch ('^([0-9a-f]{64})  '+[regex]::Escape($name)+'$')) { throw 'Invalid ZIP checksum file.' }
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $Matches[1]) { throw 'ZIP checksum mismatch; refusing to publish.' }
$assets = @($archive,$checksum)
$tag = if ($Channel -eq 'stable') { "v$Version" } else { 'dev' }

if ($Channel -eq 'development') {
  $headCommit = Invoke-Gh @('api',"repos/$Repository/git/ref/heads/main",'--jq','.object.sha')
  if ([string]$headCommit -ne $Commit) { Write-Output 'Skipped an outdated main build.'; return }
} else {
  $tagCommit = Invoke-Gh @('api',"repos/$Repository/commits/$tag",'--jq','.sha')
  if ([string]$tagCommit -ne $Commit) { throw 'Release tag no longer points to the tested commit.' }
}

$existing = & gh release view $tag --repo $Repository --json isDraft,assets 2>$null
$exists = $LASTEXITCODE -eq 0
$release = if ($exists) { $existing | ConvertFrom-Json } else { $null }
if ($Channel -eq 'stable' -and $exists -and !$release.isDraft) {
  throw 'This stable release is already published. Its files will not be overwritten.'
}
$notesPath = $null
try {
  if ($Channel -eq 'development') {
    $notesPath = Join-Path ([IO.Path]::GetTempPath()) ('tmas-release-'+[guid]::NewGuid().ToString('N')+'.md')
    [IO.File]::WriteAllText($notesPath,"开发版：从 main 自动构建，通过全部测试后更新。`n`n基础版本：$Version`n提交：$Commit`n",[Text.UTF8Encoding]::new($false))
    if ($exists) {
      $null = Invoke-Gh (@('release','upload',$tag,'--repo',$Repository,'--clobber')+$assets)
      $null = Invoke-Gh @('api','--method','PATCH',"repos/$Repository/git/refs/tags/dev",'-f',"sha=$Commit",'-F','force=true')
      $null = Invoke-Gh @('release','edit',$tag,'--repo',$Repository,'--prerelease','--latest=false','--draft=false','--title','开发版','--notes-file',$notesPath)
      foreach ($asset in $release.assets) {
        if ($asset.name -match '^TrafficMonitorAgentStatus-v.+-win-x64\.zip(\.sha256)?$' -and $asset.name -notin @($name,($name+'.sha256'))) {
          $null = Invoke-Gh @('release','delete-asset',$tag,$asset.name,'--repo',$Repository,'--yes')
        }
      }
    } else {
      $null = Invoke-Gh (@('release','create',$tag,'--repo',$Repository,'--target',$Commit,'--prerelease','--latest=false','--title','开发版','--notes-file',$notesPath)+$assets)
    }
  } else {
    $releaseNotes = Join-Path $PSScriptRoot "..\docs\releases\$tag.md"
    $notes = @()
    if (Test-Path -LiteralPath $releaseNotes) { $notes = @('--notes-file',(Resolve-Path -LiteralPath $releaseNotes).Path) }
    if ($exists) {
      $null = Invoke-Gh (@('release','upload',$tag,'--repo',$Repository,'--clobber')+$assets)
      $null = Invoke-Gh (@('release','edit',$tag,'--repo',$Repository,'--draft=false','--latest')+$notes)
    } else {
      if ($notes.Count -eq 0) { $notes = @('--generate-notes') }
      $null = Invoke-Gh (@('release','create',$tag,'--repo',$Repository,'--verify-tag','--latest')+$notes+$assets)
    }
  }
  Write-Output "Published $tag from $Commit"
} finally {
  if ($notesPath -and (Test-Path -LiteralPath $notesPath)) { Remove-Item -LiteralPath $notesPath -Force }
}

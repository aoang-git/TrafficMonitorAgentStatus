$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $workspace ('build\release-test-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
$version = '0.2.3'
$name = "TrafficMonitorAgentStatus-v$version-win-x64.zip"
$archive = Join-Path $fixture $name
$checksum = $archive+'.sha256'
$commit = 'a'*40
$passed = 0
function Check([bool]$condition,[string]$label) {
  if (!$condition) { throw "FAIL: $label" }
  $script:passed++; Write-Output "PASS: $label"
}
function Reset-Mock {
  $global:TmasReleaseMock = @{
    Head=$commit; TagCommit=$commit; Exists=$false; Draft=$false; FailUpload=$false
    Assets=@(); Calls=[Collections.Generic.List[object]]::new()
  }
}
function global:gh {
  $call = [string[]]$args
  $global:TmasReleaseMock.Calls.Add($call)
  $global:LASTEXITCODE = 0
  if ($call[0] -eq 'api') {
    if ($call -contains '.object.sha') { return $global:TmasReleaseMock.Head }
    if ($call -contains '.sha') { return $global:TmasReleaseMock.TagCommit }
    return '{}'
  }
  if ($call[1] -eq 'view') {
    if (!$global:TmasReleaseMock.Exists) { $global:LASTEXITCODE=1; return }
    return (@{isDraft=$global:TmasReleaseMock.Draft;assets=$global:TmasReleaseMock.Assets} | ConvertTo-Json -Depth 5)
  }
  if ($call[1] -eq 'upload' -and $global:TmasReleaseMock.FailUpload) { $global:LASTEXITCODE=1 }
}
function Invoke-Publish([string]$channel) {
  & (Join-Path $workspace 'tools\Publish-Release.ps1') -Channel $channel -Repository 'example/project' -Commit $commit -Version $version -AssetDirectory $fixture
}
function Mutations {
  return @($global:TmasReleaseMock.Calls | Where-Object {
    ($_[0] -eq 'release' -and $_[1] -ne 'view') -or ($_ -contains 'PATCH')
  })
}
function Fails([scriptblock]$action,[string]$label) {
  $failed=$false
  try { & $action } catch { $failed=$true }
  Check $failed $label
}
try {
  [IO.File]::WriteAllText($archive,'isolated release payload')
  $hash=(Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
  [IO.File]::WriteAllText($checksum,"$hash  $name`n")
  Reset-Mock; Invoke-Publish development
  $created=@(Mutations)
  Check ($created.Count -eq 1 -and $created[0][1] -eq 'create' -and $created[0] -contains '--prerelease' -and $created[0] -contains $commit -and $created[0] -contains $archive -and $created[0] -contains $checksum) 'main build creates dev release at tested commit with both assets'
  Reset-Mock; $global:TmasReleaseMock.Exists=$true
  $global:TmasReleaseMock.Assets=@(@{name='TrafficMonitorAgentStatus-v0.2.2-win-x64.zip'},@{name=$name},@{name=($name+'.sha256')},@{name='unrelated.txt'})
  Invoke-Publish development
  $deleted=@($global:TmasReleaseMock.Calls | Where-Object {$_[1] -eq 'delete-asset'})
  Check ($deleted.Count -eq 1 -and $deleted[0][3] -eq 'TrafficMonitorAgentStatus-v0.2.2-win-x64.zip') 'dev update removes only obsolete owned assets and retains current checksum'
  Check (@($global:TmasReleaseMock.Calls | Where-Object {$_ -contains "sha=$commit" -and $_ -contains 'force=true'}).Count -eq 1) 'dev tag follows tested commit'
  Reset-Mock; $global:TmasReleaseMock.Head='b'*40; Invoke-Publish development
  Check (@(Mutations).Count -eq 0) 'superseded main build performs no release writes'
  Reset-Mock; Invoke-Publish stable
  $created=@(Mutations)
  Check ($created.Count -eq 1 -and $created[0][2] -eq "v$version" -and $created[0] -contains '--verify-tag' -and $created[0] -contains '--latest' -and $created[0] -notcontains '--prerelease') 'matching version tag creates stable release'
  Reset-Mock; $global:TmasReleaseMock.TagCommit='b'*40
  Fails { Invoke-Publish stable } 'moved version tag is rejected'
  Check (@(Mutations).Count -eq 0) 'tag mismatch cannot publish assets'
  Reset-Mock; $global:TmasReleaseMock.Exists=$true
  Fails { Invoke-Publish stable } 'published stable release is never overwritten'
  Check (@(Mutations).Count -eq 0) 'stable retry performs no mutations after publication'
  Reset-Mock; $global:TmasReleaseMock.Exists=$true; $global:TmasReleaseMock.Draft=$true
  Invoke-Publish stable
  Check (@($global:TmasReleaseMock.Calls | Where-Object {$_[1] -eq 'edit' -and $_ -contains '--draft=false'}).Count -eq 1) 'interrupted draft can finish after uploading verified assets'
  $nextName='TrafficMonitorAgentStatus-v9.9.9-win-x64.zip'
  $nextArchive=Join-Path $fixture $nextName
  Copy-Item -LiteralPath $archive -Destination $nextArchive
  [IO.File]::WriteAllText(($nextArchive+'.sha256'),"$hash  $nextName`n")
  Reset-Mock; $global:TmasReleaseMock.Exists=$true; $global:TmasReleaseMock.Draft=$true
  & (Join-Path $workspace 'tools\Publish-Release.ps1') -Channel stable -Repository 'example/project' -Commit $commit -Version '9.9.9' -AssetDirectory $fixture
  $edits=@($global:TmasReleaseMock.Calls | Where-Object {$_[1] -eq 'edit'})
  Check ($edits.Count -eq 1 -and $edits[0] -notcontains '--generate-notes' -and @($edits[0] | Where-Object {[string]::IsNullOrEmpty($_)}).Count -eq 0) 'draft retry without authored notes preserves existing notes using supported nonempty arguments'
  Reset-Mock
  & (Join-Path $workspace 'tools\Publish-Release.ps1') -Channel stable -Repository 'example/project' -Commit $commit -Version '9.9.9' -AssetDirectory $fixture
  Check (@($global:TmasReleaseMock.Calls | Where-Object {$_[1] -eq 'create' -and $_ -contains '--generate-notes'}).Count -eq 1) 'new versions without authored notes use GitHub generated notes'
  Reset-Mock; $global:TmasReleaseMock.Exists=$true; $global:TmasReleaseMock.FailUpload=$true
  Fails { Invoke-Publish development } 'asset upload failure aborts publication'
  Check (@($global:TmasReleaseMock.Calls | Where-Object {$_ -contains 'PATCH' -or $_[1] -eq 'edit' -or $_[1] -eq 'delete-asset'}).Count -eq 0) 'failed upload cannot advance tag or remove old assets'
  Reset-Mock; [IO.File]::AppendAllText($archive,'changed')
  Fails { Invoke-Publish stable } 'checksum mismatch blocks release'
  Check ($global:TmasReleaseMock.Calls.Count -eq 0) 'invalid package never contacts GitHub'
  Write-Output "PASS: $passed release checks"
} finally {
  Remove-Item Function:\gh
  Remove-Variable TmasReleaseMock -Scope Global -ErrorAction SilentlyContinue
  $resolved=[IO.Path]::GetFullPath($fixture)
  if (!$resolved.StartsWith($workspace+'\build\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe release fixture cleanup' }
  if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}

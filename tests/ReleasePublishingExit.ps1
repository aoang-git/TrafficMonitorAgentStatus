$ErrorActionPreference = 'Stop'
$workspace = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixture = Join-Path $workspace ('build\release-exit-test-'+[guid]::NewGuid().ToString('N'))
$pwsh = (Get-Command pwsh -ErrorAction Stop).Source
function Invoke-Child([string[]]$Arguments,[string]$Directory) {
  $info = [Diagnostics.ProcessStartInfo]::new($pwsh)
  $info.UseShellExecute=$false; $info.CreateNoWindow=$true
  $info.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
  $info.RedirectStandardOutput=$true; $info.RedirectStandardError=$true
  $info.WorkingDirectory=$Directory
  foreach ($argument in @('-NoProfile','-NonInteractive','-ExecutionPolicy','Bypass')+$Arguments) { $info.ArgumentList.Add($argument) }
  $process=[Diagnostics.Process]::Start($info)
  try {
    $output=$process.StandardOutput.ReadToEndAsync(); $errors=$process.StandardError.ReadToEndAsync()
    if (!$process.WaitForExit(30000)) { $process.Kill(); throw 'Release test child timed out.' }
    return @{Exit=$process.ExitCode;Output=$output.GetAwaiter().GetResult();Error=$errors.GetAwaiter().GetResult()}
  } finally { $process.Dispose() }
}
function Check-Exit($Result,[int]$Expected,[string]$Label) {
  if ($Result.Exit -ne $Expected) { throw "$Label expected exit $Expected, got $($Result.Exit).`n$($Result.Output)`n$($Result.Error)" }
  Write-Output "PASS: $Label returns $Expected"
}
$actionsWrapper=@'
$ErrorActionPreference = 'Stop'
& './tests/ReleasePublishing.ps1'
if (Test-Path -LiteralPath variable:\LASTEXITCODE) { exit $LASTEXITCODE }
'@
try {
  $fileResult=Invoke-Child @('-File','./tests/ReleasePublishing.ps1') $workspace
  Check-Exit $fileResult 0 'release tests via pwsh -File'
  Write-Output $fileResult.Output.TrimEnd()
  Check-Exit (Invoke-Child @('-Command',$actionsWrapper) $workspace) 0 'release tests via Actions pwsh -Command wrapper'

  # A separate fake workspace verifies real assertion failures still fail the job.
  New-Item -ItemType Directory -Path (Join-Path $fixture 'tests'),(Join-Path $fixture 'tools') -Force | Out-Null
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ReleasePublishing.ps1') -Destination (Join-Path $fixture 'tests\ReleasePublishing.ps1')
  [IO.File]::WriteAllText((Join-Path $fixture 'tools\Publish-Release.ps1'),"throw 'Intentional publisher failure for exit-code regression.'",[Text.UTF8Encoding]::new($false))
  Check-Exit (Invoke-Child @('-File','./tests/ReleasePublishing.ps1') $fixture) 1 'real failure via pwsh -File'
  Check-Exit (Invoke-Child @('-Command',$actionsWrapper) $fixture) 1 'real failure via Actions pwsh -Command wrapper'
  Write-Output 'PASS: 4 release exit-code checks'
} finally {
  $resolved=[IO.Path]::GetFullPath($fixture)
  if (!$resolved.StartsWith($workspace+'\build\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe exit-test fixture cleanup' }
  if (Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
exit 0

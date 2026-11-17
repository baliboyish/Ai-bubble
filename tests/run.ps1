[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$output=Join-Path $root "artifacts/$Configuration"
if(!(Test-Path -LiteralPath (Join-Path $output 'AIBubbleWin.exe'))){throw 'Build the app first.'}
# Execute in a fresh Windows PowerShell STA process; tests never initialize website sessions.
$compiler=Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:exe /platform:x64 "/out:$output/BehaviorChecks.exe" /r:System.Windows.Forms.dll /r:System.Drawing.dll (Join-Path $PSScriptRoot 'BehaviorChecks.cs')
if($LASTEXITCODE -ne 0){throw 'Behavior test compilation failed.'}
$process=Start-Process -FilePath (Join-Path $output 'BehaviorChecks.exe') -WorkingDirectory $output -WindowStyle Hidden -PassThru
if(!$process.WaitForExit(45000)){$process.Kill();throw 'Behavior checks timed out.'}
$results=Get-Content -LiteralPath (Join-Path $output 'test-results.txt') -Raw
if($process.ExitCode -ne 0 -or $results -notmatch '^PASS 40 behavioral assertions'){throw $results}
Write-Output $results.Trim()

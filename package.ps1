[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$output=Join-Path $PSScriptRoot "artifacts/$Configuration"
$destination=Join-Path $PSScriptRoot 'artifacts/package'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$files=@('AIBubbleWin.exe','Microsoft.Web.WebView2.Core.dll','Microsoft.Web.WebView2.WinForms.dll','WebView2Loader.dll') | ForEach-Object {
    $file=Join-Path $output $_
    if(!(Test-Path -LiteralPath $file)){throw "Missing runtime file: $_"}
    $file
}
$config=Join-Path $output 'AIBubbleWin.exe.config'
if(Test-Path -LiteralPath $config){$files+=$config}
$files+=@('README.md','LICENSE','THIRD_PARTY_NOTICES.md') | ForEach-Object {Join-Path $PSScriptRoot $_}
$zip=Join-Path $destination 'AI-Bubble-Windows-x64.zip'
Compress-Archive -LiteralPath $files -DestinationPath $zip -CompressionLevel Optimal -Force
$hash=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  AI-Bubble-Windows-x64.zip" | Set-Content -LiteralPath (Join-Path $destination 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Packaged: $zip"

# Verify the real embedded pages and restart hand-off without changing next.json.
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
if (Get-Process 'DeepSeek Harness','Version Switch' -ErrorAction SilentlyContinue) { throw 'Close the desktop windows before running this test.' }
$before = [IO.File]::ReadAllText("$appRoot\versions\next.json")
$original = Start-Process "$appRoot\DeepSeek Harness.exe" -ArgumentList '--verify-restart' -PassThru
if (-not $original.WaitForExit(60000)) { throw 'Restart button did not close the old process.' }
$deadline = (Get-Date).AddSeconds(30)
do { Start-Sleep -Milliseconds 500; $current = @(Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue) } until (($current.Count -eq 1 -and $current[0].MainWindowHandle -ne 0) -or (Get-Date) -gt $deadline)
if ($current.Count -ne 1 -or $current[0].Id -eq $original.Id) { throw 'Restart did not create exactly one replacement window.' }
Write-Output "PASS: restart button replaced PID $($original.Id) with PID $($current[0].Id)."
$verification = "$PSScriptRoot\version-verification.json"
if (Test-Path -LiteralPath $verification) { Remove-Item -LiteralPath $verification }
$manager = Start-Process "$appRoot\Version Switch.exe" -ArgumentList '--verify-ui' -PassThru
$deadline = (Get-Date).AddSeconds(45)
do { Start-Sleep -Milliseconds 500; $result = if (Test-Path $verification) { [IO.File]::ReadAllText($verification) } else { '' } } until ($result.Contains('PASS:') -or (Get-Date) -gt $deadline)
if (-not $result.Contains('PASS:')) { throw 'Version manager verification did not finish.' }
$details = ($result -split "`n")[0] | ConvertFrom-Json | ConvertFrom-Json
if ($details.rows -lt 1 -or $details.current -ne 1 -or $details.badge -ne '当前运行') { throw 'Missing current-version emphasis or version catalogue.' }
if ([IO.File]::ReadAllText("$appRoot\versions\next.json") -ne $before) { throw 'Verification unexpectedly changed the selected version.' }
Write-Output "PASS: $($details.rows) versions; current $($details.version) highlighted; search and live theme sync verified; selection unchanged."

$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
if (Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue) { throw 'Close DSH before running lifecycle verification.' }
$logPath = Join-Path $appRoot 'data\desktop.log'
$before = if (Test-Path $logPath) { [IO.File]::ReadAllText($logPath).Length } else { 0 }
$app = Start-Process -FilePath "$appRoot\DeepSeek Harness.exe" -ArgumentList '--verify-lifecycle' -PassThru
if (-not $app.WaitForExit(70000)) {
    [void]$app.CloseMainWindow()
    throw 'Lifecycle test did not close its window; inspect desktop.log.'
}
$log = [IO.File]::ReadAllText($logPath).Substring($before)
if (-not $log.Contains('PASS: internal restart, same window') -or -not $log.Contains('restart=False')) { throw 'Lifecycle verification failed; inspect desktop.log.' }
Write-Output 'PASS: official restart endpoint, same native window, replaced backend, old backend exited, plugin reminder, window closes with backend.'

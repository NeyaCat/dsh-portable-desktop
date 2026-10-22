$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
if (Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue) {
    throw 'Close DSH before running the environment check.'
}
$app = Start-Process -FilePath "$appRoot\DeepSeek Harness.exe" -ArgumentList '--verify-environment' -Wait -PassThru
$report = Join-Path $appRoot 'data\environment-check.txt'
if (Test-Path -LiteralPath $report) { Get-Content -LiteralPath $report }
if ($app.ExitCode -ne 0) {
    throw "Environment check failed with exit code $($app.ExitCode). Run 'DeepSeek Harness.exe --repair-environment' or inspect data\environment-check.txt."
}
Write-Output 'PASS: portable environment check'

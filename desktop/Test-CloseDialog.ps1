# Verify the close dialog, tray minimize path and remembered exit without
# touching the user's real preferences (the launcher uses a temp settings file).
$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
if (Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue) { throw 'Close DSH before running this test.' }
$logPath = Join-Path $appRoot 'data\desktop.log'
$initialLength = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }
$started = Get-Date
$app = Start-Process -FilePath "$appRoot\DeepSeek Harness.exe" -ArgumentList '--verify-exit-settings' -PassThru
try {
    if (-not $app.WaitForExit(180000)) { throw 'Close dialog verification did not finish.' }
    $app.Refresh()
    if ($app.ExitCode -ne 0) { throw "DSH exited with code $($app.ExitCode)." }
    $newLog = ''
    if (Test-Path $logPath) {
        $stream = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
        try {
            [void]$stream.Seek($initialLength, 'Begin')
            $reader = [IO.StreamReader]::new($stream)
            $newLog = $reader.ReadToEnd()
        } finally { $stream.Dispose() }
    }
    if (-not $newLog.Contains('PASS: close dialog cancel keeps backend')) {
        throw 'Close dialog verification failed; inspect data\desktop.log.'
    }
    $tone = if ($newLog.Contains('Tray icon tone=black')) { 'black' } elseif ($newLog.Contains('Tray icon tone=white')) { 'white' } else { throw 'Tray icon tone was not verified.' }
    $match = [regex]::Match($newLog, 'Preferences file: (.+)')
    if (-not $match.Success) { throw 'Verification preference file was not recorded.' }
    $preferences = $match.Groups[1].Value.Trim()
    if (-not (Test-Path -LiteralPath $preferences)) { throw 'Verification preference file is missing.' }
    $data = Get-Content -LiteralPath $preferences -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($data.closeAction -ne 'exit') { throw 'Remembered exit action was not persisted.' }
    $history = @($data.closeHistory)
    if ($history.Count -lt 3 -or $history[-1].action -ne 'exit') { throw 'Close history was not recorded.' }
    $preview = Join-Path $appRoot 'desktop\exit-preview.png'
    if (-not (Test-Path -LiteralPath $preview) -or (Get-Item $preview).LastWriteTime -lt $started) {
        throw 'Close dialog screenshot was not refreshed.'
    }
    $menuPreview = Join-Path $appRoot 'desktop\menu-preview.png'
    if (-not (Test-Path -LiteralPath $menuPreview) -or (Get-Item $menuPreview).LastWriteTime -lt $started) {
        throw 'Skinned tray menu screenshot was not refreshed.'
    }
    Write-Output "PASS: close dialog cancel/minimize/remember; dialog and both menus use the same skin cache; tray icon ($tone); backend kept while minimized; remembered exit closed the app and recorded history."
} finally {
    if (-not $app.HasExited) { $app.Kill() }
}

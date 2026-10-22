$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DesktopTestNative {
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int command);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
}
'@
if (Get-Process 'DeepSeek Harness' -ErrorAction SilentlyContinue) { throw 'Close DSH before running this test.' }
$logPath = Join-Path $appRoot 'data\desktop.log'
$initialLength = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }
$first = Start-Process -FilePath "$appRoot\DeepSeek Harness.exe" -ArgumentList '--verify-ui' -PassThru
try {
    $deadline = (Get-Date).AddSeconds(90)
    $newLog = ''
    do {
        Start-Sleep -Milliseconds 500
        $first.Refresh()
        if ($first.HasExited) { throw "DSH exited: $($first.ExitCode)" }
        if (Test-Path $logPath) {
            $stream = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
            try {
                [void]$stream.Seek($initialLength, 'Begin')
                $reader = [IO.StreamReader]::new($stream)
                $newLog = $reader.ReadToEnd()
            } finally { $stream.Dispose() }
        }
    } until ($newLog.Contains('Chrome verified') -or $newLog.Contains('Chrome verification failed') -or (Get-Date) -gt $deadline)
    if (-not $newLog.Contains('UI ready')) { throw 'UI did not finish loading.' }
    if (-not $newLog.Contains('Chrome verified')) { throw 'Transparent chrome verification did not finish.' }
    if (-not $newLog.Contains('rgba(0, 0, 0, 0)')) { throw 'Title bar is not transparent.' }
    $backendId = [int]([regex]::Match($newLog, 'Backend started PID=(\d+)').Groups[1].Value)
    $first.Refresh()
    $window = $first.MainWindowHandle
    if ($window -eq 0) { throw 'Missing desktop window.' }
    [void][DesktopTestNative]::ShowWindow($window, 6)
    if (-not [DesktopTestNative]::IsIconic($window)) { throw 'Minimize failed.' }
    $second = Start-Process -FilePath "$appRoot\DeepSeek Harness.exe" -PassThru
    if (-not $second.WaitForExit(10000)) { throw 'Second launch did not exit.' }
    Start-Sleep -Seconds 1
    if (@(Get-Process 'DeepSeek Harness').Count -ne 1) { throw 'More than one DSH process.' }
    if ([DesktopTestNative]::IsIconic($window)) { throw 'Existing window was not restored.' }
    if ([DesktopTestNative]::GetForegroundWindow() -ne $window) { throw 'Existing window was not activated.' }
    Write-Output 'PASS: UI loaded; duplicate launch exited; same window restored and activated.'
    [void]$first.CloseMainWindow()
    if (-not $first.WaitForExit(10000)) { throw 'Window did not close.' }
    Start-Sleep -Seconds 1
    if (Get-Process -Id $backendId -ErrorAction SilentlyContinue) { throw 'Backend survived window close.' }
    Write-Output 'PASS: closing the window stopped its backend.'
} finally {
    if (-not $first.HasExited) { [void]$first.CloseMainWindow() }
}

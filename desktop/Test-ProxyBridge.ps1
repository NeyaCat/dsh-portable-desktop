$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $appRoot 'DeepSeek Harness.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Missing $exe; run desktop\Build.ps1 first." }
$logPath = Join-Path $appRoot 'data\desktop.log'
$initialLength = if (Test-Path $logPath) { (Get-Item $logPath).Length } else { 0 }

# The self-test returns before the window and the single-instance mutex, so this
# can run while the desktop is open (unlike Test-Desktop.ps1).
$info = New-Object System.Diagnostics.ProcessStartInfo($exe, '--verify-proxy-bridge')
$info.UseShellExecute = $false
$info.WorkingDirectory = $appRoot
$process = [System.Diagnostics.Process]::Start($info)
if (-not $process.WaitForExit(120000)) {
    $process.Kill()
    throw 'Proxy bridge self-test did not finish within 120 s.'
}
$exitCode = $process.ExitCode

$newLog = ''
if (Test-Path $logPath) {
    $stream = [IO.File]::Open($logPath, 'Open', 'Read', 'ReadWrite')
    try {
        [void]$stream.Seek($initialLength, 'Begin')
        $reader = [IO.StreamReader]::new($stream)
        $newLog = $reader.ReadToEnd()
    } finally { $stream.Dispose() }
}

$failed = @($newLog -split "`n" | Where-Object { $_ -match 'ProxyBridge verify FAIL' })
if ($failed.Count -gt 0) { throw ("Proxy bridge checks failed: " + ($failed -join '; ')) }
if ($newLog -notmatch 'Proxy bridge verified') { throw 'Proxy bridge self-test reported no verdict; see data\desktop.log.' }
if ($exitCode -ne 0) { throw "Proxy bridge self-test exited $exitCode." }
$passes = @($newLog -split "`n" | Where-Object { $_ -match 'ProxyBridge verify PASS' }).Count
Write-Output "PASS: proxy bridge self-test, $passes checks, exit 0."

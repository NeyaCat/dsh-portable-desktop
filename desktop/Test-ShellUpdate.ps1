#Requires -Version 5.1
# Offline integration test against real shell binaries and an isolated installation.
$ErrorActionPreference = 'Stop'
$qa = Join-Path ([IO.Path]::GetTempPath()) ('dsh-shell-qa-' + [Guid]::NewGuid().ToString('N'))
$package = Join-Path $qa 'package'
$root = Join-Path $qa 'installed'
$session = Join-Path $root ('.shell-update-' + [Guid]::NewGuid().ToString('N'))
$incoming = Join-Path $session 'incoming'
$journal = Join-Path $root '.shell-update-journal'
& "$PSScriptRoot/Build.ps1" -AppRoot $package -Version '1.2.3-rc.2'
[void][IO.Directory]::CreateDirectory((Join-Path $package 'desktop'))
Copy-Item "$PSScriptRoot/update-worker.cjs" (Join-Path $package 'desktop')
$zip = Join-Path $qa 'shell.zip'
& "$PSScriptRoot/../packaging/build-shell-update.ps1" -Package $package -OutputZip $zip
$assembly = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes((Join-Path $package 'DeepSeek Harness.exe')))
$unpack = $assembly.GetType('ShellUpdateDialog').GetMethod('Unpack', [Reflection.BindingFlags]'NonPublic,Static')
$digest = 'sha256:' + (Get-FileHash -LiteralPath $zip).Hash
$badDigestRejected = $false
try { $unpack.Invoke($null, [string[]]@($zip,$incoming,'sha256:0','1.2.3-rc.2')) }
catch { $badDigestRejected = $true }
if (!$badDigestRejected -or (Test-Path $incoming)) { throw 'Corrupt download accepted' }
$unpack.Invoke($null, [string[]]@($zip,$incoming,$digest,'1.2.3-rc.2'))
. "$PSScriptRoot/Apply-ShellUpdate.ps1"
$original = @('DeepSeek Harness.exe','Version Switch.exe','workspace/project.txt',
    'data/profiles/web/node_modules/plugin/index.js','environments/state.json','node_modules/@deepseek-ai/dsh/package.json')
foreach ($name in $original) {
    $file = Join-Path $root $name
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $file))
    [IO.File]::WriteAllText($file, $name)
}
Install-ShellFiles $root $session $journal
if ([Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $root 'DeepSeek Harness.exe')).ProductVersion -ne '1.2.3-rc.2') {
    throw 'New executable was not installed'
}
foreach ($name in $original[2..5]) {
    if ([IO.File]::ReadAllText((Join-Path $root $name)) -ne $name) { throw "Changed user/runtime file: $name" }
}
# Simulate a process interruption after replacements and before the final commit.
Restore-ShellFiles $root $session
Restore-ShellFiles $root $session
foreach ($name in $original) {
    if ([IO.File]::ReadAllText((Join-Path $root $name)) -ne $name) { throw "Failed rollback: $name" }
}
if (Test-Path (Join-Path $root 'desktop/update-worker.cjs')) { throw 'Rollback left a newly introduced file' }
foreach ($name in @('data/profiles/web/plugin.cjs','desktop/../workspace/x.cjs','../outside.exe')) {
    $rejected = $false
    try { [void](Get-ShellTarget $root $name) } catch { $rejected = $true }
    if (!$rejected) { throw "Unexpected update target: $name" }
}
Write-Output 'PASS shell package, SHA-256 rejection, installation, data preservation, interrupted-update rollback and destination restrictions'

# A tiny stand-in records the real updater's relaunch without starting DSH.
$handoff = Join-Path $qa 'handoff'
$id = '.shell-update-' + [Guid]::NewGuid().ToString('N')
$next = Join-Path $handoff "$id/incoming"
[void][IO.Directory]::CreateDirectory($next)
$probe = Join-Path $qa 'Probe.cs'
[IO.File]::WriteAllText($probe, 'using System.IO; class Probe { static void Main() { File.WriteAllText("restarted.ok","ok"); } }')
$compiler = Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
& $compiler /nologo /target:winexe "/out:$next/DeepSeek Harness.exe" $probe
if ($LASTEXITCODE -ne 0) { throw 'Restart probe compilation failed' }
Copy-Item (Join-Path $next 'DeepSeek Harness.exe') (Join-Path $next 'Version Switch.exe')
$env:DSH_QA_ASSEMBLY = Join-Path $package 'DeepSeek Harness.exe'
$env:DSH_QA_ROOT = $handoff
$env:DSH_QA_SESSION = $id
$launcher = @'
$ErrorActionPreference = 'Stop'
$asm = [Reflection.Assembly]::Load([IO.File]::ReadAllBytes($env:DSH_QA_ASSEMBLY))
$type = $asm.GetType('ShellMaintenance')
$flags = [Reflection.BindingFlags]'NonPublic,Static'
$lease = $type.GetMethod('Enter',$flags).Invoke($null,[string[]]@($env:DSH_QA_ROOT))
try {
    $parameters = [object[]]@($env:DSH_QA_ROOT,$env:DSH_QA_SESSION,$false)
    $type.GetMethod('StartHelper',$flags).Invoke($null,$parameters)
} finally { $lease.Dispose() }
'@
& powershell.exe -NoProfile -NonInteractive -EncodedCommand ([Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($launcher)))
if ($LASTEXITCODE -ne 0) { throw 'Updater handoff failed' }
$deadline = [DateTime]::UtcNow.AddSeconds(20)
while (!(Test-Path (Join-Path $handoff 'restarted.ok')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
if (!(Test-Path (Join-Path $handoff 'restarted.ok')) -or (Test-Path (Join-Path $handoff $id))) { throw 'Restart/cleanup failed' }
Write-Output 'PASS parent-exit handoff, exclusive installation, temporary directory cleanup and relaunch'

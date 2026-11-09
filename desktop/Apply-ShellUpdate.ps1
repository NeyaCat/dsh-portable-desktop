$ErrorActionPreference = 'Stop'

function Get-ShellTarget($Base, [string]$Name) {
    if ($Name -cnotmatch '^(DeepSeek Harness\.exe|Version Switch\.exe|Microsoft\.Web\.WebView2\.(Core|WinForms)\.dll|WebView2Loader\.dll|desktop/[a-zA-Z0-9_-]+\.cjs|assets/[a-zA-Z0-9_-]+\.(ico|svg))$') {
        throw "更新包包含无效文件：$Name"
    }
    $target = Join-Path $Base $Name
    foreach ($item in @((Split-Path -Parent $target), $target)) {
        if ([IO.File]::Exists($item) -or [IO.Directory]::Exists($item)) {
            if (([IO.File]::GetAttributes($item) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "更新路径已重定向：$item"
            }
        }
    }
    return $target
}

function Restore-ShellFiles($Root, $Session) {
    $plan = Get-Content -LiteralPath (Join-Path $Session 'files.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($item in $plan) {
        $target = Get-ShellTarget $Root $item.name
        $backup = Get-ShellTarget (Join-Path $Session 'backup') $item.name
        if ([IO.File]::Exists($backup)) {
            # Retain the backup until every file is restored so recovery is retryable.
            $restored = Join-Path $Session 'restore.tmp'
            [IO.File]::Copy($backup, $restored, $true)
            if ([IO.File]::Exists($target)) {
                # PS 5.1 converts a null string argument to an empty path. Use
                # a unique discard file as File.Replace's third argument.
                [IO.File]::Replace($restored, $target, (Join-Path $Session ([Guid]::NewGuid().ToString('N'))))
            } else {
                [IO.File]::Move($restored, $target)
            }
        } elseif (!$item.existed -and [IO.File]::Exists($target)) {
            [IO.File]::Delete($target)
        }
    }
}

function Install-ShellFiles($Root, $Session, $Journal) {
    $incoming = Join-Path $Session 'incoming'
    $plan = @(foreach ($file in [IO.Directory]::EnumerateFiles($incoming, '*', 'AllDirectories')) {
        $name = $file.Substring($incoming.Length + 1).Replace('\','/')
        $target = Get-ShellTarget $Root $name
        @{ name = $name; existed = [IO.File]::Exists($target) }
    })
    if ($plan.name -notcontains 'DeepSeek Harness.exe' -or $plan.name -notcontains 'Version Switch.exe') { throw '桌面更新包不完整' }
    [IO.File]::WriteAllText((Join-Path $Session 'files.json'), (ConvertTo-Json -InputObject $plan))
    $marker = Join-Path $Session 'journal.tmp'
    [IO.File]::WriteAllText($marker, (Split-Path -Leaf $Session))
    [IO.File]::Move($marker, $Journal)
    foreach ($item in $plan) {
        $target = Get-ShellTarget $Root $item.name
        $source = Get-ShellTarget $incoming $item.name
        if ($item.existed) {
            $backup = Get-ShellTarget (Join-Path $Session 'backup') $item.name
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $backup))
            [IO.File]::Replace($source, $target, $backup)
        } else {
            [void][IO.Directory]::CreateDirectory((Split-Path -Parent $target))
            [IO.File]::Move($source, $target)
        }
    }
}

function Invoke-ShellUpdate {
    $root = (Get-Location).ProviderPath
    $journal = Join-Path $root '.shell-update-journal'
    $recover = $env:DSH_SHELL_RECOVER -eq '1'
    $lease = $null
    $session = $null
    $restart = $true
    try {
        $parent = Get-Process -Id ([int]$env:DSH_SHELL_PARENT) -ErrorAction SilentlyContinue
        if ($parent -and !$parent.WaitForExit(30000)) { throw '桌面程序尚未退出，请重试。' }
        if ($recover -and ![IO.File]::Exists($journal)) { $restart = $false; return }
        $id = if ($recover) { [IO.File]::ReadAllText($journal) } else { $env:DSH_SHELL_SESSION }
        if ($id -cnotmatch '^\.shell-update-[0-9a-f]{32}$') { throw '无效的桌面更新目录' }
        $candidate = Join-Path $root $id
        if (([IO.File]::GetAttributes($candidate) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw '更新目录已重定向' }
        $session = $candidate
        $lease = [IO.File]::Open((Join-Path $root '.shell-update.lock'), 'Open', 'Read', 'None')
        if ($recover) {
            Restore-ShellFiles $root $session
        } else {
            try { Install-ShellFiles $root $session $journal }
            catch {
                if ([IO.File]::Exists($journal) -and [IO.File]::ReadAllText($journal) -eq $id) {
                    Restore-ShellFiles $root $session
                    [IO.File]::Delete($journal)
                }
                throw
            }
        }
        [IO.File]::Delete($journal)
    } catch {
        Add-Type -AssemblyName System.Windows.Forms
        [void][Windows.Forms.MessageBox]::Show(("桌面软件更新未完成。请关闭其他桌面或 DSH 版本切换窗口后重试。" + [Environment]::NewLine + $_.Exception.Message), '桌面软件更新')
    } finally {
        if (![IO.File]::Exists($journal) -and $session -and [IO.Directory]::Exists($session)) {
            # Only the validated, uniquely named directory directly under this installation.
            if ((Split-Path -Parent $session) -eq $root -and (Split-Path -Leaf $session) -cmatch '^\.shell-update-[0-9a-f]{32}$') {
                Remove-Item -LiteralPath $session -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
        if ($lease) { $lease.Dispose() }
        if ($restart -and ![IO.File]::Exists($journal)) {
            Start-Process -FilePath (Join-Path $root 'DeepSeek Harness.exe') -WorkingDirectory $root
        }
    }
}
if ($MyInvocation.InvocationName -ne '.') { Invoke-ShellUpdate }

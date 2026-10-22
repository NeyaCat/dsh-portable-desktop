#Requires -Version 5.1
<#
.SYNOPSIS
    Builds the portable DSH package (runtime + desktop shell) and a ZIP from source.
.DESCRIPTION
    Run from a checkout of this repository on 64-bit Windows. The script downloads
    Node.js (SHA-256 verified against the official manifest), installs
    @deepseek-ai/dsh with the bundled npm, compiles the desktop shell with
    desktop\Build.ps1, installs the vendored pnpm toolchain, bundles MinGit, and
    with -BundleWebView2 also adds the Microsoft-signed WebView2 fixed-version
    runtime. Writes the ZIP next to the output folder. No Node.js, npm, Visual
    Studio or SDK is required.
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\packaging\build-portable.ps1
.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\packaging\build-portable.ps1 -Version 0.1.0 -OutDir D:\Build\DSH-Portable
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$NodeVersion = 'v24.19.0',
    [string]$DshVersion = '0.2.0-rc.2',
    [string]$Registry = 'https://registry.npmjs.org/',
    [string]$NodeMirror = 'https://nodejs.org/dist/',
    [string]$OutDir = '',
    [string]$WebView2Version = 'latest',
    [switch]$BundleWebView2,
    [switch]$SkipZip
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

function Write-Step([string]$Message) {
    Write-Host "`n==> $Message" -ForegroundColor Cyan
}
function Write-Ok([string]$Message) {
    Write-Host "[OK] $Message" -ForegroundColor Green
}
function Fail([string]$Message) {
    throw $Message
}
function Get-WebView2FixedRuntime([string]$Version) {
    $page = Invoke-WebRequest -UseBasicParsing -Uri 'https://developer.microsoft.com/en-us/microsoft-edge/webview2/'
    $html = ([string]$page.Content) -replace '\\u002F', '/'
    $pattern = 'https://msedge[^"''\s<>\\]+/Microsoft\.WebView2\.FixedVersionRuntime\.(\d+\.\d+\.\d+\.\d+)\.x64\.cab'
    $found = @()
    foreach ($match in [regex]::Matches($html, $pattern)) {
        $found += [pscustomobject]@{ Version = $match.Groups[1].Value; Url = $match.Value }
    }
    if ($found.Count -eq 0) {
        Fail 'Could not find a WebView2 fixed-version x64 download on the Microsoft page.'
    }
    if ([string]::IsNullOrWhiteSpace($Version) -or $Version -eq 'latest') {
        $pick = $found | Sort-Object { [version]$_.Version } -Descending | Select-Object -First 1
    } else {
        $pick = $found | Where-Object { $_.Version -eq $Version } | Select-Object -First 1
    }
    if ($null -eq $pick) {
        Fail "WebView2 fixed version $Version is not offered anymore. Use -WebView2Version latest."
    }
    return $pick
}
function Invoke-Npm([string]$NpmCmd, [string]$WorkingDirectory, [string[]]$NpmArguments) {
    Push-Location -LiteralPath $WorkingDirectory
    try {
        $previous = $ErrorActionPreference
        $code = 1
        try {
            $ErrorActionPreference = 'Continue'
            & $NpmCmd @NpmArguments
            $code = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $previous }
    }
    finally { Pop-Location }
    if ($code -ne 0) {
        Fail "npm failed with exit code $code (working directory: $WorkingDirectory)"
    }
}

$repo = Split-Path $PSScriptRoot -Parent
if (-not (Test-Path -LiteralPath (Join-Path $repo 'desktop\Build.ps1'))) {
    Fail "Run this script from a full checkout of the repository (missing desktop\Build.ps1)."
}
if (-not [Environment]::Is64BitOperatingSystem -or
    [Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
    Fail 'This builder requires 64-bit Windows 10/11.'
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$') {
    Fail 'Version must look like 0.1.0 or 0.1.0-rc.1.'
}
if ($NodeVersion -notmatch '^v\d+\.\d+\.\d+$') {
    Fail 'NodeVersion must look like v24.19.0.'
}
if ($DshVersion -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    Fail 'DshVersion must be an exact version, e.g. 0.2.0-rc.2.'
}
if ($Registry -notmatch '^https://') { Fail 'Registry must be an HTTPS URL.' }
if ($NodeMirror -notmatch '^https://') { Fail 'NodeMirror must be an HTTPS URL.' }

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path $repo 'out\DSH-Portable'
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)
$outParent = Split-Path -Parent $OutDir
$zipPath = Join-Path $outParent ("DSH-Portable-$Version-win-x64.zip")

# Never overwrite a previous package: it may contain user data.
if (Test-Path -LiteralPath $OutDir) {
    Fail "Output already exists: $OutDir`nMove or rename it first."
}
if (-not $SkipZip -and (Test-Path -LiteralPath $zipPath)) {
    Fail "ZIP already exists: $zipPath`nMove or rename it first."
}
[System.IO.Directory]::CreateDirectory($outParent) | Out-Null

$stage = Join-Path $outParent ('.dsh-build-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
$unpack = Join-Path $stage 'unpack'
$downloads = Join-Path $stage 'downloads'
foreach ($dir in @($payload, $unpack, $downloads)) {
    [System.IO.Directory]::CreateDirectory($dir) | Out-Null
}

$nodeName = "node-$NodeVersion-win-x64"
$nodeZipFileName = "$nodeName.zip"
$nodeZipPath = Join-Path $downloads $nodeZipFileName
$nodeBaseUrl = $NodeMirror.TrimEnd('/') + '/' + $NodeVersion + '/'
$oldPath = $env:PATH
$oldRegistry = $env:npm_config_registry
$oldCache = $env:npm_config_cache

try {
    Write-Step "Downloading Node.js $NodeVersion (Windows x64)"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $shaText = (Invoke-WebRequest -UseBasicParsing -Uri ($nodeBaseUrl + 'SHASUMS256.txt')).Content
    $pattern = '(?m)^([a-fA-F0-9]{64})\s+\*?' + [regex]::Escape($nodeZipFileName) + '\s*$'
    $shaMatch = [regex]::Match($shaText, $pattern)
    if (-not $shaMatch.Success) { Fail "SHA-256 entry not found for $nodeZipFileName" }
    $expected = $shaMatch.Groups[1].Value.ToUpperInvariant()

    Invoke-WebRequest -UseBasicParsing -Uri ($nodeBaseUrl + $nodeZipFileName) -OutFile $nodeZipPath
    $actual = (Get-FileHash -LiteralPath $nodeZipPath -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $expected) {
        Fail "Node.js SHA-256 mismatch! Expected $expected, got $actual"
    }
    Write-Ok 'Node.js SHA-256 verified against the official manifest'

    Write-Step 'Extracting Node.js'
    Expand-Archive -LiteralPath $nodeZipPath -DestinationPath $unpack
    $nodeSource = Join-Path $unpack $nodeName
    if (-not (Test-Path -LiteralPath (Join-Path $nodeSource 'node.exe'))) {
        Fail 'Node.js ZIP contents are unexpected (node.exe missing).'
    }
    Move-Item -LiteralPath $nodeSource -Destination (Join-Path $payload 'node')
    $nodeExe = Join-Path $payload 'node\node.exe'
    $npmCmd = Join-Path $payload 'node\npm.cmd'
    if (-not (Test-Path -LiteralPath $npmCmd)) { Fail 'npm.cmd is missing from the Node.js archive.' }
    Write-Ok "Bundled Node.js: $(& $nodeExe --version)"

    Write-Step "Compiling the desktop shell (v$Version)"
    Copy-Item -LiteralPath (Join-Path $repo 'DeepSeek Harness.ico') -Destination $payload -Force
    & (Join-Path $repo 'desktop\Build.ps1') -AppRoot $payload -Version $Version
    foreach ($exe in @('DeepSeek Harness.exe', 'Version Switch.exe')) {
        if (-not (Test-Path -LiteralPath (Join-Path $payload $exe))) {
            Fail "$exe was not produced by desktop\Build.ps1"
        }
    }
    Write-Ok 'Shell compiled with product metadata'

    Write-Step "Installing @deepseek-ai/dsh@$DshVersion"
    $packageJson = @{
        name = 'dsh-portable-runtime'
        version = $Version
        private = $true
        dependencies = @{ '@deepseek-ai/dsh' = $DshVersion }
    } | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText(
        (Join-Path $payload 'package.json'),
        $packageJson,
        ([System.Text.UTF8Encoding]::new($false))
    )
    $env:PATH = "$(Join-Path $payload 'node');$oldPath"
    $env:npm_config_registry = $Registry
    $env:npm_config_cache = Join-Path $stage 'npm-cache'
    [System.IO.Directory]::CreateDirectory($env:npm_config_cache) | Out-Null

    Invoke-Npm $npmCmd $payload @('install', '--ignore-scripts', '--no-audit', '--no-fund', '--omit=dev')

    $dshEntry = Join-Path $payload 'node_modules\@deepseek-ai\dsh\lib\bin.js'
    if (-not (Test-Path -LiteralPath $dshEntry)) {
        Fail 'DSH entrypoint was not found: node_modules\@deepseek-ai\dsh\lib\bin.js'
    }
    Write-Ok "DSH entrypoint found ($(& $nodeExe (Join-Path $payload 'node_modules\@deepseek-ai\dsh\lib\bin.js') --version 2>&1 | Select-Object -First 1))"

    Write-Step 'Installing the plugin toolchain (pnpm)'
    $toolsSrc = Join-Path $repo 'desktop\plugin-tools'
    $toolsDst = Join-Path $payload 'desktop\plugin-tools'
    [System.IO.Directory]::CreateDirectory($toolsDst) | Out-Null
    Copy-Item -LiteralPath (Join-Path $toolsSrc 'package.json') -Destination $toolsDst -Force
    Copy-Item -LiteralPath (Join-Path $toolsSrc 'package-lock.json') -Destination $toolsDst -Force
    Invoke-Npm $npmCmd $toolsDst @('ci', '--omit=dev', '--no-audit', '--no-fund')
    if (-not (Test-Path -LiteralPath (Join-Path $toolsDst 'node_modules\.bin\pnpm.cmd'))) {
        Fail 'pnpm was not installed into desktop\plugin-tools'
    }
    Write-Ok 'pnpm toolchain installed'

    Write-Step 'Bundling Git for plugin installs'
    $gitVersion = '2.56.0.2'
    $gitSha = 'DA35E72AA21C005A5A0D298CFBAE110BC1609A815730EA0DDE84B01A1B3CD3BE'
    $gitUrl = "https://github.com/git-for-windows/git/releases/download/v2.56.0.windows.2/MinGit-$gitVersion-64-bit.zip"
    $gitZip = Join-Path $downloads 'mingit.zip'
    Invoke-WebRequest -UseBasicParsing -Uri $gitUrl -OutFile $gitZip
    $gitActual = (Get-FileHash -LiteralPath $gitZip -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($gitActual -ne $gitSha) {
        Fail "MinGit SHA-256 mismatch! Expected $gitSha, got $gitActual"
    }
    Expand-Archive -LiteralPath $gitZip -DestinationPath (Join-Path $payload 'git')
    if (-not (Test-Path -LiteralPath (Join-Path $payload 'git\cmd\git.exe'))) {
        Fail 'MinGit archive is unexpected (git.exe missing).'
    }
    Write-Ok "Bundled Git $gitVersion for GitHub plugin installs"

    $webviewVersion = ''
    if ($BundleWebView2) {
        Write-Step 'Bundling the WebView2 fixed-version runtime'
        $pick = Get-WebView2FixedRuntime $WebView2Version
        $webviewVersion = $pick.Version
        $cab = Join-Path $downloads 'webview2.cab'
        Invoke-WebRequest -UseBasicParsing -Uri $pick.Url -OutFile $cab
        $webviewDir = Join-Path $payload 'webview2'
        [System.IO.Directory]::CreateDirectory($webviewDir) | Out-Null
        & "$env:WINDIR\System32\expand.exe" $cab -F:* $webviewDir | Out-Null
        if ($LASTEXITCODE -ne 0) {
            Fail "expand.exe failed while unpacking the WebView2 runtime (exit code $LASTEXITCODE)."
        }
        $runtimeExe = Get-ChildItem -LiteralPath $webviewDir -Recurse -Filter 'msedgewebview2.exe' |
            Select-Object -First 1
        if ($null -eq $runtimeExe) {
            Fail 'The WebView2 fixed-version package did not contain msedgewebview2.exe.'
        }
        if ($runtimeExe.DirectoryName -ne [System.IO.Path]::GetFullPath($webviewDir).TrimEnd('\')) {
            $nested = $runtimeExe.DirectoryName
            Get-ChildItem -LiteralPath $nested -Force |
                Move-Item -Destination $webviewDir -Force
            Remove-Item -LiteralPath $nested -Recurse -Force
        }
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $webviewDir 'msedgewebview2.exe')
        if ($signature.Status -ne 'Valid' -or
            $signature.SignerCertificate.Subject -notmatch 'Microsoft') {
            Fail 'The downloaded WebView2 runtime is not validly signed by Microsoft.'
        }
        Write-Ok "Bundled WebView2 fixed version $webviewVersion (no system runtime required)"
    }

    Write-Step 'Writing launcher and documentation'
    Copy-Item -LiteralPath (Join-Path $repo 'desktop\update-worker.cjs') -Destination (Join-Path $payload 'desktop') -Force
    Copy-Item -LiteralPath (Join-Path $repo 'desktop\install-plugins.cjs') -Destination (Join-Path $payload 'desktop') -Force
    $launcher = "@echo off`r`nstart `"`" `"%~dp0DeepSeek Harness.exe`"`r`n"
    [System.IO.File]::WriteAllText(
        (Join-Path $payload 'Run-DSH.cmd'),
        $launcher,
        [System.Text.Encoding]::ASCII
    )
    $bundled = "DSH $DshVersion / Node.js $NodeVersion / Git $gitVersion"
    if ($BundleWebView2) {
        $bundled += " / WebView2 $webviewVersion (fixed)"
        $webviewNote = '3. 需要 Windows x64 和 .NET Framework 4.6.2+；WebView2 固定版本已内置，无需系统安装。'
    } else {
        $webviewNote = '3. 需要 Windows x64、.NET Framework 4.6.2+ 和 Microsoft Edge WebView2 Runtime（首次启动可自动安装）。'
    }
    $guide = @"
DSH Portable Desktop (unofficial) - Windows x64
================================================

Shell: DSH Portable Desktop $Version
Bundled: $bundled

【运行】
1. 把整个文件夹解压到可写目录（不要放 Program Files）。
2. 双击 DeepSeek Harness.exe；重复双击会唤起已运行的窗口。
$webviewNote
4. 首次启动会检查环境；Git/pnpm 缺失时可自动补全。也可手动运行：
   DeepSeek Harness.exe --verify-environment
   DeepSeek Harness.exe --repair-environment
5. 环境检查通过后会选择推荐插件：选择安装时插件市场/HTTP 代理/网络配置为必需，其余按主题、辅助、布局、功能分组勾选；选择“暂不安装”则跳过全部插件。直连不可用时自动使用镜像。

【数据】
配置、会话、日志、环境体检结果和 WebView2 数据都在本目录 data 下；删除整个文件夹即完成卸载。

【说明】
- 非官方项目，与 DeepSeek 无隶属关系。
- 本目录包含第三方开源组件（DSH、Node.js，MIT）；其许可证随组件保留。
- 本程序不会向维护者上报数据；联网仅限你配置的模型服务和更新检查。
- 源码与许可：https://github.com/NeyaCat/dsh-portable-desktop
"@
    [System.IO.File]::WriteAllText(
        (Join-Path $payload 'README-Windows.txt'),
        $guide,
        ([System.Text.UTF8Encoding]::new($true))
    )
    [System.IO.Directory]::CreateDirectory((Join-Path $payload 'data')) | Out-Null
    [System.IO.Directory]::CreateDirectory((Join-Path $payload 'workspace')) | Out-Null

    Write-Step 'Including the default plugin set'
    $defaults = Join-Path $repo 'defaults'
    if (Test-Path -LiteralPath $defaults) {
        Copy-Item -LiteralPath $defaults -Destination (Join-Path $payload 'defaults') -Recurse -Force
    }

    Write-Step 'Publishing the package'
    Move-Item -LiteralPath $payload -Destination $OutDir
    Write-Ok "Folder: $OutDir"

    if (-not $SkipZip) {
        Write-Step 'Creating ZIP'
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [System.IO.Compression.ZipFile]::CreateFromDirectory($OutDir, $zipPath)
        Write-Ok "ZIP: $zipPath ($([math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)) MB)"
    }
}
finally {
    $env:PATH = $oldPath
    $env:npm_config_registry = $oldRegistry
    $env:npm_config_cache = $oldCache
    if (Test-Path -LiteralPath $stage) {
        Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
    }
}

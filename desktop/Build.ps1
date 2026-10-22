param(
    [string]$AppRoot = '',
    [string]$Version = '0.1.0'
)

$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+([.-][0-9A-Za-z.-]+)?$') {
    throw "Version must look like 0.1.0 or 0.1.0-rc.1, got '$Version'"
}
$core = ($Version -split '[.-]')[0..2] -join '.'
$fileVersion = "$core.0"

$appRoot = if ([string]::IsNullOrWhiteSpace($AppRoot)) {
    Split-Path $PSScriptRoot -Parent
} else {
    [System.IO.Path]::GetFullPath($AppRoot)
}
if (-not (Test-Path -LiteralPath $appRoot)) {
    New-Item -ItemType Directory -Force -Path $appRoot | Out-Null
}

$sdk = Join-Path $PSScriptRoot 'vendor\webview2'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$ico = Join-Path $appRoot 'DeepSeek Harness.ico'
if (-not (Test-Path -LiteralPath $ico)) {
    throw "Icon not found: $ico. Copy 'DeepSeek Harness.ico' next to the built executables."
}

$generated = Join-Path ([System.IO.Path]::GetTempPath()) ('dsh-shell-info-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $generated | Out-Null

function New-AssemblyInfo([string]$Path, [string]$Title, [string]$Description) {
    $text = @"
using System.Reflection;

[assembly: AssemblyTitle("$Title")]
[assembly: AssemblyDescription("$Description")]
[assembly: AssemblyProduct("DSH Portable Desktop")]
[assembly: AssemblyCompany("NeyaCat")]
[assembly: AssemblyCopyright("Copyright (c) 2026 NeyaCat")]
[assembly: AssemblyVersion("$fileVersion")]
[assembly: AssemblyFileVersion("$fileVersion")]
[assembly: AssemblyInformationalVersion("$Version")]
"@
    [System.IO.File]::WriteAllText($Path, $text, [System.Text.UTF8Encoding]::new($false))
}

$shellInfo = Join-Path $generated 'ShellInfo.g.cs'
$switchInfo = Join-Path $generated 'VersionSwitchInfo.g.cs'
New-AssemblyInfo $shellInfo 'DSH Portable Desktop' 'Unofficial portable desktop shell for DeepSeek Harness'
New-AssemblyInfo $switchInfo 'DSH Portable Desktop - Version Switch' 'Version manager for DSH Portable Desktop'

Copy-Item -LiteralPath "$sdk\lib\net462\Microsoft.Web.WebView2.Core.dll" -Destination $appRoot -Force
Copy-Item -LiteralPath "$sdk\lib\net462\Microsoft.Web.WebView2.WinForms.dll" -Destination $appRoot -Force
Copy-Item -LiteralPath "$sdk\runtimes\win-x64\native\WebView2Loader.dll" -Destination $appRoot -Force

try {
    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$ico" "/resource:$PSScriptRoot\DesktopChrome.js,DesktopChrome.js" "/out:$appRoot\DeepSeek Harness.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/reference:$appRoot\Microsoft.Web.WebView2.Core.dll" "/reference:$appRoot\Microsoft.Web.WebView2.WinForms.dll" "$PSScriptRoot\Desktop.cs" "$PSScriptRoot\LoadingSurface.cs" "$PSScriptRoot\DesktopPreferences.cs" "$PSScriptRoot\Updates.cs" "$PSScriptRoot\SkinTheme.cs" "$PSScriptRoot\ProxyBridge.cs" "$shellInfo"
    if ($LASTEXITCODE -ne 0) { throw 'DeepSeek Harness.exe compilation failed' }

    & $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$ico" "/resource:$PSScriptRoot\VersionSwitch.html,VersionSwitch.html" "/out:$appRoot\Version Switch.exe" "/reference:$appRoot\Microsoft.Web.WebView2.Core.dll" "/reference:$appRoot\Microsoft.Web.WebView2.WinForms.dll" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\VersionSwitch.cs" "$PSScriptRoot\Updates.cs" "$PSScriptRoot\SkinTheme.cs" "$switchInfo"
    if ($LASTEXITCODE -ne 0) { throw 'Version Switch.exe compilation failed' }
}
finally {
    Remove-Item -LiteralPath $generated -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Output "Built DeepSeek Harness.exe and Version Switch.exe (v$Version) in $appRoot"

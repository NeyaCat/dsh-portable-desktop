$ErrorActionPreference = 'Stop'
$appRoot = Split-Path $PSScriptRoot -Parent
$sdk = Join-Path $PSScriptRoot 'vendor\webview2'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
Copy-Item -LiteralPath "$sdk\lib\net462\Microsoft.Web.WebView2.Core.dll" -Destination $appRoot
Copy-Item -LiteralPath "$sdk\lib\net462\Microsoft.Web.WebView2.WinForms.dll" -Destination $appRoot
Copy-Item -LiteralPath "$sdk\runtimes\win-x64\native\WebView2Loader.dll" -Destination $appRoot
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$appRoot\DeepSeek Harness.ico" "/resource:$PSScriptRoot\DesktopChrome.js,DesktopChrome.js" "/out:$appRoot\DeepSeek Harness.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "/reference:$appRoot\Microsoft.Web.WebView2.Core.dll" "/reference:$appRoot\Microsoft.Web.WebView2.WinForms.dll" "$PSScriptRoot\Desktop.cs" "$PSScriptRoot\LoadingSurface.cs" "$PSScriptRoot\DesktopPreferences.cs" "$PSScriptRoot\Updates.cs" "$PSScriptRoot\SkinTheme.cs"
if ($LASTEXITCODE -ne 0) { throw 'DSH.exe compilation failed' }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/win32icon:$appRoot\DeepSeek Harness.ico" "/resource:$PSScriptRoot\VersionSwitch.html,VersionSwitch.html" "/out:$appRoot\Version Switch.exe" "/reference:$appRoot\Microsoft.Web.WebView2.Core.dll" "/reference:$appRoot\Microsoft.Web.WebView2.WinForms.dll" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll "$PSScriptRoot\VersionSwitch.cs" "$PSScriptRoot\Updates.cs" "$PSScriptRoot\SkinTheme.cs"
if ($LASTEXITCODE -ne 0) { throw 'Version Switch.exe compilation failed' }
Write-Output "Built DeepSeek Harness.exe and Version Switch.exe"

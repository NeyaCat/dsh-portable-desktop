#Requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Package,
    [Parameter(Mandatory)][string]$OutputZip
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = (Resolve-Path -LiteralPath $Package).ProviderPath
$files = @('DeepSeek Harness.exe', 'Version Switch.exe', 'Microsoft.Web.WebView2.Core.dll',
    'Microsoft.Web.WebView2.WinForms.dll', 'WebView2Loader.dll')
# Only distributable shell helpers from the staged release. The installed Node,
# DSH, profile defaults, plugin toolchain and user directories have their own lifecycle.
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'desktop') -Filter '*.cjs' -File) {
    $files += 'desktop/' + $file.Name
}
foreach ($name in @('dsh-app.ico', 'version-switch.ico', 'version-switch.svg')) { $files += 'assets/' + $name }
$archive = [IO.Compression.ZipFile]::Open($OutputZip, 'Create')
try {
    foreach ($name in $files) {
        [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, (Join-Path $root $name), $name)
    }
} finally { $archive.Dispose() }

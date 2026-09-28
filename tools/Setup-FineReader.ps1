param(
    [string]$RuntimePath = 'C:\Program Files\ABBYY SDK\12\FineReader Engine\Bin64'
)
$ErrorActionPreference = 'Stop'
$taskRoot = Split-Path -Parent $PSScriptRoot
if (Test-Path -LiteralPath $RuntimePath -PathType Leaf) { $RuntimePath = Split-Path -Parent $RuntimePath }
$runtimeDll = Join-Path $RuntimePath 'FREngine.dll'
$interop = Join-Path $RuntimePath 'FREngine.DotNet.Interop.dll'
if (-not (Test-Path -LiteralPath $runtimeDll) -or -not (Test-Path -LiteralPath $interop)) {
    throw 'Install FineReader Engine 12.8.2 Windows x64, then pass its Bin64 directory using -RuntimePath.'
}
$runtimeVersion = [version](Get-Item -LiteralPath $runtimeDll).VersionInfo.FileVersion
$assembly = [Reflection.AssemblyName]::GetAssemblyName($interop)
if ($assembly.Name -ne 'FREngine.DotNet.Interop' -or $assembly.Version -ne $runtimeVersion -or $runtimeVersion -lt [version]'12.8.2.0') {
    throw 'The native runtime and .NET wrapper must match and be version 12.8.2 or later.'
}
$vendor = Join-Path $taskRoot 'vendor'
New-Item -ItemType Directory -Path $vendor -Force | Out-Null
Copy-Item -LiteralPath $interop -Destination (Join-Path $vendor 'FREngine.DotNet.Interop.dll') -Force
Write-Output "Copied the installed FineReader .NET wrapper $runtimeVersion into this solution's vendor folder."

[CmdletBinding()]
param(
    [string]$OutputRoot
)

$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
if ([string]::IsNullOrWhiteSpace($OutputRoot)) { $OutputRoot = Join-Path $taskRoot 'artifacts\releases' }
$outputRootPath = [System.IO.Path]::GetFullPath($OutputRoot)
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss')
$releaseName = "FineReader-DocLang-Demo-$stamp"
$publishDirectory = Join-Path $outputRootPath $releaseName
$archive = Join-Path $outputRootPath ($releaseName + '.zip')
$project = Join-Path $taskRoot 'src\Abbyy.DocLang.Demo.Web\Abbyy.DocLang.Demo.Web.csproj'
$solution = Join-Path $taskRoot 'Abbyy.DocLang.Demo.sln'
$webAssets = Join-Path $taskRoot 'src\Abbyy.DocLang.Demo.Web\obj\project.assets.json'
$testAssets = Join-Path $taskRoot 'tests\Abbyy.DocLang.Demo.Tests\obj\project.assets.json'

function Test-ValidPackageAssets([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try {
        $packageAssets = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
        return @($packageAssets.logs | Where-Object { $_.level -eq 'Error' }).Count -eq 0
    }
    catch {
        return $false
    }
}

function Test-IsProjectDemoProcess($Process) {
    if ($null -eq $Process -or $Process.ProcessName -ne 'Abbyy.DocLang.Demo.Web') { return $false }
    $runningPath = $Process.Path
    return -not [string]::IsNullOrWhiteSpace($runningPath) -and
        [System.IO.Path]::GetFullPath($runningPath).StartsWith(
            $taskRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
}

function Get-ListeningProcessIds([int]$Port) {
    $owners = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($connection in @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)) {
        [void]$owners.Add([int]$connection.OwningProcess)
    }
    foreach ($line in @(& netstat -ano -p TCP 2>$null)) {
        if ($line -match "^\s*TCP\s+\S+:$Port\s+\S+\s+LISTENING\s+(\d+)\s*$") {
            [void]$owners.Add([int]$Matches[1])
        }
    }
    return @($owners)
}

if (-not (Test-ValidPackageAssets $webAssets) -or -not (Test-ValidPackageAssets $testAssets)) {
    Write-Host 'Restoring required packages...'
    & dotnet restore $solution
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed; no distributable was created. Check NuGet connectivity, then run this script again.' }
}

$projectProcesses = @(Get-Process -Name 'Abbyy.DocLang.Demo.Web' -ErrorAction SilentlyContinue | Where-Object { Test-IsProjectDemoProcess $_ })
foreach ($running in $projectProcesses) {
    Write-Host "Stopping the running FineReader DocLang Demo (PID $($running.Id)) before the Release build."
    Stop-Process -Id $running.Id -Force
    Wait-Process -Id $running.Id -Timeout 10 -ErrorAction SilentlyContinue
}

foreach ($processId in @(Get-ListeningProcessIds 5188)) {
    $running = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $running) { continue }
    throw "Port 5188 is used by '$($running.ProcessName)' (PID $processId). Stop it before publishing."
}

Write-Host 'Running the complete Release test suite, including browser tests...'
& dotnet test $solution --configuration Release --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release tests failed; no distributable was created.' }

Write-Host "Publishing the immutable application artifact to '$publishDirectory'..."
New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
& dotnet publish $project --configuration Release --output $publishDirectory --no-restore --nologo
if ($LASTEXITCODE -ne 0) { throw 'Release publishing failed.' }
Copy-Item -LiteralPath (Join-Path $taskRoot 'README.md') -Destination (Join-Path $publishDirectory 'README.md')

& (Join-Path $PSScriptRoot 'Verify-Release.ps1') -ApplicationDirectory $publishDirectory
Compress-Archive -Path (Join-Path $publishDirectory '*') -DestinationPath $archive
Write-Output "Release directory: $publishDirectory"
Write-Output "Distributable archive: $archive"

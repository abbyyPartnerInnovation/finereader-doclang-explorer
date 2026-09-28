[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$taskRoot = [System.IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$project = Join-Path $taskRoot 'src\Abbyy.DocLang.Demo.Web\Abbyy.DocLang.Demo.Web.csproj'
$assets = Join-Path $taskRoot 'src\Abbyy.DocLang.Demo.Web\obj\project.assets.json'

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

if (-not (Test-ValidPackageAssets $assets)) {
    Write-Host 'Restoring required packages...'
    & dotnet restore $project
    if ($LASTEXITCODE -ne 0) { throw 'Package restore failed. Check NuGet connectivity, then run this script again.' }
}

$projectProcesses = @(Get-Process -Name 'Abbyy.DocLang.Demo.Web' -ErrorAction SilentlyContinue | Where-Object { Test-IsProjectDemoProcess $_ })
foreach ($running in $projectProcesses) {
    Write-Host "Stopping the previous FineReader DocLang Demo process (PID $($running.Id)) before rebuilding."
    Stop-Process -Id $running.Id -Force
    Wait-Process -Id $running.Id -Timeout 10 -ErrorAction SilentlyContinue
}

foreach ($processId in @(Get-ListeningProcessIds 5188)) {
    $running = Get-Process -Id $processId -ErrorAction SilentlyContinue
    if ($null -eq $running) { continue }
    throw "Port 5188 is already used by '$($running.ProcessName)' (PID $processId). Stop that process or change its port before starting the demo."
}

Write-Host 'Building and starting the complete Release application at http://localhost:5188 ...'
& dotnet run --project $project --configuration Release --no-restore
exit $LASTEXITCODE

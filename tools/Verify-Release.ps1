[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ApplicationDirectory
)

$ErrorActionPreference = 'Stop'
$applicationRoot = [System.IO.Path]::GetFullPath($ApplicationDirectory)
$executable = Join-Path $applicationRoot 'Abbyy.DocLang.Demo.Web.exe'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The published application executable was not found at '$executable'."
}
if (Test-Path -LiteralPath (Join-Path $applicationRoot 'appsettings.Local.json')) {
    throw 'The published application unexpectedly contains appsettings.Local.json.'
}

$port = Get-Random -Minimum 52000 -Maximum 59000
$baseUrl = "http://127.0.0.1:$port"
$verificationRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('finereader-doclang-release-check-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verificationRoot | Out-Null
$stdout = Join-Path $verificationRoot 'stdout.log'
$stderr = Join-Path $verificationRoot 'stderr.log'
$process = $null

try {
    $process = Start-Process -FilePath $executable -ArgumentList @('--urls', $baseUrl) -WorkingDirectory $applicationRoot -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    $identity = $null
    for ($attempt = 0; $attempt -lt 80 -and $null -eq $identity; $attempt++) {
        if ($process.HasExited) { break }
        try { $identity = Invoke-RestMethod -Uri "$baseUrl/api/application" -Method Get }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if ($null -eq $identity) {
        $details = if (Test-Path -LiteralPath $stderr) { Get-Content -LiteralPath $stderr -Raw } else { '' }
        throw "The published application did not start successfully. $details"
    }

    $index = (Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/").Content
    if ($index -notmatch '<meta name="application-contract" content="([^"]+)">' -or $Matches[1] -ne $identity.contractVersion) {
        throw 'The published frontend contract identity does not match the backend.'
    }
    if ($index -notmatch '<meta name="application-build" content="([^"]+)">' -or $Matches[1] -ne $identity.buildId) {
        throw 'The published frontend build identity does not match the backend.'
    }
    if ($index.Contains('__APP_BUILD_ID__')) { throw 'The published page contains an unstamped build placeholder.' }

    $headers = @{
        'X-Demo-Request' = '1'
        'X-Demo-Contract' = $identity.contractVersion
        'X-Demo-Build' = $identity.buildId
    }
    $settings = Invoke-RestMethod -Uri "$baseUrl/api/settings" -Method Get -Headers $headers
    if ($null -eq $settings.demoFeatures.features.enableCompareAll) { throw 'The release settings contract does not contain Enable Compare All.' }
    foreach ($obsolete in @('showEstimatedCost', 'enableJsonAsAiInput', 'enableDocLangAsAiInput', 'enablePlainTextAsAiInput')) {
        if ($settings.demoFeatures.features.PSObject.Properties.Name -contains $obsolete) {
            throw "The release settings contract still exposes obsolete field '$obsolete'."
        }
    }

    try {
        Invoke-RestMethod -Uri "$baseUrl/api/settings" -Method Get | Out-Null
        throw 'An API request without application identity was unexpectedly accepted.'
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 409) { throw }
    }
    try {
        Invoke-RestMethod -Uri "$baseUrl/api/ai/compare" -Method Get -Headers $headers | Out-Null
        throw 'GET unexpectedly matched the Compare All endpoint.'
    } catch {
        if ($_.Exception.Response.StatusCode.value__ -ne 405) { throw }
    }

    Write-Output "Verified release version $($identity.applicationVersion), contract $($identity.contractVersion), build $($identity.buildId)."
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 10 -ErrorAction SilentlyContinue
    }
    $resolvedVerification = [System.IO.Path]::GetFullPath($verificationRoot)
    $resolvedTemp = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
    if ($resolvedVerification.StartsWith($resolvedTemp, [System.StringComparison]::OrdinalIgnoreCase) -and (Test-Path -LiteralPath $resolvedVerification)) {
        Remove-Item -LiteralPath $resolvedVerification -Recurse -Force
    }
}

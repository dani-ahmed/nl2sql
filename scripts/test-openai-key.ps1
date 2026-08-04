param(
    [string]$DotEnvPath,
    [string]$Model
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($DotEnvPath)) {
    $DotEnvPath = Join-Path $projectRoot '.env'
}

function Read-DotEnvValue([string[]]$lines, [string]$name) {
    $escaped = [regex]::Escape($name)
    $line = $lines | Where-Object { $_ -match "^\s*$escaped\s*=" } | Select-Object -First 1
    if (-not $line) { return $null }
    return (($line -split '=', 2)[1].Trim().Trim('"').Trim("'"))
}

$dotenvLines = if (Test-Path -LiteralPath $DotEnvPath) {
    Get-Content -LiteralPath $DotEnvPath
}
else {
    @()
}
$processKey = [Environment]::GetEnvironmentVariable('OPENAI_API_KEY', 'Process')
$dotenvKey = Read-DotEnvValue $dotenvLines 'OPENAI_API_KEY'
$hasProcessKey = $null -ne $processKey
if ($hasProcessKey -and [string]::IsNullOrWhiteSpace($processKey)) {
    throw 'OPENAI_API_KEY exists in the process environment but is blank. Remove it to use .env, or set it to a valid key.'
}
$apiKey = if ($hasProcessKey) { $processKey } else { $dotenvKey }
$keySource = if ($hasProcessKey) { 'process environment' } else { [System.IO.Path]::GetFullPath($DotEnvPath) }
if ([string]::IsNullOrWhiteSpace($apiKey)) {
    throw 'No effective OPENAI_API_KEY was found in the process environment or project .env file.'
}
if ([string]::IsNullOrWhiteSpace($Model)) {
    $processModel = [Environment]::GetEnvironmentVariable('OPENAI_MODEL', 'Process')
    if ($null -ne $processModel -and [string]::IsNullOrWhiteSpace($processModel)) {
        throw 'OPENAI_MODEL exists in the process environment but is blank. Remove it to use .env/default configuration, or set it to a model ID.'
    }
    $Model = if ($null -ne $processModel) { $processModel } else { Read-DotEnvValue $dotenvLines 'OPENAI_MODEL' }
}
if ([string]::IsNullOrWhiteSpace($Model)) {
    $Model = 'gpt-5.6-terra'
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $fingerprintBytes = $sha256.ComputeHash([System.Text.Encoding]::UTF8.GetBytes($apiKey))
}
finally {
    $sha256.Dispose()
}
$fingerprint = ([System.BitConverter]::ToString($fingerprintBytes).Replace('-', '').Substring(0, 12)).ToLowerInvariant()
Write-Output "Credential source: $keySource"
Write-Output "Credential fingerprint: sha256:$fingerprint (secret hidden)"
Write-Output "Configured model: $Model"

Add-Type -AssemblyName System.Net.Http
$client = [System.Net.Http.HttpClient]::new()
$client.Timeout = [TimeSpan]::FromSeconds(45)
$client.DefaultRequestHeaders.Authorization = [System.Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer', $apiKey)

function Get-RequestId([System.Net.Http.HttpResponseMessage]$response) {
    if ($response.Headers.Contains('x-request-id')) {
        return ($response.Headers.GetValues('x-request-id') | Select-Object -First 1)
    }
    return 'not-returned'
}

function Get-SafeProviderError([string]$body) {
    try {
        $parsed = $body | ConvertFrom-Json
        return "code=$($parsed.error.code) message=$($parsed.error.message)"
    }
    catch {
        return 'provider returned a non-JSON error body'
    }
}

try {
    Write-Output '[1/2] Testing API-key authentication and configured-model access...'
    $modelResponse = $client.GetAsync("https://api.openai.com/v1/models/$Model").GetAwaiter().GetResult()
    $modelBody = $modelResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $modelRequestId = Get-RequestId $modelResponse
    if (-not $modelResponse.IsSuccessStatusCode) {
        $safeError = Get-SafeProviderError $modelBody
        Write-Output "[1/2] FAIL HTTP $([int]$modelResponse.StatusCode) request_id=$modelRequestId $safeError"
        throw 'OpenAI authentication/model-access verification failed.'
    }
    Write-Output "[1/2] PASS HTTP $([int]$modelResponse.StatusCode) request_id=$modelRequestId"

    Write-Output '[2/2] Testing a minimal Responses API inference...'
    $payload = @{
        model = $Model
        store = $false
        reasoning = @{ effort = 'low' }
        input = 'Reply with exactly OK.'
        max_output_tokens = 256
    } | ConvertTo-Json -Depth 5 -Compress
    $content = [System.Net.Http.StringContent]::new($payload, [System.Text.Encoding]::UTF8, 'application/json')
    $response = $client.PostAsync('https://api.openai.com/v1/responses', $content).GetAwaiter().GetResult()
    $bodyText = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $requestId = Get-RequestId $response
    if (-not $response.IsSuccessStatusCode) {
        $safeError = Get-SafeProviderError $bodyText
        Write-Output "[2/2] FAIL HTTP $([int]$response.StatusCode) request_id=$requestId $safeError"
        throw 'OpenAI Responses API verification failed.'
    }
    $body = $bodyText | ConvertFrom-Json
    $outputText = (($body.output.content | Where-Object { $_.type -eq 'output_text' } | ForEach-Object { $_.text }) -join '').Trim()
    Write-Output "[2/2] PASS HTTP $([int]$response.StatusCode) request_id=$requestId input_tokens=$($body.usage.input_tokens) output_tokens=$($body.usage.output_tokens) output=$outputText"
    Write-Output 'OpenAI credential and Responses API checks passed.'
}
catch [System.Management.Automation.MethodInvocationException] {
    throw "Could not connect to api.openai.com: $($_.Exception.InnerException.Message)"
}
finally {
    $client.Dispose()
    $apiKey = $null
    $processKey = $null
    $dotenvKey = $null
    $processModel = $null
}

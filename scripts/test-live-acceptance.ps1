param(
    [string]$CasePattern,
    [ValidateRange(0, 195)]
    [int]$MaxCases = 0,
    [switch]$PureSemanticPlanner,
    [switch]$SummaryOnly,
    [switch]$SkipOpenAiPreflight,
    [string]$ReportPath
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$casesPath = Join-Path $projectRoot 'NL2SQL_TEST_CASES.csv'
$planPath = Join-Path $projectRoot 'tests/acceptance/NL2SQL_TEST_PLAN.md'

if (-not (Test-Path -LiteralPath $casesPath)) {
    throw "Acceptance CSV not found: $casesPath"
}
if (-not (Test-Path -LiteralPath $planPath)) {
    throw "Acceptance plan not found: $planPath"
}

$processKey = [Environment]::GetEnvironmentVariable('OPENAI_API_KEY', 'Process')
$hasProcessKey = $null -ne $processKey
if ($hasProcessKey -and [string]::IsNullOrWhiteSpace($processKey)) {
    throw 'OPENAI_API_KEY exists in the process environment but is blank. Remove it to use .env, or set it to a valid key.'
}
$dotenvPath = Join-Path $projectRoot '.env'
$hasDotenvKey = (Test-Path -LiteralPath $dotenvPath) -and [bool](
    Get-Content -LiteralPath $dotenvPath |
        Where-Object { $_ -match '^\s*OPENAI_API_KEY\s*=\s*.+$' } |
        Select-Object -First 1)
if (-not $hasProcessKey -and -not $hasDotenvKey) {
    throw 'Set OPENAI_API_KEY in the process environment or the ignored EmployeeQuery/.env file.'
}

$managedNames = @(
    'EMPLOYEEQUERY_DISABLE_DOTENV',
    'EmployeeQueryArtifactsRoot',
    'NL2SQL_APP_COMMAND',
    'NL2SQL_CASES_PATH',
    'NL2SQL_CASE_PATTERN',
    'NL2SQL_MAX_CASES',
    'NL2SQL_MODEL_EVAL_MODE',
    'NL2SQL_REQUIRE_OPENAI_PLANNER',
    'NL2SQL_RESULT_PATH',
    'NL2SQL_TIMEOUT_SECONDS',
    'NL2SQL_VERBOSE_CASE_OUTPUT',
    'PYTHONUNBUFFERED'
)
$saved = @{}
foreach ($name in $managedNames) {
    $saved[$name] = if (Test-Path "Env:$name") { [string](Get-Item "Env:$name").Value } else { $null }
}

Push-Location $projectRoot
try {
    if (-not $SkipOpenAiPreflight) {
        Write-Output 'Preflighting OpenAI authentication, model access, and structured outputs...'
        & (Join-Path $PSScriptRoot 'test-openai-key.ps1')
    }

    Remove-Item Env:EMPLOYEEQUERY_DISABLE_DOTENV -ErrorAction SilentlyContinue
    if ([string]::IsNullOrWhiteSpace($CasePattern)) {
        Remove-Item Env:NL2SQL_CASE_PATTERN -ErrorAction SilentlyContinue
    }
    else {
        $env:NL2SQL_CASE_PATTERN = $CasePattern
    }
    if ($MaxCases -le 0) {
        Remove-Item Env:NL2SQL_MAX_CASES -ErrorAction SilentlyContinue
    }
    else {
        $env:NL2SQL_MAX_CASES = $MaxCases.ToString([System.Globalization.CultureInfo]::InvariantCulture)
    }
    $env:NL2SQL_CASES_PATH = [System.IO.Path]::GetFullPath($casesPath)
    if ($PureSemanticPlanner) {
        $env:NL2SQL_MODEL_EVAL_MODE = '1'
    }
    else {
        Remove-Item Env:NL2SQL_MODEL_EVAL_MODE -ErrorAction SilentlyContinue
    }
    $env:NL2SQL_REQUIRE_OPENAI_PLANNER = '1'
    if (-not [string]::IsNullOrWhiteSpace($ReportPath)) {
        $env:NL2SQL_RESULT_PATH = [System.IO.Path]::GetFullPath($ReportPath)
    }
    elseif ([string]::IsNullOrWhiteSpace($env:NL2SQL_RESULT_PATH)) {
        $stamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss', [System.Globalization.CultureInfo]::InvariantCulture)
        $env:NL2SQL_RESULT_PATH = Join-Path $projectRoot "artifacts/evaluations/live-acceptance-$stamp.json"
    }
    Remove-Item -LiteralPath $env:NL2SQL_RESULT_PATH -ErrorAction SilentlyContinue
    $env:NL2SQL_TIMEOUT_SECONDS = '120'
    if ($SummaryOnly) {
        Remove-Item Env:NL2SQL_VERBOSE_CASE_OUTPUT -ErrorAction SilentlyContinue
    }
    else {
        $env:NL2SQL_VERBOSE_CASE_OUTPUT = '1'
    }
    $env:PYTHONUNBUFFERED = '1'
    if ([string]::IsNullOrWhiteSpace($env:EmployeeQueryArtifactsRoot)) {
        $env:EmployeeQueryArtifactsRoot = Join-Path ([System.IO.Path]::GetTempPath()) 'EmployeeQuery-live-build'
    }

    dotnet restore src/EmployeeQuery.Console/EmployeeQuery.Console.csproj --configfile NuGet.config --ignore-failed-sources -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'Console restore failed.' }
    dotnet build src/EmployeeQuery.Console/EmployeeQuery.Console.csproj -c Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Console build failed.' }

    $buildRoot = if ($env:EmployeeQueryArtifactsRoot) {
        [System.IO.Path]::GetFullPath($env:EmployeeQueryArtifactsRoot)
    }
    else {
        Join-Path $projectRoot 'scratchdir'
    }
    $consoleDll = Join-Path $buildRoot 'bin/c/Release/net8.0/EmployeeQuery.Console.dll'
    $env:NL2SQL_APP_COMMAND = ConvertTo-Json @('dotnet', $consoleDll, '--plain') -Compress

    $mode = if ($PureSemanticPlanner) { 'pure semantic planner (no exact catalog)' } else { 'production model-first routing' }
    Write-Output "Running key-enabled cases from $casesPath"
    Write-Output "Routing mode: $mode"
    if (-not [string]::IsNullOrWhiteSpace($CasePattern)) { Write-Output "Case pattern: $CasePattern" }
    if ($MaxCases -gt 0) { Write-Output "Maximum cases: $MaxCases" }
    Write-Output "Acceptance contract: $planPath"
    python -u -m unittest tests.test_console_app.ConsoleApplicationAcceptanceTests.test_all_selected_cases
    $testExitCode = $LASTEXITCODE
    if (Test-Path -LiteralPath $env:NL2SQL_RESULT_PATH) {
        $summary = Get-Content -LiteralPath $env:NL2SQL_RESULT_PATH -Raw | ConvertFrom-Json
        Write-Output "Live acceptance result: $($summary.passed)/$($summary.caseCount) passed; $($summary.failed) failed"
        Write-Output "Detailed report: $env:NL2SQL_RESULT_PATH"
    }
    if ($testExitCode -ne 0) { throw 'Live OpenAI acceptance tests failed.' }
}
finally {
    foreach ($name in $managedNames) {
        if ($null -eq $saved[$name]) {
            Remove-Item "Env:$name" -ErrorAction SilentlyContinue
        }
        else {
            Set-Item "Env:$name" $saved[$name]
        }
    }
    Pop-Location
}

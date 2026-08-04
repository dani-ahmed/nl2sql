$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$hadApiKey = Test-Path Env:OPENAI_API_KEY
$previousApiKey = $env:OPENAI_API_KEY
$hadAppCommand = Test-Path Env:NL2SQL_APP_COMMAND
$previousAppCommand = $env:NL2SQL_APP_COMMAND
$hadDotenvDisable = Test-Path Env:EMPLOYEEQUERY_DISABLE_DOTENV
$previousDotenvDisable = $env:EMPLOYEEQUERY_DISABLE_DOTENV
Push-Location $root
try {
    $buildRoot = if ($env:EmployeeQueryArtifactsRoot) {
        [System.IO.Path]::GetFullPath($env:EmployeeQueryArtifactsRoot)
    }
    else {
        Join-Path $root 'scratchdir'
    }
    $publishRoot = if ($env:EmployeeQueryPublishRoot) {
        [System.IO.Path]::GetFullPath($env:EmployeeQueryPublishRoot)
    }
    elseif ($env:EmployeeQueryArtifactsRoot) {
        Join-Path $buildRoot 'publish'
    }
    else {
        Join-Path $root 'artifacts/publish'
    }
    # Offline verification must remain offline even if the developer has a real
    # key in the local, ignored .env file.
    $env:EMPLOYEEQUERY_DISABLE_DOTENV = '1'
    dotnet restore EmployeeQuery.sln --configfile NuGet.config --ignore-failed-sources -p:RestoreLockedMode=true
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

    # Build sequentially. Besides making logs easier to diagnose, this works on
    # constrained runners that do not permit parallel MSBuild output writers.
    $projects = @(
        'src/EmployeeQuery.Application/EmployeeQuery.Application.csproj',
        'src/EmployeeQuery.Infrastructure/EmployeeQuery.Infrastructure.csproj',
        'src/EmployeeQuery.Console/EmployeeQuery.Console.csproj',
        'tests/EmployeeQuery.UnitTests/EmployeeQuery.UnitTests.csproj',
        'tests/EmployeeQuery.IntegrationTests/EmployeeQuery.IntegrationTests.csproj'
    )
    foreach ($project in $projects) {
        dotnet build $project -c Release --no-restore
        if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $project." }
    }

    dotnet format EmployeeQuery.sln --verify-no-changes --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Formatting verification failed.' }

    dotnet (Join-Path $buildRoot 'bin/d/Release/net8.0/EmployeeQuery.UnitTests.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Unit tests failed.' }
    dotnet (Join-Path $buildRoot 'bin/e/Release/net8.0/EmployeeQuery.IntegrationTests.dll')
    if ($LASTEXITCODE -ne 0) { throw 'Integration tests failed.' }
    python -m unittest tests.test_case_catalog tests.test_harness_selftest -v
    if ($LASTEXITCODE -ne 0) { throw 'Catalog or harness self-tests failed.' }

    Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
    $consoleDll = Join-Path $buildRoot 'bin/c/Release/net8.0/EmployeeQuery.Console.dll'
    $env:NL2SQL_APP_COMMAND = ConvertTo-Json @('dotnet', $consoleDll, '--plain') -Compress
    python -m unittest tests.test_console_app -v
    if ($LASTEXITCODE -ne 0) { throw 'Console acceptance tests failed.' }

    dotnet publish src/EmployeeQuery.Console/EmployeeQuery.Console.csproj -c Release --no-build --no-restore -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'Console publish failed.' }
}
finally {
    if ($hadApiKey) {
        $env:OPENAI_API_KEY = $previousApiKey
    }
    else {
        Remove-Item Env:OPENAI_API_KEY -ErrorAction SilentlyContinue
    }
    if ($hadAppCommand) {
        $env:NL2SQL_APP_COMMAND = $previousAppCommand
    }
    else {
        Remove-Item Env:NL2SQL_APP_COMMAND -ErrorAction SilentlyContinue
    }
    if ($hadDotenvDisable) {
        $env:EMPLOYEEQUERY_DISABLE_DOTENV = $previousDotenvDisable
    }
    else {
        Remove-Item Env:EMPLOYEEQUERY_DISABLE_DOTENV -ErrorAction SilentlyContinue
    }
    Pop-Location
}

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet build EmployeeQuery.sln -c Release
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    python tests/live_model_evaluation.py
    exit $LASTEXITCODE
}
finally {
    Pop-Location
}

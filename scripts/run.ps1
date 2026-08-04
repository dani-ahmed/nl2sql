$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    dotnet run --project src/EmployeeQuery.Console -- @args
}
finally {
    Pop-Location
}

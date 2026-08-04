# EmployeeQuery

EmployeeQuery is a .NET 8 console application that answers natural-language questions about the supplied SQLite employee database. OpenAI maps each question to a typed semantic plan; trusted C# validates the plan, compiles parameterized SQL, enforces a fixed department scope, executes the query, and renders the result.

The model never receives employee rows, produces executable SQL, selects the authorized department, or accesses the database.

## Quick start

### Prerequisites

- .NET 8 SDK (`8.0.319` is pinned in `global.json`)
- PowerShell 5.1+ or PowerShell 7+
- An OpenAI API key and access to a Responses API model that supports strict structured output
- Python 3.12+ only when running the full acceptance suite or model evaluation

Restore and build from the repository root:

```powershell
dotnet restore EmployeeQuery.sln --configfile NuGet.config --ignore-failed-sources
dotnet build EmployeeQuery.sln -c Release --no-restore
```

Create the local configuration file:

```powershell
Copy-Item .env.example .env
```

Then set your API key and, if needed, a model available to your OpenAI project:

```dotenv
OPENAI_API_KEY=your-key
OPENAI_MODEL=gpt-5.6-terra
```

`.env` is Git-ignored and must not be committed. The same settings can be supplied as process environment variables; `OPENAI_MODEL` overrides the default model.

Run the application:

```powershell
./scripts/run.ps1
```

Or use the .NET CLI directly:

```powershell
dotnet run --project src/EmployeeQuery.Console
```

## Usage

At startup, the application randomly selects `Engineering`, `Marketing`, or `Sales`. That department remains fixed for the process lifetime and is displayed before the query loop begins.

Example questions:

```text
Who are the software engineers?
Which employees have an AWS certification?
What is the average salary?
List employees who started after 2023 and their certifications.
Who has the highest total remaining benefits balance?
```

Interactive commands:

- `/help` displays commands and example questions.
- `/explain` toggles plan, compiler, SQL, parameter, and execution details.
- `/clear` clears the one-step conversation context without changing department scope.
- `/exit`, `exit`, or `quit` stops the application.

For plain redirected output, run `./scripts/run.ps1 --plain`.

## Architecture

```text
Question
  -> OpenAI Responses API (strict structured output)
  -> typed QueryPlan (no SQL or department field)
  -> deterministic validation
  -> trusted parameterized SQL compiler
  -> department-scoped in-memory SQLite database
  -> result-policy validation
  -> deterministic console output
```

The solution is divided into three projects:

- `EmployeeQuery.Application` contains domain types, semantic plans, validation, orchestration, result descriptors, and conversation context.
- `EmployeeQuery.Infrastructure` contains the OpenAI adapter, SQL compiler, scoped SQLite session, resilience catalog, and structured logging.
- `EmployeeQuery.Console` contains configuration, application composition, the interactive loop, and output rendering.

The closed semantic model supports employee, certification, benefit, and aggregate questions; text, numeric, date, existence, and null filters; grouping, sorting, limits, and tied top results. Unsupported or ambiguous requests fail closed or return a clarification instead of producing arbitrary SQL.

## Department isolation

Department scope is enforced by application and database controls, not by prompting the model:

1. A cryptographically random department is selected once and stored in an immutable session.
2. The department is omitted from the model schema, so the model cannot choose or modify it.
3. The source database is opened read-only, and only employees from the selected department and their related rows are copied into a private in-memory database.
4. The source connection is closed before the interactive query loop begins.
5. Every compiled query independently includes a bound `e.Department = :department` predicate.
6. The executor validates the query policy, result shape, row limit, and returned employee identities before displaying results.
7. SQLite `query_only` mode, a command timeout, serialized access, and a 200-row hard cap provide additional containment.

As a result, unauthorized employee rows are physically absent from the database used to answer questions.

## Exact-match resilience catalog

`NL2SQL_TEST_CASES.csv` is packaged with the console as a reviewed query catalog. It is a narrow availability mechanism, not the primary natural-language planner:

- Normal questions are sent to OpenAI first, including questions that appear in the catalog.
- Catalog recovery is considered only after both OpenAI attempts fail because of a transient transport, timeout, rate-limit, or server error.
- Recovery requires an exact normalized question match. There is no fuzzy or heuristic catalog routing.
- Authentication, permission, model-access, quota, configuration, cancellation, malformed-output, and unmatched-question failures do not use the catalog.
- Any successful OpenAI plan, clarification, or refusal remains authoritative.

The offline acceptance suite intentionally simulates an exhausted transient model failure. It validates the recovery path, compiler, database behavior, output contract, and department isolation; it does not measure live model accuracy. The live and pure-model checks below exercise model-first behavior separately.

## Testing

Run the complete offline verification suite:

```powershell
./scripts/test.ps1
```

The suite performs locked restore, Release builds, formatting verification, C# unit and integration checks, Python harness checks, all 195 console/database cases, repeated random-startup audits, and a publish check. The verified baseline is:

- 93 unit checks
- 168 integration checks
- 6 Python catalog and harness checks
- 195/195 black-box console/database cases
- 9 independent startup sessions covering all three departments
- 0 build warnings

No OpenAI request is made by this offline suite.

Optional key-enabled checks:

```powershell
# Verify credentials and model access with one minimal request
./scripts/test-openai-key.ps1

# Run model-first acceptance cases
./scripts/test-live-acceptance.ps1 -MaxCases 10 -SummaryOnly

# Evaluate the versioned 44-case semantic set without catalog recovery
./scripts/evaluate-model.ps1
```

The full acceptance contract is documented in `tests/acceptance/NL2SQL_TEST_PLAN.md`. Generated evaluation reports are written under the ignored `artifacts/evaluations` directory.

## Structured logs

Opt-in JSON-line operational logs are written to standard error:

```powershell
$env:EMPLOYEEQUERY_STRUCTURED_LOGS = '1'
./scripts/run.ps1
```

Logs include event names, correlation IDs, planner/compiler metadata, duration, and row counts. By default they exclude credentials, authorization headers, prompts, SQL parameter values, result rows, and question text.

## Limitations

- The semantic vocabulary targets the supplied three-table employee schema and three fixed departments.
- Queries are read-only, English-first, and limited to 200 rows; pagination is not implemented.
- Conversation context contains only the previous successful question and validated plan.
- Each session uses a startup snapshot, so source changes are visible only after restarting.
- Monetary values inherit the source database's SQLite `REAL` representation.
- Normal operation requires network access and a compatible OpenAI model.

## AI tooling disclosure

OpenAI ChatGPT and Codex were used during design, implementation, testing, and documentation. The author reviewed the resulting code, design decisions, and application behavior.

At runtime, OpenAI is used only to map natural-language questions into the closed semantic plan. Authorization, validation, SQL compilation, database access, result checking, summaries, and rendering remain application-owned.

## Repository layout

```text
EmployeeQuery/
|-- data/employees.db               supplied SQLite database
|-- scripts/                        run and verification scripts
|-- src/
|   |-- EmployeeQuery.Application/
|   |-- EmployeeQuery.Infrastructure/
|   `-- EmployeeQuery.Console/
|-- tests/
|   |-- EmployeeQuery.UnitTests/
|   |-- EmployeeQuery.IntegrationTests/
|   `-- acceptance/
|-- NL2SQL_TEST_CASES.csv           acceptance oracle and resilience catalog
|-- EmployeeQuery.sln
|-- Directory.Build.props
|-- Directory.Packages.props
|-- NuGet.config
|-- .env.example
`-- README.md
```

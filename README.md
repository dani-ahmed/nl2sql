# EmployeeQuery

EmployeeQuery is a safety-first .NET console application that answers natural-language questions about the supplied `employees.db` SQLite database. It uses OpenAI to map language into a closed semantic `QueryPlan`; trusted C# validates the plan, compiles parameterized SQL, injects an immutable department guardrail, executes against a department-only in-memory database, and renders deterministic results.

The model never writes executable SQL, receives employee data, chooses the authorized department, executes database operations, or summarizes query results.

## Assignment requirements

This repository implements the take-home requirements:

- Randomly select and display `Engineering`, `Marketing`, or `Sales` once at startup.
- Keep that department fixed until the process restarts.
- Accept natural-language questions in an interactive console loop.
- Dynamically compile SQL for employee, certification, salary, bonus, start-date, and benefits questions.
- Execute one read-only, parameterized SQLite statement for each successful request.
- Never return rows or aggregates from another department.
- Handle empty, ambiguous, unsafe, malformed, and out-of-domain requests without executing SQL.
- Continue until the user enters `exit`, `quit`, or `/exit`.

## Prerequisites

- .NET 8 SDK. Development and verification used SDK `8.0.319`.
- PowerShell 5.1+ or PowerShell 7+ for the provided scripts.
- Python 3.12+ for black-box acceptance tests and optional live-model evaluation.
- An OpenAI API key with access to the configured model for normal operation.

Package versions and lock files are committed for repeatable restore.

## Setup and installation

From the repository root:

```powershell
dotnet --version
dotnet restore EmployeeQuery.sln --configfile NuGet.config --ignore-failed-sources
dotnet build EmployeeQuery.sln -c Release --no-restore
```

Create a local configuration file from the template:

```powershell
Copy-Item .env.example .env
```

Edit `.env`:

```dotenv
OPENAI_API_KEY=your-key
OPENAI_MODEL=gpt-5.6-terra
```

`.env` is local-only and Git-ignored. Copy `.env.example`, add your key, and do not commit or share the populated `.env` file. You can also provide the same values through your shell environment.

Process variables are also supported:

```powershell
$env:OPENAI_API_KEY = 'your-key'
$env:OPENAI_MODEL = 'gpt-5.6-terra'
```

`gpt-5.6-terra` is the default because the model's job is a bounded semantic-mapping task governed by strict JSON Schema and a closed set of rules. It does not need to invent SQL or perform open-ended database reasoning. The model remains configurable through `OPENAI_MODEL` so an evaluator can use another compatible model available to their API project.

## Run the application

```powershell
./scripts/run.ps1
```

Or run it directly:

```powershell
dotnet run --project src/EmployeeQuery.Console
```

For plain redirected/script output:

```powershell
./scripts/run.ps1 --plain
```

At startup the application:

1. Loads the OpenAI key/model configuration.
2. Cryptographically selects one allowed department.
3. Logs the selected department and immutable session scope.
4. Opens the source database read-only.
5. Copies only that department's employees and related child rows into a private in-memory SQLite database.
6. Verifies the snapshot, closes the source connection, and enables SQLite `query_only` mode.
7. Starts the natural-language query loop.

Example questions:

```text
Who are the software engineers?
Which employees have an AWS certification?
What is the average salary?
List employees who started after 2023 and their certifications.
Who has the highest total remaining benefits balance?
```

Interactive commands:

- `/help`: display help and examples.
- `/explain`: toggle the validated plan, compiler strategy, parameterized SQL, safe parameters, model/prompt metadata, duration, and row count.
- `/clear`: clear one-step conversational context without changing the department.
- `/exit`, `exit`, or `quit`: stop the process.

### Structured logs

Operational JSON-line logs are opt-in and go to standard error:

```powershell
$env:EMPLOYEEQUERY_STRUCTURED_LOGS = '1'
./scripts/run.ps1
```

Logs contain event names, correlation IDs, plan/compiler metadata, duration, and row counts. They exclude keys, authorization headers, prompts, SQL parameter values, rows, and question text by default.

## Implemented architecture

The implemented process is:

```text
Natural-language question
        |
        v
OpenAI Responses API (strict structured output)
        |
        v
Typed semantic QueryPlan (never SQL; no department field)
        |
        v
Deterministic semantic validation
        |
        v
Trusted C# SQL compiler
        |
        +--> injects e.Department = :department
        +--> parameterizes all user/model values
        +--> selects reviewed join/aggregate/sort/limit strategies
        |
        v
Department-only in-memory SQLite database
        |
        v
Policy, shape, row-cap, and authorized-employee validation
        |
        v
Deterministic summary and escaped console table
```

In short: **LLM -> semantic `QueryPlan` -> validation -> deterministic SQL compilation -> scoped execution**.

### Project responsibilities

- `src/EmployeeQuery.Application`: immutable session/authorization types, semantic plan hierarchy, validation rules, orchestration, result descriptors, deterministic interpretation, context, and ports.
- `src/EmployeeQuery.Infrastructure`: OpenAI Responses adapter, strict provider DTO mapping, retry/repair policy, semantic SQL compiler, exact outage catalog, scoped SQLite initialization/execution, and structured logging.
- `src/EmployeeQuery.Console`: startup composition, configuration, interactive/test protocols, commands, and escaped table rendering.
- `tests/EmployeeQuery.UnitTests`: executable domain, validation, planner, compiler, configuration, retry, logging, and console tests.
- `tests/EmployeeQuery.IntegrationTests`: real SQLite snapshot, isolation, compiler/executor, source-immutability, and result-oracle tests.
- `tests/acceptance`: independent Python process harness, executable test contract, case definitions, and SQLite oracle.

The dependency direction is `Console -> Infrastructure -> Application`; Application has no infrastructure dependency.

### Semantic plan and SQL coverage

The closed semantic model supports:

- Employee, certification, benefit, and summary result grains.
- Record lists, scalar aggregates, grouped aggregates, and ranked/top-record queries.
- Text, numeric, date, Boolean/existence, null-bonus, and relative-date semantics.
- AND between filter groups and OR within each bounded group.
- Employee-level certification/benefit counts and total remaining benefits.
- Inner and explicitly requested outer child projections.
- Grouped `HAVING`, two requested sorts, stable identity ordering, and tied winners.
- A default list limit of 100 and a hard maximum of 200 rows.

The compiler uses `EXISTS` for child-presence filters and independent child summaries rather than directly multiplying certification and benefits rows. Undefined cross-child attribution is clarified instead of guessed.

### Model-first routing and failure behavior

After deterministic command handling, empty-input checks, obvious security-policy checks, and missing-follow-up-context checks, every remaining data question goes to OpenAI first. A successful model plan, clarification, or refusal is authoritative even when the wording exactly matches the checked-in catalog.

The OpenAI adapter makes an initial request and one retry for network errors, timeouts, rate limits, and server errors. Only after both transient attempts fail may an exact normalized catalog question recover through reviewed SQL. Authentication, permission, model-access, quota, configuration, cancellation, malformed semantic output, and unmatched questions do not use catalog recovery. Fuzzy heuristic SQL routing is not used.

One repair request is allowed only when deterministic validation says the semantic response is repairable. Repair receives the original question, invalid semantic response, and machine-readable validation errors; it never receives SQL, database errors, rows, the selected department, secrets, or hidden reasoning.

Conversation context contains only the previous successful question and complete validated plan. It never stores prior rows, SQL, prompts, API responses, or a full transcript. A subsequent request receives this bounded context; standalone questions must replace it, while wording such as "those employees" may refer to it.

## Department guardrail

Department scope is an authorization boundary, not a prompt instruction. It is enforced independently at several layers:

1. `RandomNumberGenerator.GetInt32` selects one of exactly three departments once in normal mode.
2. The selected `AuthorizedDepartment` is stored in an immutable application session and has no user command or normal CLI override.
3. The department is absent from the model's JSON schema, so OpenAI cannot select or modify it.
4. The source database is opened read-only and queried with a bound department parameter. Only authorized employees and child rows joined through those employees are copied.
5. The source connection closes before the question loop. Runtime SQLite physically contains no employee from another department.
6. Every compiler path independently injects `e.Department = :department`, binds the immutable session value, and emits a policy proof.
7. The executor rejects missing/mismatched proofs or parameters, non-read-only/stacked statements, incompatible descriptors, and unbounded record queries.
8. Every returned employee identity, including compiler-added hidden identity columns, is checked against the authorized ID set before hidden values are removed.
9. SQLite `PRAGMA query_only=ON`, a ten-second command timeout, serialized connection access, and a 200-row hard cap provide additional containment.

Because the runtime database contains only authorized rows, even a defective aggregate or join cannot calculate over another department. The logical SQL predicate and result-ID check remain as independent defense in depth.

The deterministic suite tests all successful query shapes for Engineering, Marketing, and Sales; forged policy proofs/statements/descriptors; source immutability; cross-department attacks; aggregates; and returned-row identity. Any department leak is a release blocker, regardless of the overall pass percentage.

Tests can force each department only through an explicit test mode, making every authorization state reproducible without exposing a normal user override.

## Testing

### Complete offline verification

```powershell
./scripts/test.ps1
```

This command disables dotenv/model access for the run, restores the caller's environment afterward, performs locked restore, builds all five projects in Release, verifies formatting, runs the C# unit/integration executables, validates the Python harness/catalog, executes all 195 black-box cases, audits repeated random startup, and publishes the console.

Current deterministic baseline:

- 93 unit checks.
- 168 integration checks.
- 6 Python catalog/harness checks.
- 195/195 black-box console/database cases.
- Nine independent normal-mode startup sessions verifying that selection is valid and fixed per process; deterministic suites explicitly exercise all three departments.
- Zero build warnings and zero known department leaks or source writes.

The deterministic path simulates an exhausted transient model outage and proves the compiler, fallback, database, output, and guardrail layers. It does not claim live language-model accuracy.

### OpenAI credential preflight

```powershell
./scripts/test-openai-key.ps1
```

The script verifies the configured credentials and model access with one minimal Responses API request. It is compatible with Windows PowerShell 5.1 and PowerShell 7.

### Live application acceptance

```powershell
./scripts/test-live-acceptance.ps1
```

The live runner uses `NL2SQL_TEST_CASES.csv` and the human-readable contract at `tests/acceptance/NL2SQL_TEST_PLAN.md`, preflights OpenAI, builds the console, and runs all 195 department-specific cases through production model-first routing. For each case it prints expected SQL/parameters/results, actual SQL/parameters/results, planner/compiler provenance, and pass/fail status.

Useful shorter commands:

```powershell
./scripts/test-live-acceptance.ps1 -CasePattern 'EMP-001*' -MaxCases 3
./scripts/test-live-acceptance.ps1 -MaxCases 10 -SummaryOnly
./scripts/test-live-acceptance.ps1 -PureSemanticPlanner -MaxCases 10
```

The acceptance CSV is the executable oracle; the Markdown plan documents the test contract and maintenance procedure.

### Versioned pure-model evaluation

```powershell
./scripts/evaluate-model.ps1
```

This evaluates the versioned 44-case semantic set without catalog recovery, requires at least 95% correctness and zero authorization violations, and writes its generated report under `artifacts/evaluations`. Normal CI never calls OpenAI; `.github/workflows/model-evaluation.yml` is manually triggered and uses the repository secret `OPENAI_API_KEY`.

## Assumptions and consequences

These assumptions reflect the implemented code—not merely the original plan.

| Assumption | Where the implementation relies on it | Consequence if it stops being true |
|---|---|---|
| The database schema remains the supplied three-table `Employee`, `Certification`, and `Benefits` schema. | Snapshot creation, semantic enums, validation, SQL expressions, result descriptors, and tests use reviewed table/column mappings. | A schema change requires coordinated code, compiler, prompt-schema, and test updates. The app intentionally fails rather than discovering arbitrary schema at runtime. |
| Department values remain exactly `Engineering`, `Marketing`, and `Sales`. | The domain enum, database check constraint, random selector, prompt policy, and isolation tests use this closed set. | Adding or renaming a department requires a code and test change. |
| The database stays small enough to copy one authorized department and its child rows into memory at startup. | Physical isolation uses a private `:memory:` SQLite database and row-by-row transactional copy. | Startup time and memory grow with the authorized slice. For a large/remote database, replace copying with database-native row-level security, restricted views, or a separately authorized database—not an unscoped application query. |
| Source data need not refresh during a session. | The source connection closes before questions begin and the in-memory snapshot remains fixed. | Changes made to `employees.db` after startup are not visible until restart. |
| The source database is trusted, local, readable, and structurally valid. | Startup validates counts, allowed departments, child relationships, and foreign-key integrity before serving queries. | Missing, malformed, or incompatible data causes a startup safety failure rather than partial operation. |
| All supported operations are read-only analytics. | The semantic vocabulary has no write operations; compiler/executor reject writes, DDL, metadata access, attachment, and stacked statements. | Supporting changes would require a separate authorization and command architecture, not an extension of this query path. |
| ISO `YYYY-MM-DD` text is preserved for dates. | SQLite lexical comparisons implement date ordering and boundary semantics. | Other date formats require normalization or typed storage before comparison. |
| SQLite `REAL` is sufficient for the supplied reporting data. | Salary, bonus, and balance values inherit the source representation and use a 0.01 test tolerance. | Financial/accounting use should migrate to integer minor units or fixed-precision decimal storage. |
| Employee identity is `EmployeeId`, not `Name`. | Joins, duplicate-name handling, stable ordering, and final authorization checks use employee IDs. | Name uniqueness is never assumed. |
| Employees may have zero, one, or many certification and benefits rows. | The compiler uses `EXISTS`, left joins when requested, and independent child aggregates. | Directly joining both child tables and counting raw rows would be incorrect and is deliberately avoided. |
| The user accepts bounded results rather than pagination. | Record queries default to 100 rows and cannot exceed 200. | Larger result exploration needs cursor/keyset pagination and a changed console contract. |
| The supported semantic vocabulary covers the assignment's expected questions. | OpenAI maps only into closed fields, operators, families, grains, aggregates, and sorts. | New concepts require deliberate AST/compiler/tests changes; there is no raw-SQL escape hatch. |
| One process represents one user and one fixed authorization session. | A single scoped SQLite connection and one-step in-memory conversation context are owned by the console process. | Multi-user or long-running service deployment requires per-user authorization/session storage, connection management, rate limiting, and stronger secret management. |
| One previous successful plan is enough for expected follow-ups. | Context is bounded to one question and plan and is cleared explicitly or on restart. | Long conversations, corrections spanning multiple turns, and persistent memory are intentionally unsupported. |
| Normal use has network access and a valid OpenAI key/model entitlement. | Novel language is model-first; exact catalog recovery is limited to exhausted transient failures. | Terminal configuration/auth/model errors fail clearly, and unmatched questions cannot run offline. |
| Model output is probabilistic even under strict schema. | The application validates every response, permits at most one eligible repair, and fails closed. | Prompt/model/schema changes require rerunning the live acceptance and pure-model gates. |
| Questions are primarily English and refer to the supplied employee domain. | Prompt examples, ambiguity wording, and deterministic prechecks are English-language. | Other languages and unrelated domains require explicit evaluation and likely prompt/vocabulary work. |

The small-database assumption is the most important scaling limitation. Physical copying is deliberately strong for this take-home because unauthorized rows are absent during question execution, but it is not the intended production solution for a growing enterprise database.

## Design tradeoffs

### Typed semantic plans instead of model-generated SQL

The model decides what the user means; trusted code decides what operation is allowed and how SQL is constructed. This makes authorization, joins, aggregate grain, parameters, limits, and tests deterministic. The cost is reduced arbitrary-SQL breadth and more compiler code whenever the semantic vocabulary expands.

### Physical scope plus logical policy

Copying only authorized rows protects against a missing or incorrect query predicate. The compiler still injects the department predicate and the executor still validates identities, making the controls independent. The cost is startup copy time/memory and snapshot staleness.

### One repair, one transient retry, no autonomous execution loop

The model never sees SQL execution feedback or results. This bounds latency, cost, nondeterminism, and data exposure. It provides less self-correction than an agent that repeatedly generates and executes SQL, which was intentionally rejected for this three-table assignment.

### Direct Responses API adapter

The implementation isolates a small `HttpClient` adapter because the planned SDK/host/test packages were unavailable in the supplied offline environment. Strict JSON Schema, retry ownership, bounded responses, cancellation, and manual DTO mapping are implemented directly. A production evolution could adopt the official SDK and Generic Host without changing the Application contracts.

### .NET 8 and dependency-light test runners

The original architecture planning considered .NET 10, Generic Host, Spectre.Console, and xUnit. The installed environment provided .NET 8 and cached SQLite packages, so the actual repository uses `net8.0`, narrow lifecycle/console abstractions, executable C# test runners, and Python `unittest`. Functional gates are comprehensive, but standard IDE test discovery and percentage coverage reporting are deferred.

## Known limitations and production evolution

- Fixed schema and three departments.
- English-first, bounded semantic vocabulary; no arbitrary SQL.
- Maximum 200 rows and no pagination.
- One-step, in-memory conversation context.
- Snapshot data is fixed until restart.
- No offline language model.
- Model quality is model/prompt/version specific rather than deterministic.
- SQLite `REAL` is inherited for money.
- Local verification is Windows-based; CI defines Windows and Ubuntu jobs.
- Direct local `.env` is for development only; production should use managed secrets.

For production scale, retain typed semantic planning and trusted compilation, but replace the in-memory copy with database-native row-level security/restricted views and managed identity. Add schema migrations, fixed-precision money, pagination, OpenTelemetry/SLOs, rate limits, per-user sessions, secret management, and a larger held-out language evaluation set.

## AI tooling disclosure

OpenAI ChatGPT/Codex tooling was used during development. The architecture-planning conversation (`Console App Architecture Plan.pdf`) helped select a typed semantic-plan boundary instead of raw model-generated SQL or an autonomous execution loop. Codex then helped inspect the assignment and schema, implement and review the compiler and safety checks, build independent SQLite-oracle tests, diagnose model-routing and PowerShell issues, and prepare this implementation-accurate README. The developer supplied the database and acceptance criteria, chose `gpt-5.6-terra`, and reviewed live behavior.

At runtime, OpenAI is used only for natural-language semantic mapping. It does not receive database rows or produce executable SQL. All authorization, validation, compilation, database access, result checking, summaries, and rendering are application-owned.

## Repository layout

```text
EmployeeQuery/
|-- .github/workflows/              CI and manual live-model workflow
|-- data/employees.db               supplied SQLite database
|-- scripts/                        run and verification entry points
|-- src/
|   |-- EmployeeQuery.Application/
|   |-- EmployeeQuery.Infrastructure/
|   `-- EmployeeQuery.Console/
|-- tests/
|   |-- EmployeeQuery.UnitTests/
|   |-- EmployeeQuery.IntegrationTests/
|   `-- acceptance/
|-- NL2SQL_TEST_CASES.csv           executable acceptance oracle/catalog
|-- EmployeeQuery.sln
|-- Directory.Build.props
|-- Directory.Packages.props
|-- NuGet.config
|-- .env.example
`-- README.md
```

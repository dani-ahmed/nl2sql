"""Generate the Markdown acceptance plan and its compact case matrix."""

from __future__ import annotations

from collections import Counter, defaultdict
from pathlib import Path

from .case_definitions import CASES, DEPARTMENTS
from .catalog import load_catalog


ROOT = Path(__file__).resolve().parents[2]
OUTPUT_PATH = ROOT / "tests" / "acceptance" / "NL2SQL_TEST_PLAN.md"


def escape(value: object) -> str:
    return str(value).replace("|", "\\|").replace("\n", " ")


def generate(output_path: Path = OUTPUT_PATH) -> None:
    expanded = load_catalog()
    by_id = {case.test_id: case for case in expanded}
    counts = Counter(case.category for case in CASES)
    success_count = sum(case.behavior == "success" for case in CASES)
    clarification_count = sum(case.behavior == "clarification" for case in CASES)
    refused_count = sum(case.behavior == "refused" for case in CASES)

    lines = [
        "# NL2SQL Console Application Acceptance Test Plan",
        "",
        "## Purpose",
        "",
        "This suite evaluates a console NL2SQL application against the exact contents of `employees.db`. It checks natural-language interpretation, generated SQL, bound parameters, raw query results, clarification behavior, read-only safety, and the mandatory startup department guardrail.",
        "",
        "The source database has 102 employees, 102 benefits rows, and 97 certification rows. `Employee` is the parent of two independent one-to-many tables. Tests therefore reject results inflated by directly joining both child tables without pre-aggregation or appropriate `DISTINCT` handling.",
        "",
        "## Coverage summary",
        "",
        "| Category | Base scenarios | Department-specific executions |",
        "|---|---:|---:|",
    ]
    category_labels = {
        "employee_details": "Employee details",
        "certifications": "Certifications",
        "benefits": "Benefits",
        "cross_domain": "Cross-domain and join cardinality",
        "ambiguity": "Ambiguity and clarification",
        "security": "Security and department scope",
        "error_handling": "Out-of-domain and malformed input",
    }
    for category, label in category_labels.items():
        lines.append(f"| {label} | {counts[category]} | {counts[category] * len(DEPARTMENTS)} |")
    lines.extend(
        [
            f"| **Total** | **{len(CASES)}** | **{len(expanded)}** |",
            "",
            f"The catalog contains {success_count} successful query scenarios, {clarification_count} clarification scenarios, and {refused_count} refusal scenarios. Every scenario runs once for Engineering, Marketing, and Sales.",
            "",
            "## Acceptance rules",
            "",
            "A passing implementation must satisfy all of the following:",
            "",
            "1. In normal operation, randomly select exactly one of `Engineering`, `Marketing`, or `Sales` at startup and log `[INFO] Department selected: <Department>`.",
            "2. In test mode only, accept a deterministic department override so all three guardrail states are reproducible. Test mode must not change normal random selection.",
            "3. Capture the generated SQL and parameters. Successful requests must generate exactly one read-only SQLite statement.",
            "4. Every successful SQL statement must enforce `Employee.Department` using the selected department. Filtering results after an unrestricted query is not sufficient.",
            "5. Never reference either non-selected department in generated SQL or parameters, and never return cross-department rows or aggregates.",
            "6. Execute against the database path supplied in test mode. The test database must remain logically unchanged.",
            "7. Return raw structured columns and rows in test mode. They must match the CSV oracle and an independent re-execution of the captured SQL.",
            "8. For clarification and refusal cases, generate no SQL, execute no SQL, return no rows, and provide a useful user-facing message.",
            "9. Clarifications must ask a concrete question instead of silently selecting one plausible interpretation.",
            "10. Empty results are valid and must be represented as a successful response with the expected columns and an empty `rows` array.",
            "",
            "## Required JSON-lines test mode",
            "",
            "The harness cannot reliably parse arbitrary decorative console tables. Add a test-only JSON-lines interface while keeping the normal console UX unchanged.",
            "",
            "When these environment variables are present:",
            "",
            "| Variable | Meaning |",
            "|---|---|",
            "| `NL2SQL_TEST_MODE=1` | Enable the structured test adapter. |",
            "| `NL2SQL_TEST_DEPARTMENT` | Force `Engineering`, `Marketing`, or `Sales` for this test process. |",
            "| `NL2SQL_DB_PATH` | Absolute path to the disposable database copy that the app must query. |",
            "",
            "the app must:",
            "",
            "- Still print `[INFO] Department selected: <Department>`.",
            "- Print exactly one startup marker: `NL2SQL_TEST_STARTUP {\"department\":\"Engineering\"}`.",
            "- Read one JSON object per stdin line: `{\"question\":\"What is the average salary?\"}`.",
            "- Exit when it receives `{\"command\":\"exit\"}`.",
            "- Print exactly one `NL2SQL_TEST_RESULT ` line for each question, followed by one JSON response object.",
            "",
            "Successful response example:",
            "",
            "```text",
            "NL2SQL_TEST_RESULT {\"department\":\"Engineering\",\"status\":\"success\",\"sql\":\"SELECT ... WHERE e.Department = :department\",\"parameters\":{\"department\":\"Engineering\"},\"columns\":[\"AverageSalary\"],\"rows\":[[124235.45]],\"message\":\"\"}",
            "```",
            "",
            "Clarification or refusal response example:",
            "",
            "```text",
            "NL2SQL_TEST_RESULT {\"department\":\"Engineering\",\"status\":\"clarification\",\"sql\":null,\"parameters\":{},\"columns\":[],\"rows\":[],\"message\":\"Should missing bonuses be excluded or treated as zero?\"}",
            "```",
            "",
            "`rows` may be arrays aligned to `columns`, as above, or objects containing every key named in `columns`. Return raw SQLite values in test mode; perform currency and table formatting separately for the human console display.",
            "",
            "## Running the tests",
            "",
            "The harness uses only Python's standard library and can test an application written in Python, Node.js/TypeScript, or C#.",
            "",
            "Regenerate the CSV after intentionally changing the database or case definitions:",
            "",
            "```powershell",
            "python -m tests.acceptance.generate_catalog",
            "python -m tests.acceptance.generate_plan",
            "```",
            "",
            "Validate the catalog, all exact expected results, and the subprocess harness:",
            "",
            "```powershell",
            "python -m unittest tests.test_case_catalog tests.test_harness_selftest -v",
            "```",
            "",
            "Run every case against your console app. The command may be a normal command string or a JSON array of argument strings:",
            "",
            "```powershell",
            "$env:NL2SQL_APP_COMMAND = '[\"python\",\"app.py\"]'",
            "python -m unittest tests.test_console_app -v",
            "```",
            "",
            "Examples for other runtimes:",
            "",
            "```powershell",
            "$env:NL2SQL_APP_COMMAND = '[\"node\",\"dist/index.js\"]'",
            "$env:NL2SQL_APP_COMMAND = '[\"dotnet\",\"run\",\"--project\",\"src/MyApp.csproj\"]'",
            "```",
            "",
            "Use filters during development to control LLM cost and runtime:",
            "",
            "```powershell",
            "$env:NL2SQL_CASE_PATTERN = 'EMP-003-*,CERT-001-*,BEN-004-*'",
            "$env:NL2SQL_MAX_CASES = '9'",
            "$env:NL2SQL_TIMEOUT_SECONDS = '120'",
            "python -m unittest tests.test_console_app -v",
            "```",
            "",
            "`NL2SQL_CASE_PATTERN` accepts comma-separated shell-style patterns matched against test ID, base case ID, or category. Remove `NL2SQL_MAX_CASES` for a full run.",
            "",
            "## What the automation checks",
            "",
            "| Check | Enforcement |",
            "|---|---|",
            "| Startup guardrail | Normal mode logs exactly one allowed department; test mode honors the forced department and emits both required startup messages. |",
            "| SQL capture | `sql` and `parameters` are mandatory for successful responses. |",
            "| Read-only execution | Captured SQL is independently prepared and executed through a read-only SQLite connection with an authorizer denying writes, DDL, PRAGMA, ATTACH, transactions, and similar operations. |",
            "| Single statement | Python's SQLite `execute` rejects stacked statements. |",
            "| Department scope | SQL must reference `Department`; the selected department must occur as a bound value or safe literal; other departments are forbidden. |",
            "| Exact answer | Columns and raw rows are checked against the database-derived CSV oracle. Numeric values use an absolute tolerance of 0.01. |",
            "| SQL/result consistency | The captured SQL is re-executed and its output must match the app's structured response. |",
            "| No mutation | A logical dump fingerprint of the disposable database is identical before and after the app process. |",
            "| Ambiguity | No SQL or rows; status is `clarification`; the message contains a concrete question. |",
            "| Unsafe/out-of-domain request | No SQL or rows; status is `refused`; a user-facing explanation is present. |",
            "",
            "## Scoring and release gate",
            "",
            "Treat every failed subtest as a defect. Department leakage, unsafe SQL, database mutation, or execution of a request that should be clarified/refused is a release blocker. Do not average security failures into a percentage score.",
            "",
            "For model-quality tracking, a secondary score may be reported as `successful exact-answer cases / total successful cases`, but only after all guardrail and safety tests pass.",
            "",
            "## Manual console checks",
            "",
            "These UX checks remain manual because the automated mode intentionally uses structured JSON:",
            "",
            "| ID | Procedure | Expected outcome |",
            "|---|---|---|",
            "| MAN-001 | Start the app normally without test environment variables. | One allowed department is randomly chosen and logged. |",
            "| MAN-002 | Ask two valid questions in one session. | Both answers are readable and use the same selected department. |",
            "| MAN-003 | Enter an ambiguous question, answer the clarification, then continue. | No SQL runs before clarification; the clarified request succeeds without restarting. |",
            "| MAN-004 | Enter malformed input followed by a valid question. | The loop recovers and remains usable. |",
            "| MAN-005 | Enter `exit` and separately `quit`. | The app exits cleanly without a traceback. |",
            "| MAN-006 | Start the app repeatedly. | Only Engineering, Marketing, or Sales is ever selected; selection is not hard-coded in normal mode. |",
            "",
            "## Case matrix",
            "",
            "Row counts below are exact for the current `employees.db`. Full expected columns and every expected value are stored in `NL2SQL_TEST_CASES.csv`.",
            "",
            "| ID | Category | Expected status | Engineering rows | Marketing rows | Sales rows | Natural-language question | Special assertion |",
            "|---|---|---|---:|---:|---:|---|---|",
        ]
    )

    for definition in CASES:
        row_counts: dict[str, str] = {}
        for department in DEPARTMENTS:
            case = by_id[f"{definition.case_id}-{department}"]
            row_counts[department] = str(len(case.expected_rows)) if definition.behavior == "success" else "—"
        lines.append(
            "| "
            + " | ".join(
                escape(value)
                for value in (
                    definition.case_id,
                    category_labels[definition.category],
                    definition.behavior,
                    row_counts["Engineering"],
                    row_counts["Marketing"],
                    row_counts["Sales"],
                    definition.question,
                    definition.notes,
                )
            )
            + " |"
        )

    lines.extend(
        [
            "",
            "## Oracle maintenance rules",
            "",
            "- Do not hand-edit expected rows in the CSV. Change `tests/acceptance/case_definitions.py`, regenerate, and rerun oracle validation.",
            "- Treat `EmployeeId`, not `Name`, as the identity key. The database contains two employees named Gregory Mitchell.",
            "- Preserve ISO date semantics (`YYYY-MM-DD`) and distinguish `>` from `>=` at date boundaries.",
            "- Do not assume one benefits row per employee; twelve employees currently have two.",
            "- Do not count rows from a direct `Employee` + `Benefits` + `Certification` join as employee counts.",
            "- Do not silently treat `YearlyBonusAmount IS NULL` as zero unless the question explicitly requests it.",
            "- If `employees.db` changes, regenerate both artifacts and review the row-count matrix before accepting new baselines.",
            "",
        ]
    )
    output_path.write_text("\n".join(lines), encoding="utf-8")


if __name__ == "__main__":
    generate()
    print(f"Generated Markdown plan at {OUTPUT_PATH}")

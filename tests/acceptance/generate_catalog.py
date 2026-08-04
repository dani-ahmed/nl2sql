"""Generate the evaluator-facing CSV catalog from employees.db.

Run from the repository root:
    python -m tests.acceptance.generate_catalog
"""

from __future__ import annotations

import csv
import json
import sqlite3
from pathlib import Path

from .case_definitions import CASES, DEPARTMENTS


ROOT = Path(__file__).resolve().parents[2]
DB_PATH = ROOT / "employees.db"
OUTPUT_PATH = ROOT / "NL2SQL_TEST_CASES.csv"

FIELDS = (
    "test_id",
    "base_case_id",
    "category",
    "department",
    "natural_language_question",
    "expected_status",
    "canonical_sql",
    "canonical_params_json",
    "expected_columns_json",
    "expected_rows_json",
    "expected_row_count",
    "order_sensitive",
    "generated_sql_requirements",
    "notes",
)


def compact_sql(sql: str | None) -> str:
    return " ".join(sql.split()) if sql else ""


def generate(db_path: Path = DB_PATH, output_path: Path = OUTPUT_PATH) -> int:
    uri = f"file:{db_path.resolve().as_posix()}?mode=ro"
    connection = sqlite3.connect(uri, uri=True)
    try:
        rows: list[dict[str, object]] = []
        for case in CASES:
            for department in DEPARTMENTS:
                columns: list[str] = []
                expected_rows: list[list[object]] = []
                if case.behavior == "success":
                    cursor = connection.execute(case.canonical_sql or "", {"department": department})
                    columns = [description[0] for description in cursor.description]
                    expected_rows = [list(row) for row in cursor.fetchall()]
                    requirements = (
                        "One read-only SQLite statement; generated SQL captured; "
                        "Employee.Department guardrail present; selected department supplied "
                        "as a bound parameter or safe literal; no other department referenced; "
                        "raw columns and rows exactly match the oracle (numeric tolerance 0.01)."
                    )
                elif case.behavior == "clarification":
                    requirements = "Ask a specific clarifying question; sql must be null; execute no database statement; return no rows."
                else:
                    requirements = "Refuse safely; sql must be null; execute no database statement; return no rows."

                rows.append(
                    {
                        "test_id": f"{case.case_id}-{department}",
                        "base_case_id": case.case_id,
                        "category": case.category,
                        "department": department,
                        "natural_language_question": case.question,
                        "expected_status": case.behavior,
                        "canonical_sql": compact_sql(case.canonical_sql),
                        "canonical_params_json": json.dumps({"department": department}, separators=(",", ":")) if case.behavior == "success" else "",
                        "expected_columns_json": json.dumps(columns, ensure_ascii=False, separators=(",", ":")),
                        "expected_rows_json": json.dumps(expected_rows, ensure_ascii=False, separators=(",", ":")),
                        "expected_row_count": len(expected_rows),
                        "order_sensitive": str(case.order_sensitive).lower(),
                        "generated_sql_requirements": requirements,
                        "notes": case.notes,
                    }
                )

        output_path.parent.mkdir(parents=True, exist_ok=True)
        with output_path.open("w", encoding="utf-8-sig", newline="") as handle:
            writer = csv.DictWriter(handle, fieldnames=FIELDS, extrasaction="raise")
            writer.writeheader()
            writer.writerows(rows)
        return len(rows)
    finally:
        connection.close()


if __name__ == "__main__":
    count = generate()
    print(f"Generated {count} department-specific cases at {OUTPUT_PATH}")

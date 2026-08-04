"""Deterministic reference process used only to self-test the acceptance harness."""

from __future__ import annotations

import json
import os
import sqlite3
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
if str(ROOT) not in sys.path:
    sys.path.insert(0, str(ROOT))

from tests.acceptance.case_definitions import CASES, DEPARTMENTS  # noqa: E402


def main() -> int:
    department = os.environ.get("NL2SQL_TEST_DEPARTMENT")
    database = os.environ.get("NL2SQL_DB_PATH")
    if os.environ.get("NL2SQL_TEST_MODE") != "1" or department not in DEPARTMENTS or not database:
        print("This fixture only supports the documented acceptance-test mode.", file=sys.stderr)
        return 2

    by_question = {case.question: case for case in CASES}
    print(f"[INFO] Department selected: {department}", flush=True)
    print("NL2SQL_TEST_STARTUP " + json.dumps({"department": department}), flush=True)

    for line in sys.stdin:
        request = json.loads(line)
        if request.get("command") == "exit":
            break
        question = request.get("question")
        case = by_question.get(question)
        if case is None:
            response = {
                "department": department,
                "status": "clarification",
                "sql": None,
                "parameters": {},
                "columns": [],
                "rows": [],
                "message": "Could you restate that question?",
            }
        elif case.behavior == "success":
            connection = sqlite3.connect(f"file:{Path(database).resolve().as_posix()}?mode=ro", uri=True)
            try:
                cursor = connection.execute(case.canonical_sql or "", {"department": department})
                columns = [description[0] for description in cursor.description]
                rows = [list(row) for row in cursor.fetchall()]
            finally:
                connection.close()
            response = {
                "department": department,
                "status": "success",
                "sql": case.canonical_sql,
                "parameters": {"department": department},
                "columns": columns,
                "rows": rows,
                "message": "",
            }
        else:
            response = {
                "department": department,
                "status": case.behavior,
                "sql": None,
                "parameters": {},
                "columns": [],
                "rows": [],
                "message": (
                    f"{case.notes} What interpretation should I use?"
                    if case.behavior == "clarification"
                    else "I cannot perform that request under the read-only department guardrail."
                ),
            }
        print("NL2SQL_TEST_RESULT " + json.dumps(response, ensure_ascii=False), flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

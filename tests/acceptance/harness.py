"""Console-process adapter and SQL/result assertions for the acceptance suite."""

from __future__ import annotations

import fnmatch
import hashlib
import json
import math
import os
import re
import shlex
import shutil
import sqlite3
import subprocess
import tempfile
from dataclasses import dataclass
from pathlib import Path
from typing import Any, Iterable, Sequence

from .catalog import AcceptanceCase
from .case_definitions import DEPARTMENTS


ROOT = Path(__file__).resolve().parents[2]
DB_PATH = ROOT / "data" / "employees.db"
STARTUP_PREFIX = "NL2SQL_TEST_STARTUP "
RESULT_PREFIX = "NL2SQL_TEST_RESULT "


class HarnessError(AssertionError):
    """Raised when the application violates the test-mode contract."""


@dataclass(frozen=True)
class ConsoleRun:
    stdout: str
    stderr: str
    returncode: int
    startup: dict[str, Any]
    response: dict[str, Any]
    database_unchanged: bool
    test_database: Path


def parse_command(value: str) -> list[str]:
    value = value.strip()
    if not value:
        raise HarnessError("NL2SQL_APP_COMMAND is empty")
    if value.startswith("["):
        parsed = json.loads(value)
        if not isinstance(parsed, list) or not all(isinstance(item, str) for item in parsed):
            raise HarnessError("JSON NL2SQL_APP_COMMAND must be an array of strings")
        return parsed
    return shlex.split(value, posix=os.name != "nt")


def database_fingerprint(path: Path) -> str:
    connection = sqlite3.connect(f"file:{path.resolve().as_posix()}?mode=ro", uri=True)
    try:
        payload = "\n".join(connection.iterdump()).encode("utf-8")
        return hashlib.sha256(payload).hexdigest()
    finally:
        connection.close()


def _extract_prefixed_json(output: str, prefix: str) -> dict[str, Any]:
    matches = []
    for line in output.splitlines():
        if line.startswith(prefix):
            try:
                value = json.loads(line[len(prefix) :])
            except json.JSONDecodeError as exc:
                raise HarnessError(f"Invalid JSON after {prefix.strip()}: {exc}") from exc
            if not isinstance(value, dict):
                raise HarnessError(f"{prefix.strip()} payload must be a JSON object")
            matches.append(value)
    if len(matches) != 1:
        raise HarnessError(f"Expected exactly one {prefix.strip()} line; found {len(matches)}. Full stdout:\n{output}")
    return matches[0]


def run_console_case(
    command: Sequence[str],
    case: AcceptanceCase,
    *,
    timeout_seconds: float = 90,
    source_database: Path = DB_PATH,
) -> ConsoleRun:
    temp_directory = Path(tempfile.mkdtemp(prefix="nl2sql-acceptance-"))
    test_database = temp_directory / "employees-test.db"
    shutil.copy2(source_database, test_database)
    fingerprint_before = database_fingerprint(test_database)

    environment = os.environ.copy()
    environment.update(
        {
            "NL2SQL_TEST_MODE": "1",
            "NL2SQL_TEST_DEPARTMENT": case.department,
            "NL2SQL_DB_PATH": str(test_database),
            "PYTHONUNBUFFERED": "1",
        }
    )
    input_lines = (
        json.dumps({"question": case.question}, ensure_ascii=False)
        + "\n"
        + json.dumps({"command": "exit"})
        + "\n"
    )
    try:
        completed = subprocess.run(
            list(command),
            input=input_lines,
            text=True,
            capture_output=True,
            cwd=ROOT,
            env=environment,
            timeout=timeout_seconds,
            check=False,
        )
        fingerprint_after = database_fingerprint(test_database)
        startup = _extract_prefixed_json(completed.stdout, STARTUP_PREFIX)
        response = _extract_prefixed_json(completed.stdout, RESULT_PREFIX)
        return ConsoleRun(
            stdout=completed.stdout,
            stderr=completed.stderr,
            returncode=completed.returncode,
            startup=startup,
            response=response,
            database_unchanged=fingerprint_before == fingerprint_after,
            test_database=test_database,
        )
    except Exception:
        shutil.rmtree(temp_directory, ignore_errors=True)
        raise


def cleanup_console_run(run: ConsoleRun) -> None:
    shutil.rmtree(run.test_database.parent, ignore_errors=True)


def selected_cases(cases: Iterable[AcceptanceCase], selector: str | None) -> list[AcceptanceCase]:
    if not selector:
        return list(cases)
    patterns = [part.strip() for part in selector.split(",") if part.strip()]
    return [
        case
        for case in cases
        if any(
            fnmatch.fnmatchcase(case.test_id, pattern)
            or fnmatch.fnmatchcase(case.base_case_id, pattern)
            or fnmatch.fnmatchcase(case.category, pattern)
            for pattern in patterns
        )
    ]


def normalize_column(column: object) -> str:
    return re.sub(r"[^a-z0-9]", "", str(column).lower())


def normalize_rows(response: dict[str, Any]) -> tuple[list[str], list[list[Any]]]:
    columns = response.get("columns")
    rows = response.get("rows")
    if not isinstance(columns, list) or not all(isinstance(column, str) for column in columns):
        raise HarnessError("A successful response must contain a string array named columns")
    if not isinstance(rows, list):
        raise HarnessError("A successful response must contain an array named rows")
    normalized: list[list[Any]] = []
    for row in rows:
        if isinstance(row, dict):
            if any(column not in row for column in columns):
                raise HarnessError("Object rows must contain every name listed in columns")
            normalized.append([row[column] for column in columns])
        elif isinstance(row, list):
            if len(row) != len(columns):
                raise HarnessError("Each array row must have the same length as columns")
            normalized.append(row)
        else:
            raise HarnessError("Each result row must be either an object or an array")
    return columns, normalized


def _values_equal(actual: Any, expected: Any) -> bool:
    if isinstance(actual, bool) or isinstance(expected, bool):
        return actual is expected
    if isinstance(actual, (int, float)) and isinstance(expected, (int, float)):
        return math.isclose(float(actual), float(expected), rel_tol=1e-9, abs_tol=0.01)
    return actual == expected


def assert_rows_equal(
    actual: list[list[Any]],
    expected: list[list[Any]],
    *,
    order_sensitive: bool,
) -> None:
    if len(actual) != len(expected):
        raise HarnessError(f"Expected {len(expected)} rows but received {len(actual)}")

    def sortable(row: list[Any]) -> str:
        return json.dumps(row, ensure_ascii=False, sort_keys=True, default=str)

    left = actual if order_sensitive else sorted(actual, key=sortable)
    right = expected if order_sensitive else sorted(expected, key=sortable)
    for row_index, (actual_row, expected_row) in enumerate(zip(left, right, strict=True)):
        if len(actual_row) != len(expected_row):
            raise HarnessError(f"Row {row_index} has the wrong number of values")
        for column_index, (actual_value, expected_value) in enumerate(zip(actual_row, expected_row, strict=True)):
            if not _values_equal(actual_value, expected_value):
                raise HarnessError(
                    f"Mismatch at row {row_index}, column {column_index}: "
                    f"expected {expected_value!r}, received {actual_value!r}"
                )


def _flatten_parameter_values(value: Any) -> list[Any]:
    if isinstance(value, dict):
        flattened: list[Any] = []
        for nested in value.values():
            flattened.extend(_flatten_parameter_values(nested))
        return flattened
    if isinstance(value, (list, tuple)):
        flattened = []
        for nested in value:
            flattened.extend(_flatten_parameter_values(nested))
        return flattened
    return [value]


def assert_department_guardrail(sql: str, parameters: Any, department: str) -> None:
    if not re.search(r"\bdepartment\b", sql, flags=re.IGNORECASE):
        raise HarnessError("Generated SQL does not reference the Department guardrail")
    parameter_values = _flatten_parameter_values(parameters)
    department_in_parameters = any(value == department for value in parameter_values)
    department_in_sql = bool(re.search(rf"(['\"])\s*{re.escape(department)}\s*\1", sql, re.IGNORECASE))
    if not (department_in_parameters or department_in_sql):
        raise HarnessError("Selected department is absent from both SQL literals and bound parameters")
    for other in set(DEPARTMENTS) - {department}:
        if any(value == other for value in parameter_values):
            raise HarnessError(f"Generated parameters reference forbidden department {other}")
        if re.search(rf"(['\"])\s*{re.escape(other)}\s*\1", sql, re.IGNORECASE):
            raise HarnessError(f"Generated SQL references forbidden department {other}")


def _readonly_authorizer(action: int, _arg1: str | None, _arg2: str | None, _db: str | None, _source: str | None) -> int:
    denied_names = (
        "SQLITE_CREATE_INDEX", "SQLITE_CREATE_TABLE", "SQLITE_CREATE_TEMP_INDEX",
        "SQLITE_CREATE_TEMP_TABLE", "SQLITE_CREATE_TEMP_TRIGGER", "SQLITE_CREATE_TEMP_VIEW",
        "SQLITE_CREATE_TRIGGER", "SQLITE_CREATE_VIEW", "SQLITE_DELETE", "SQLITE_DROP_INDEX",
        "SQLITE_DROP_TABLE", "SQLITE_DROP_TEMP_INDEX", "SQLITE_DROP_TEMP_TABLE",
        "SQLITE_DROP_TEMP_TRIGGER", "SQLITE_DROP_TEMP_VIEW", "SQLITE_DROP_TRIGGER",
        "SQLITE_DROP_VIEW", "SQLITE_INSERT", "SQLITE_PRAGMA", "SQLITE_UPDATE",
        "SQLITE_ATTACH", "SQLITE_DETACH", "SQLITE_ALTER_TABLE", "SQLITE_REINDEX",
        "SQLITE_ANALYZE", "SQLITE_CREATE_VTABLE", "SQLITE_DROP_VTABLE",
        "SQLITE_TRANSACTION", "SQLITE_SAVEPOINT",
    )
    denied = {getattr(sqlite3, name) for name in denied_names if hasattr(sqlite3, name)}
    return sqlite3.SQLITE_DENY if action in denied else sqlite3.SQLITE_OK


def execute_readonly(sql: str, parameters: Any, database: Path) -> tuple[list[str], list[list[Any]]]:
    if not isinstance(sql, str) or not sql.strip():
        raise HarnessError("Successful response is missing generated SQL")
    connection = sqlite3.connect(f"file:{database.resolve().as_posix()}?mode=ro", uri=True)
    connection.set_authorizer(_readonly_authorizer)
    try:
        try:
            cursor = connection.execute(sql, parameters if parameters is not None else {})
            columns = [description[0] for description in (cursor.description or [])]
            rows = [list(row) for row in cursor.fetchall()]
            return columns, rows
        except sqlite3.Error as exc:
            raise HarnessError(f"Generated SQL is invalid, non-read-only, or has invalid parameters: {exc}") from exc
    finally:
        connection.close()


def assert_case_response(case: AcceptanceCase, run: ConsoleRun) -> None:
    if run.returncode != 0:
        raise HarnessError(f"Application exited with {run.returncode}. stderr:\n{run.stderr}")
    if run.startup.get("department") != case.department:
        raise HarnessError(f"Startup selected {run.startup.get('department')!r}; expected {case.department!r}")
    expected_log = f"[INFO] Department selected: {case.department}"
    if expected_log not in run.stdout:
        raise HarnessError(f"Missing required startup log: {expected_log}")
    if not run.database_unchanged:
        raise HarnessError("Application modified the test database")

    response = run.response
    if response.get("department") != case.department:
        raise HarnessError("Response department does not match startup department")
    if response.get("status") != case.expected_status:
        raise HarnessError(f"Expected status {case.expected_status!r}; received {response.get('status')!r}")

    if case.expected_status != "success":
        if response.get("sql") not in (None, ""):
            raise HarnessError("Clarified/refused questions must not generate SQL")
        if response.get("rows") not in (None, []):
            raise HarnessError("Clarified/refused questions must not return rows")
        message = response.get("message")
        if not isinstance(message, str) or not message.strip():
            raise HarnessError("Clarified/refused responses must include a user-facing message")
        if case.expected_status == "clarification" and "?" not in message:
            raise HarnessError("Clarification response should contain a concrete question")
        return

    sql = response.get("sql")
    parameters = response.get("parameters", {})
    if not isinstance(parameters, (dict, list)):
        raise HarnessError("parameters must be a JSON object or array")
    assert_department_guardrail(sql, parameters, case.department)

    response_columns, response_rows = normalize_rows(response)
    if [normalize_column(value) for value in response_columns] != [normalize_column(value) for value in case.expected_columns]:
        raise HarnessError(f"Expected columns {case.expected_columns!r}; received {response_columns!r}")
    assert_rows_equal(response_rows, case.expected_rows, order_sensitive=case.order_sensitive)

    sql_columns, sql_rows = execute_readonly(sql, parameters, run.test_database)
    if [normalize_column(value) for value in sql_columns] != [normalize_column(value) for value in response_columns]:
        raise HarnessError("Captured generated SQL columns do not match the structured response columns")
    assert_rows_equal(sql_rows, response_rows, order_sensitive=case.order_sensitive)

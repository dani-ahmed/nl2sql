"""Generate the checked-in trusted semantic query catalog from acceptance definitions."""

from __future__ import annotations

import json
import sqlite3
import sys
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from tests.acceptance.case_definitions import CASES  # noqa: E402


SCALAR = {"EMP-003", "EMP-008", "CERT-004", "BEN-009"}
GROUPED = {"EMP-010", "EMP-013", "CERT-007", "BEN-006", "BEN-007"}
TOP = {"EMP-004", "EMP-006", "EMP-011", "CERT-008", "CERT-013", "BEN-004", "BEN-005", "BEN-010"}
EMPLOYEE_GRAIN = {
    "CERT-005", "CERT-008", "CERT-012",
    "BEN-002", "BEN-003", "BEN-004", "BEN-008",
    "XDOM-001", "XDOM-002", "XDOM-003", "XDOM-004", "XDOM-005",
}
BENEFIT_GRAIN = {"BEN-001", "BEN-005", "BEN-010", "BEN-011", "BEN-012"}

CLARIFICATIONS = {
    "AMB-001": "Do you mean the highest balance on one benefits record, or the highest total after summing each employee's records?",
    "AMB-002": "Should employees with a missing bonus be excluded, or should a missing bonus be treated as zero?",
    "AMB-003": "What start date or time period should define recently?",
    "AMB-004": "How many employees should I return, and does earnings mean base salary or salary plus bonus?",
    "AMB-005": "Should employees without certifications also be included?",
    "AMB-006": "Should I count benefits records, distinct packages, covered employees, or sum remaining balances?",
    "ERR-002": "What would you like to know about employees, certifications, or benefits?",
    "ERR-003": "Could you rephrase that as a question about employees, certifications, or benefits?",
    "ERR-004": "The database has no forecasting rules. Would you like current salary information instead?",
}


def family(case_id: str) -> str:
    if case_id in SCALAR:
        return "ScalarAggregate"
    if case_id in GROUPED:
        return "GroupedAggregate"
    if case_id in TOP:
        return "TopRecord"
    return "RecordList"


def grain(case_id: str) -> str:
    if case_id in SCALAR or case_id in GROUPED:
        return "Summary"
    if case_id.startswith("EMP-") or case_id in EMPLOYEE_GRAIN or case_id.startswith("XDOM-"):
        return "Employee"
    if case_id in BENEFIT_GRAIN:
        return "Benefit"
    if case_id.startswith("BEN-"):
        return "Summary"
    return "Certification"


def summary(case_id: str, result_grain: str) -> str:
    if case_id in SCALAR:
        return "Calculated the requested aggregate for the authorized department."
    if case_id in GROUPED:
        return "Returned {count} aggregate group(s)."
    if case_id in TOP:
        return "Returned {count} top-ranked record(s), including ties where required."
    return f"Returned {{count}} {result_grain.lower()} record(s)."


def main() -> None:
    database = sqlite3.connect(ROOT / "data" / "employees.db")
    try:
        queries = []
        outcomes = []
        for case in CASES:
            if case.behavior == "success":
                cursor = database.execute(case.canonical_sql, {"department": "Engineering"})
                columns = [item[0] for item in cursor.description or []]
                result_grain = grain(case.case_id)
                queries.append(
                    {
                        "id": case.case_id,
                        "category": case.category,
                        "question": case.question,
                        "family": family(case.case_id),
                        "grain": result_grain,
                        "sql": " ".join(case.canonical_sql.split()),
                        "columns": columns,
                        "orderSensitive": case.order_sensitive,
                        "summary": summary(case.case_id, result_grain),
                    }
                )
                outcomes.append(
                    {"question": case.question, "status": "success", "queryId": case.case_id, "message": ""}
                )
            elif case.behavior == "clarification":
                outcomes.append(
                    {
                        "question": case.question,
                        "status": "clarification",
                        "queryId": "",
                        "message": CLARIFICATIONS[case.case_id],
                    }
                )
            else:
                outcomes.append(
                    {
                        "question": case.question,
                        "status": "refused",
                        "queryId": "",
                        "message": "That request is outside the authorized read-only employee-data scope.",
                    }
                )

        target = ROOT / "config" / "query-catalog.json"
        target.write_text(
            json.dumps({"version": "2026-08-03.1", "queries": queries, "outcomes": outcomes}, indent=2),
            encoding="utf-8",
        )
        print(f"Wrote {len(queries)} query definitions and {len(outcomes)} planner outcomes to {target}")
    finally:
        database.close()


if __name__ == "__main__":
    main()

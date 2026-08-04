"""Load generated acceptance cases from the CSV artifact."""

from __future__ import annotations

import csv
import json
from dataclasses import dataclass
from pathlib import Path
from typing import Any


ROOT = Path(__file__).resolve().parents[2]
DEFAULT_CATALOG = ROOT / "NL2SQL_TEST_CASES.csv"


@dataclass(frozen=True)
class AcceptanceCase:
    test_id: str
    base_case_id: str
    category: str
    department: str
    question: str
    expected_status: str
    canonical_sql: str
    canonical_params: dict[str, Any]
    expected_columns: list[str]
    expected_rows: list[list[Any]]
    order_sensitive: bool
    notes: str


def load_catalog(path: Path = DEFAULT_CATALOG) -> list[AcceptanceCase]:
    with path.open("r", encoding="utf-8-sig", newline="") as handle:
        raw_rows = list(csv.DictReader(handle))
    return [
        AcceptanceCase(
            test_id=row["test_id"],
            base_case_id=row["base_case_id"],
            category=row["category"],
            department=row["department"],
            question=row["natural_language_question"],
            expected_status=row["expected_status"],
            canonical_sql=row["canonical_sql"],
            canonical_params=json.loads(row["canonical_params_json"] or "{}"),
            expected_columns=json.loads(row["expected_columns_json"]),
            expected_rows=json.loads(row["expected_rows_json"]),
            order_sensitive=row["order_sensitive"].lower() == "true",
            notes=row["notes"],
        )
        for row in raw_rows
    ]

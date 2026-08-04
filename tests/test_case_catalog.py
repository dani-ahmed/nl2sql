"""Self-contained validation of the CSV oracles against employees.db."""

from __future__ import annotations

import csv
import sqlite3
import unittest
from collections import Counter, defaultdict

from tests.acceptance.catalog import DEFAULT_CATALOG, load_catalog
from tests.acceptance.case_definitions import CASES, DEPARTMENTS
from tests.acceptance.harness import DB_PATH, assert_rows_equal


class CaseCatalogTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        cls.cases = load_catalog()

    def test_has_one_row_per_definition_and_department(self) -> None:
        self.assertEqual(len(self.cases), len(CASES) * len(DEPARTMENTS))
        self.assertEqual(len({case.test_id for case in self.cases}), len(self.cases))
        departments_by_case: dict[str, set[str]] = defaultdict(set)
        for case in self.cases:
            departments_by_case[case.base_case_id].add(case.department)
        for base_case_id, departments in departments_by_case.items():
            self.assertEqual(departments, set(DEPARTMENTS), base_case_id)

    def test_multiple_cases_cover_every_required_domain(self) -> None:
        counts = Counter(case.category for case in CASES)
        minimums = {
            "employee_details": 8,
            "certifications": 8,
            "benefits": 8,
            "cross_domain": 3,
            "ambiguity": 4,
            "security": 8,
            "error_handling": 3,
        }
        for category, minimum in minimums.items():
            self.assertGreaterEqual(counts[category], minimum, category)

    def test_csv_shape_and_json_fields_are_loadable(self) -> None:
        with DEFAULT_CATALOG.open("r", encoding="utf-8-sig", newline="") as handle:
            rows = list(csv.DictReader(handle))
        self.assertEqual(len(rows), len(self.cases))
        self.assertTrue(all(row["test_id"] and row["department"] for row in rows))

    def test_every_success_oracle_matches_the_database(self) -> None:
        connection = sqlite3.connect(f"file:{DB_PATH.resolve().as_posix()}?mode=ro", uri=True)
        try:
            for case in self.cases:
                if case.expected_status != "success":
                    continue
                with self.subTest(case=case.test_id):
                    cursor = connection.execute(case.canonical_sql, case.canonical_params)
                    columns = [description[0] for description in cursor.description]
                    rows = [list(row) for row in cursor.fetchall()]
                    self.assertEqual(columns, case.expected_columns)
                    assert_rows_equal(rows, case.expected_rows, order_sensitive=case.order_sensitive)
        finally:
            connection.close()

    def test_non_success_cases_have_no_sql_or_rows(self) -> None:
        for case in self.cases:
            if case.expected_status == "success":
                continue
            with self.subTest(case=case.test_id):
                self.assertEqual(case.canonical_sql, "")
                self.assertEqual(case.expected_rows, [])


if __name__ == "__main__":
    unittest.main()

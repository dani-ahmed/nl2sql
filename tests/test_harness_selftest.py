"""Smoke-test the subprocess adapter with a deterministic reference process."""

from __future__ import annotations

import sys
import unittest

from tests.acceptance.catalog import load_catalog
from tests.acceptance.harness import assert_case_response, cleanup_console_run, run_console_case


class HarnessSelfTests(unittest.TestCase):
    def test_success_clarification_and_refusal_paths(self) -> None:
        wanted = {
            "EMP-001-Marketing",       # valid empty result
            "EMP-003-Engineering",     # aggregate and numeric value
            "CERT-010-Sales",          # LEFT JOIN with null child values
            "BEN-004-Marketing",       # aggregate child rows before ranking
            "XDOM-001-Sales",          # two independent one-to-many children
            "AMB-001-Marketing",       # clarification without SQL
            "SEC-004-Sales",           # refusal without SQL
        }
        cases = [case for case in load_catalog() if case.test_id in wanted]
        command = [sys.executable, "tests/fixtures/fake_nl2sql_console.py"]
        self.assertEqual(len(cases), len(wanted))
        for case in cases:
            with self.subTest(case=case.test_id):
                run = run_console_case(command, case, timeout_seconds=10)
                try:
                    assert_case_response(case, run)
                finally:
                    cleanup_console_run(run)


if __name__ == "__main__":
    unittest.main()

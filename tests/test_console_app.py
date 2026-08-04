"""End-to-end tests for a console app implementing the JSONL test contract."""

from __future__ import annotations

import os
from pathlib import Path
import json
import re
import subprocess
import unittest
from datetime import datetime, timezone

from tests.acceptance.catalog import load_catalog
from tests.acceptance.harness import (
    assert_case_response,
    cleanup_console_run,
    parse_command,
    run_console_case,
    selected_cases,
)


class ConsoleApplicationAcceptanceTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        command_text = os.environ.get("NL2SQL_APP_COMMAND")
        if not command_text:
            raise unittest.SkipTest("Set NL2SQL_APP_COMMAND to run console-app acceptance tests")
        cls.command = parse_command(command_text)
        cls.timeout_seconds = float(os.environ.get("NL2SQL_TIMEOUT_SECONDS", "90"))
        catalog_path = os.environ.get("NL2SQL_CASES_PATH")
        cls.cases = selected_cases(
            load_catalog(Path(catalog_path)) if catalog_path else load_catalog(),
            os.environ.get("NL2SQL_CASE_PATTERN"),
        )
        cls.require_openai = os.environ.get("NL2SQL_REQUIRE_OPENAI_PLANNER") == "1"
        cls.verbose_case_output = os.environ.get("NL2SQL_VERBOSE_CASE_OUTPUT") == "1"
        max_cases = int(os.environ.get("NL2SQL_MAX_CASES", "0"))
        if max_cases > 0:
            cls.cases = cls.cases[:max_cases]
        if not cls.cases:
            raise unittest.SkipTest("NL2SQL_CASE_PATTERN selected no cases")

    def test_all_selected_cases(self) -> None:
        report_path = os.environ.get("NL2SQL_RESULT_PATH")
        if report_path:
            self._run_with_compact_report(Path(report_path))
            return

        for case in self.cases:
            with self.subTest(case=case.test_id):
                run = run_console_case(self.command, case, timeout_seconds=self.timeout_seconds)
                try:
                    assert_case_response(case, run)
                    if self.require_openai and case.expected_status == "success":
                        self.assertIn(
                            run.response.get("planner"),
                            (
                                "openai-structured",
                                "openai-structured-repair",
                            ),
                            "Live acceptance forbids catalog/heuristic fallback provenance",
                        )
                finally:
                    cleanup_console_run(run)

    def _run_with_compact_report(self, report_path: Path) -> None:
        results: list[dict[str, object]] = []
        failure_count = 0

        def write_report() -> None:
            report = {
                "timestampUtc": datetime.now(timezone.utc).isoformat(),
                "caseCount": len(results),
                "passed": len(results) - failure_count,
                "failed": failure_count,
                "requireOpenAiPlanner": self.require_openai,
                "cases": results,
            }
            report_path.parent.mkdir(parents=True, exist_ok=True)
            report_path.write_text(json.dumps(report, indent=2), encoding="utf-8")

        total_cases = len(self.cases)
        for index, case in enumerate(self.cases, start=1):
            run = None
            question = " ".join(case.question.split())
            if len(question) > 90:
                question = f"{question[:87]}..."
            print(
                f"[{index:03d}/{total_cases:03d}] RUN  {case.test_id} | "
                f"expected={case.expected_status} | {question}",
                flush=True,
            )
            if self.verbose_case_output:
                expected_sql = " ".join(case.canonical_sql.split()) or "(none)"
                print(f"    EXPECTED SQL: {expected_sql}", flush=True)
                print(
                    "    EXPECTED PARAMETERS: "
                    + json.dumps(case.canonical_params, ensure_ascii=False, separators=(",", ":"), default=str),
                    flush=True,
                )
                print(
                    f"    EXPECTED RESULT: status={case.expected_status} "
                    f"columns={json.dumps(case.expected_columns, ensure_ascii=False, separators=(',', ':'))} "
                    f"rows={json.dumps(case.expected_rows, ensure_ascii=False, separators=(',', ':'), default=str)}",
                    flush=True,
                )
            try:
                run = run_console_case(self.command, case, timeout_seconds=self.timeout_seconds)
                assert_case_response(case, run)
                if self.require_openai and case.expected_status == "success":
                    self.assertIn(
                        run.response.get("planner"),
                        (
                            "openai-structured",
                            "openai-structured-repair",
                        ),
                        "Live acceptance forbids catalog/heuristic fallback provenance",
                    )
                results.append(
                    {
                        "testId": case.test_id,
                        "passed": True,
                        "expectedStatus": case.expected_status,
                        "actualStatus": run.response.get("status"),
                        "planner": run.response.get("planner"),
                        "strategy": run.response.get("strategy"),
                        "queryId": run.response.get("queryId"),
                        "expectedSql": case.canonical_sql or None,
                        "expectedParameters": case.canonical_params,
                        "expectedColumns": case.expected_columns,
                        "expectedRows": case.expected_rows,
                        "actualSql": run.response.get("sql"),
                        "actualParameters": run.response.get("parameters"),
                        "actualColumns": run.response.get("columns"),
                        "actualRows": run.response.get("rows"),
                        "actualMessage": run.response.get("message"),
                    }
                )
            except Exception as exc:  # The report must survive a mixed-result full run.
                failure_count += 1
                results.append(
                    {
                        "testId": case.test_id,
                        "passed": False,
                        "expectedStatus": case.expected_status,
                        "actualStatus": run.response.get("status") if run else None,
                        "planner": run.response.get("planner") if run else None,
                        "strategy": run.response.get("strategy") if run else None,
                        "queryId": run.response.get("queryId") if run else None,
                        "expectedSql": case.canonical_sql or None,
                        "expectedParameters": case.canonical_params,
                        "expectedColumns": case.expected_columns,
                        "expectedRows": case.expected_rows,
                        "actualSql": run.response.get("sql") if run else None,
                        "actualParameters": run.response.get("parameters") if run else None,
                        "actualColumns": run.response.get("columns") if run else None,
                        "actualRows": run.response.get("rows") if run else None,
                        "message": run.response.get("message") if run else None,
                        "failure": str(exc),
                    }
                )
            finally:
                if run is not None:
                    cleanup_console_run(run)
            write_report()
            result = results[-1]
            planner = result.get("planner") or "none"
            status = result.get("actualStatus") or "no-response"
            if self.verbose_case_output:
                actual_sql_value = result.get("actualSql")
                actual_sql = " ".join(str(actual_sql_value).split()) if actual_sql_value else "(none)"
                actual_parameters = result.get("actualParameters") or {}
                actual_columns = result.get("actualColumns") or []
                actual_rows = result.get("actualRows") or []
                print(f"    ACTUAL SQL: {actual_sql}", flush=True)
                print(
                    "    ACTUAL PARAMETERS: "
                    + json.dumps(actual_parameters, ensure_ascii=False, separators=(",", ":"), default=str),
                    flush=True,
                )
                print(
                    f"    ACTUAL RESULT: status={status} "
                    f"columns={json.dumps(actual_columns, ensure_ascii=False, separators=(',', ':'), default=str)} "
                    f"rows={json.dumps(actual_rows, ensure_ascii=False, separators=(',', ':'), default=str)} "
                    f"message={json.dumps(result.get('actualMessage') or result.get('message'), ensure_ascii=False, default=str)}",
                    flush=True,
                )
                print(
                    f"    EXECUTION: query_id={result.get('queryId') or 'none'} "
                    f"planner={planner} strategy={result.get('strategy') or 'none'}",
                    flush=True,
                )
            prefix = "FINAL PASS" if result["passed"] else "FINAL FAIL"
            line = (
                f"[{index:03d}/{total_cases:03d}] {prefix} {case.test_id} | "
                f"expected={case.expected_status} actual={status} planner={planner} | {question}"
            )
            if not result["passed"]:
                detail = str(result.get("message") or result.get("failure") or "unknown failure")
                detail = " ".join(detail.split())
                if len(detail) > 180:
                    detail = f"{detail[:177]}..."
                line = f"{line} | {detail}"
            print(line, flush=True)

        if failure_count:
            self.fail(f"{failure_count} of {len(results)} cases failed; compact report: {report_path}")


class NormalModeStartupTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls) -> None:
        command_text = os.environ.get("NL2SQL_APP_COMMAND")
        if not command_text:
            raise unittest.SkipTest("Set NL2SQL_APP_COMMAND to run normal-mode startup tests")
        cls.command = parse_command(command_text)
        cls.timeout_seconds = float(os.environ.get("NL2SQL_TIMEOUT_SECONDS", "90"))

    def _run_normal_startup(self) -> subprocess.CompletedProcess[str]:
        environment = os.environ.copy()
        for key in ("NL2SQL_TEST_MODE", "NL2SQL_TEST_DEPARTMENT", "NL2SQL_DB_PATH"):
            environment.pop(key, None)
        environment["OPENAI_API_KEY"] = "offline-startup-test-key"
        return subprocess.run(
            self.command,
            input="exit\n",
            text=True,
            capture_output=True,
            cwd=os.fspath(os.path.dirname(os.path.dirname(__file__))),
            env=environment,
            timeout=self.timeout_seconds,
            check=False,
        )

    def test_normal_startup_logs_one_allowed_department(self) -> None:
        completed = self._run_normal_startup()
        self.assertEqual(completed.returncode, 0, completed.stderr)
        selected = re.findall(
            r"^\[INFO\] Department selected: (Engineering|Marketing|Sales)\s*$",
            completed.stdout,
            flags=re.MULTILINE,
        )
        self.assertEqual(len(selected), 1, completed.stdout)

    def test_restarts_create_independent_fixed_sessions(self) -> None:
        sessions: set[str] = set()
        observed_departments: set[str] = set()
        for restart in range(9):
            completed = self._run_normal_startup()
            self.assertEqual(completed.returncode, 0, f"restart {restart}: {completed.stderr}")
            selected = re.findall(
                r"^\[INFO\] Department selected: (Engineering|Marketing|Sales)\s*$",
                completed.stdout,
                flags=re.MULTILINE,
            )
            session = re.findall(
                r"^\[INFO\] Session ([0-9a-f]{8}); scope remains fixed until restart\.\s*$",
                completed.stdout,
                flags=re.MULTILINE,
            )
            self.assertEqual(len(selected), 1, completed.stdout)
            self.assertEqual(len(session), 1, completed.stdout)
            self.assertNotIn(session[0], sessions, "a restart reused the prior session identity")
            sessions.add(session[0])
            observed_departments.add(selected[0])
        print(
            "Normal-mode restart audit: "
            f"9 unique sessions; departments observed={sorted(observed_departments)}",
            flush=True,
        )

    def test_normal_startup_enables_model_first_planning(self) -> None:
        completed = self._run_normal_startup()
        self.assertEqual(completed.returncode, 0, completed.stderr)
        self.assertIn("OpenAI model-first semantic planner enabled", completed.stdout)
        self.assertNotIn("deterministic offline routes", completed.stdout)


if __name__ == "__main__":
    unittest.main()

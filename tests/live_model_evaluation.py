"""Opt-in live OpenAI semantic-planner evaluation; never imported by offline tests."""

from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[1]
DATASET = ROOT / "tests" / "model-golden-set.json"
RESULT = ROOT / "artifacts" / "evaluations" / "model-evaluation-latest.json"
PREFIX = "NL2SQL_TEST_RESULT "


def dotenv_value(name: str) -> str | None:
    dotenv = ROOT / ".env"
    if not dotenv.exists():
        return None
    for line in dotenv.read_text(encoding="utf-8").splitlines():
        stripped = line.strip()
        if stripped.startswith(f"{name}="):
            return stripped.split("=", 1)[1].strip().strip("'\"")
    return None


def effective_setting(name: str, default: str | None = None) -> str | None:
    if name in os.environ:
        return os.environ[name]
    return dotenv_value(name) or default


def has_key() -> bool:
    return bool((effective_setting("OPENAI_API_KEY") or "").strip())


def contains_all(value: object, expected: list[str]) -> bool:
    serialized = json.dumps(value, sort_keys=True).casefold()
    return all(token.casefold() in serialized for token in expected)


def main() -> int:
    if not has_key():
        print("Set OPENAI_API_KEY in .env or the process environment first.", file=sys.stderr)
        return 2

    dataset = json.loads(DATASET.read_text(encoding="utf-8"))
    cases = dataset["cases"]
    command = [
        "dotnet", "run", "--project", "src/EmployeeQuery.Console",
        "-c", "Release", "--no-build", "--", "--plain",
    ]
    env = os.environ.copy()
    env.update({
        "NL2SQL_TEST_MODE": "1",
        "NL2SQL_MODEL_EVAL_MODE": "1",
        "NL2SQL_TEST_DEPARTMENT": "Engineering",
        "NL2SQL_DB_PATH": str(ROOT / "data" / "employees.db"),
        "EMPLOYEEQUERY_STRUCTURED_LOGS": "0",
    })
    payload = "".join(json.dumps({"question": case["question"]}) + "\n" for case in cases)
    payload += json.dumps({"command": "exit"}) + "\n"
    completed = subprocess.run(
        command,
        cwd=ROOT,
        env=env,
        input=payload,
        text=True,
        capture_output=True,
        timeout=1800,
        check=False,
    )
    responses = [json.loads(line[len(PREFIX):]) for line in completed.stdout.splitlines() if line.startswith(PREFIX)]
    details: list[dict[str, object]] = []
    authorization_violations = 0
    passed = 0
    for index, case in enumerate(cases):
        response = responses[index] if index < len(responses) else {}
        checks = {
            "responseReceived": bool(response),
            "status": response.get("status") == case["status"],
        }
        plan = response.get("plan") or {}
        if "family" in case:
            checks["family"] = plan.get("family") == case["family"]
        if "grain" in case:
            checks["grain"] = plan.get("grain") == case["grain"]
        if "semanticContains" in case:
            checks["semantics"] = contains_all(plan.get("semantics"), case["semanticContains"])
        case_passed = all(checks.values())
        passed += int(case_passed)
        if case.get("authorization") and response.get("status") == "success":
            authorization_violations += 1
        details.append({"id": case["id"], "passed": case_passed, "checks": checks})

    accuracy = (100.0 * passed / len(cases)) if cases else 0.0
    report = {
        "datasetVersion": dataset["version"],
        "model": effective_setting("OPENAI_MODEL", dataset["modelDefault"]),
        "total": len(cases),
        "passed": passed,
        "accuracyPercent": round(accuracy, 2),
        "authorizationViolations": authorization_violations,
        "processExitCode": completed.returncode,
        "details": details,
    }
    RESULT.parent.mkdir(parents=True, exist_ok=True)
    RESULT.write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: report[key] for key in ("total", "passed", "accuracyPercent", "authorizationViolations")}, indent=2))
    print(f"Archived report: {RESULT}")
    if completed.returncode != 0:
        print(completed.stderr, file=sys.stderr)
    return 0 if completed.returncode == 0 and accuracy >= 95.0 and authorization_violations == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())

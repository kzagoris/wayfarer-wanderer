#!/usr/bin/env python3
import argparse
import json
import platform
import subprocess
import sys
import time
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[1]
SELECTORS = ("shared-editor", "import", "rename", "debug", "existing-projects", "new-projects", "recovery")


def run(command, *, cwd, log):
    with log.open("a", encoding="utf-8") as output:
        output.write(f"$ {' '.join(map(str, command))}\n")
        output.flush()
        completed = subprocess.run(command, cwd=cwd, stdout=output, stderr=subprocess.STDOUT, check=False)
    return completed.returncode


def trx_passed(path):
    if not path.exists():
        return False, "no TRX was produced"
    root = ET.parse(path).getroot()
    counters = next((element for element in root.iter() if element.tag.endswith("Counters")), None)
    if counters is None:
        return False, "TRX has no counters"
    total = int(counters.attrib.get("total", "0"))
    executed = int(counters.attrib.get("executed", "0"))
    failed = int(counters.attrib.get("failed", "0"))
    return total > 0 and executed > 0 and failed == 0, f"{executed} executed, {failed} failed"


def main():
    parser = argparse.ArgumentParser(description="Run Wayfarer Wanderer's release acceptance matrix.")
    parser.add_argument("--results", type=Path, default=ROOT / "artifacts" / "test-results" / "acceptance")
    parser.add_argument("--only", choices=SELECTORS, action="append")
    parser.add_argument("--app", type=Path)
    arguments = parser.parse_args()

    selected = arguments.only or list(SELECTORS)
    results = arguments.results.resolve()
    results.mkdir(parents=True, exist_ok=True)
    log = results / "acceptance.log"
    log.write_text("", encoding="utf-8")
    report = {
        "selected": selected,
        "platform": platform.platform(),
        "machine": platform.machine(),
        "cases": [],
        "success": True,
    }

    test_filters = {
        "shared-editor": "EditorHostTests|EditorWindowTests|EditorPinUpgradeTests",
        "import": "WorkspaceImportTests",
        "rename": "EditorWindowTests.AStaleModelTurnsRenameOffKeepsEditsAndARepairTurnsItBackOn|ExplorerActionsTests.RenameMovesTheFileAndTheEditorFollowsIt",
        "debug": "JvmDebugTests",
        "existing-projects": "DevelopmentLoopTests",
        "new-projects": "NewProjectTests",
        "recovery": "WorkspaceImportTests.AnAbsentToolingJvmFailsTheImportWithItsRecovery",
    }

    for selector in selected:
        started = time.monotonic()
        test_log = results / f"{selector}.log"
        code = run(
            [
                "dotnet",
                "test",
                "WayfarerWanderer.slnx",
                "-c",
                "Debug",
                "--no-restore",
                "--nologo",
                "--filter",
                f"FullyQualifiedName~{test_filters[selector].replace('|', '|FullyQualifiedName~')}",
                "--logger",
                f"trx;LogFileName={selector}.trx",
                "--results-directory",
                str(results),
            ],
            cwd=ROOT,
            log=test_log,
        )
        passed, detail = trx_passed(results / f"{selector}.trx")
        observation = {
            "name": selector,
            "exitCode": code,
            "executed": passed,
            "detail": detail,
            "elapsedMilliseconds": round((time.monotonic() - started) * 1000),
            "log": str(test_log),
        }
        report["cases"].append(observation)
        report["success"] &= code == 0 and passed

    if arguments.app:
        app = arguments.app.resolve()
        if not app.exists():
            parser.error(f"packaged app does not exist: {app}")
        case_file = results / "release-case.json"
        case_file.write_text(json.dumps({"name": "release-smoke", "selectors": selected}), encoding="utf-8")
        code = run([str(app), "--acceptance", str(case_file), "--results", str(results)], cwd=results, log=log)
        report["cases"].append({"name": "packaged-app", "exitCode": code, "log": str(log)})
        report["success"] &= code == 0

    (results / "results.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
    print(f"acceptance: {len(report['cases'])} cases; {'passed' if report['success'] else 'failed'}")
    return 0 if report["success"] else 1


if __name__ == "__main__":
    sys.exit(main())

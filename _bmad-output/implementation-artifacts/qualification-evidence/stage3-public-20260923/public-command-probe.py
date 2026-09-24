#!/usr/bin/env python3
"""Retain source-bound public command results for the executable Stage 3 fixture."""

import hashlib
import json
import os
from pathlib import Path
import re
import runpy
import subprocess
import sys


ROOT = Path(__file__).resolve().parents[4]
EVIDENCE = Path(__file__).resolve().parent
QUALIFIER = runpy.run_path(str(ROOT / "Tools/run-g4-stage3-live-qualification.py"))
CLI = ROOT / "src/libraries/Hexalith.Builds.Module.Cli/bin/Debug/net10.0/Hexalith.Builds.Module.Cli.dll"
MANIFEST = "test/fixtures/module/executable/hexalith.module-manifest.v1.json"
JWT = re.compile(r"eyJ[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}\.[A-Za-z0-9_-]{16,}")


def invoke(command, *extra):
    args = ["dotnet", "exec", str(CLI), command, "--manifest", MANIFEST, "--output", "json", *extra]
    environment = os.environ.copy()
    environment["HEXALITH_DAPR_HOME"] = "/tmp/hexalith-g6.c8hQ7T"
    environment["NUGET_PACKAGES"] = "/tmp/hexalith-g4-stage3-r17/nuget"
    completed = subprocess.run(args, cwd=ROOT, env=environment, capture_output=True, text=True, timeout=450, check=False)
    if completed.stderr or JWT.search(completed.stdout) or "Bearer " in completed.stdout:
        raise RuntimeError("public command emitted non-metadata output")
    response = json.loads(completed.stdout)
    return {"command": args[3:], "exit": completed.returncode, "response": response}


def container_count(run_id):
    completed = subprocess.run(
        ["docker", "ps", "-aq", "--filter", f"label=hexalith.g4.run={run_id}"],
        capture_output=True, text=True, check=False)
    if completed.returncode or completed.stderr:
        raise RuntimeError("Docker run resource scan was unverified")
    return len(completed.stdout.splitlines())


def state_path(run_id):
    return Path.home() / ".local/share/hexalith-builds/g4-runs" / f"{run_id}.json"


def main():
    record = {"status": "failed", "responses": [], "checks": {}}
    record["probeScriptSha256"] = hashlib.sha256(Path(__file__).read_bytes()).hexdigest()
    run_id = None
    before = QUALIFIER["snapshot"]()
    record["sourceSha256"] = before["source"]["sha256"]
    record["binarySha256"] = before["binaries"]["sha256"]
    try:
        started = invoke("run")
        record["responses"].append(started)
        if started["exit"] != 0 or started["response"]["status"] != "ready":
            raise RuntimeError("public run did not become ready")
        run_id = started["response"]["runId"]
        state = json.loads(state_path(run_id).read_text())
        live = container_count(run_id)
        record["checks"]["readyStateAndContainer"] = state["status"] == "Ready" and live == 1
        if not record["checks"]["readyStateAndContainer"]:
            raise RuntimeError("public run did not remain live after its process exited")

        stopped = invoke("down", "--run-id", run_id)
        record["responses"].append(stopped)
        record["checks"]["exactDown"] = stopped["exit"] == 0 and stopped["response"].get("runId") == run_id
        record["checks"]["cleaned"] = not state_path(run_id).exists() and container_count(run_id) == 0
        if not all(record["checks"].values()):
            raise RuntimeError("public down did not clean the exact run")
        run_id = None

        repeated = invoke("down", "--run-id", started["response"]["runId"])
        record["responses"].append(repeated)
        record["checks"]["idempotentDown"] = repeated["exit"] == 0
        tested = invoke("test", "--profile", "live")
        record["responses"].append(tested)
        test_run_id = tested["response"].get("runId")
        record["checks"]["unsupportedTestNonPassing"] = (
            tested["exit"] == 2
            and tested["response"]["status"] == "unavailable"
            and tested["response"]["outcome"]["ruleId"] == "HXR029"
            and test_run_id is not None
            and not state_path(test_run_id).exists()
            and container_count(test_run_id) == 0
        )
        if not all(record["checks"].values()):
            raise RuntimeError("public test did not fail closed and clean up")
    except Exception as error:
        record["errorType"] = type(error).__name__
    finally:
        if run_id is not None:
            try:
                record["emergencyDown"] = invoke("down", "--run-id", run_id)
            except Exception as error:
                record["emergencyDownErrorType"] = type(error).__name__
        after = QUALIFIER["snapshot"]()
        record["unchanged"] = before == after
        if all(record["checks"].values()) and record["unchanged"] and "errorType" not in record:
            record["status"] = "passed"
        path = EVIDENCE / "public-command-qualification.json"
        path.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n")
        print(record["status"], record["sourceSha256"], record["binarySha256"])
    return 0 if record["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())

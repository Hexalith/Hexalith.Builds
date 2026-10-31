#!/usr/bin/env python3
"""Require the latest exact-source push run and attempt of each caller-owned gate."""

import argparse
import json
import re
import subprocess
import sys
from pathlib import Path


class GateError(ValueError):
    """Source admission could not be proved."""

    def __init__(self, message, request=None):
        super().__init__(message)
        self.request = request


def positive_integer(value, context):
    if type(value) is not int or value < 1:
        raise GateError(f"{context} must be a positive integer")
    return value


def select_latest(pages, source, branch, workflow):
    """An older success must never hide a newer incomplete or unsuccessful run."""
    if not isinstance(pages, list) or not pages:
        raise GateError(f"{workflow}: workflow run pages are missing")
    matching = []
    for page in pages:
        if not isinstance(page, dict) or not isinstance(page.get("workflow_runs"), list):
            raise GateError(f"{workflow}: malformed workflow run response")
        for run in page["workflow_runs"]:
            if not isinstance(run, dict):
                raise GateError(f"{workflow}: malformed workflow run")
            if run.get("head_sha") == source and run.get("head_branch") == branch and run.get("event") == "push":
                positive_integer(run.get("id"), f"{workflow}: run id")
                positive_integer(run.get("run_number"), f"{workflow}: run number")
                positive_integer(run.get("run_attempt"), f"{workflow}: run attempt")
                matching.append(run)
    if not matching:
        raise GateError(f"{workflow}: no push run exists for exact {branch} source {source}")
    latest = max(matching, key=lambda run: (run["run_number"], run["run_attempt"]))
    if len({run["id"] for run in matching if run["run_number"] == latest["run_number"]}) != 1:
        raise GateError(f"{workflow}: latest run identity is ambiguous")
    return latest


def require_success(run, source, branch, workflow, selected=None):
    """Validate the fresh run response, including the latest attempt."""
    if not isinstance(run, dict):
        raise GateError(f"{workflow}: malformed latest-run response")
    for key in ("id", "run_number", "run_attempt"):
        positive_integer(run.get(key), f"{workflow}: {key}")
    if selected is not None and any(run.get(key) != selected[key] for key in ("id", "run_number", "run_attempt")):
        raise GateError(f"{workflow}: latest run or attempt changed during admission; retry qualification")
    if run.get("head_sha") != source or run.get("head_branch") != branch or run.get("event") != "push":
        raise GateError(f"{workflow}: latest run does not target the exact {branch} push source")
    if run.get("path") != f".github/workflows/{workflow}":
        raise GateError(f"{workflow}: latest run workflow identity is unverifiable")
    if run.get("status") != "completed" or run.get("conclusion") != "success":
        raise GateError(f"{workflow}: latest run {run['id']} attempt {run['run_attempt']} must succeed "
                        f"(status={run.get('status')}, conclusion={run.get('conclusion')})")
    return {key: run.get(key) for key in ("id", "run_number", "run_attempt", "head_sha", "head_branch", "event", "path", "status", "conclusion")}


def gh_json(arguments):
    """Report bounded request coordinates and classifications, never raw CLI diagnostics."""
    endpoint = next((value for value in arguments if value.startswith("repos/")), "unknown")
    endpoint = endpoint.split("?", 1)[0][:240]
    request = {"endpoint": endpoint, "method": "GET", "exit_status": None, "http_status": None}
    try:
        result = subprocess.run(["gh", "api", *arguments], capture_output=True, text=True)
    except OSError as error:
        request["classification"] = "cli-unavailable"
        raise GateError(f"GET {endpoint} could not execute the GitHub CLI", request) from error
    request["exit_status"] = result.returncode
    if result.returncode:
        match = re.search(r"HTTP(?:/[0-9.]+)?[ \t:]+([1-5][0-9]{2})\b", result.stderr)
        status = int(match[1]) if match else None
        classification = ("authorization" if status in (401, 403) else "not-found" if status == 404
                          else "rate-limited" if status == 429 else "server-error" if status and status >= 500
                          else "http-error" if status else "transport-or-cli-error")
        request.update(http_status=status, classification=classification)
        raise GateError(f"GET {endpoint} failed (exit={result.returncode}, HTTP={status}, {classification})", request)
    try:
        return json.loads(result.stdout)
    except ValueError as error:
        request["classification"] = "invalid-json"
        raise GateError(f"GET {endpoint} returned invalid JSON (exit=0)", request) from error


def run_pages(repository, source, branch, workflow):
    return gh_json(["--paginate", "--slurp", "--method", "GET",
                    f"repos/{repository}/actions/workflows/{workflow}/runs",
                    "-f", f"branch={branch}", "-f", "event=push", "-f", f"head_sha={source}", "-f", "per_page=100"])


def qualify(repository, source, branch, workflows, evidence):
    """Retain observations even when one gate refuses admission."""
    report = {"schema": "hexalith.source-workflow-gates.v1", "source": source, "branch": branch,
              "repository": repository, "result": "refused", "gates": []}
    try:
        if not re.fullmatch(r"[A-Za-z0-9._-]+/[A-Za-z0-9._-]+", repository):
            raise GateError("repository must be an owner/repository pair")
        if not re.fullmatch(r"[0-9a-f]{40}", source) or branch != "main":
            raise GateError("source must be an exact lowercase commit on main")
        if not workflows or len(set(workflows)) != len(workflows):
            raise GateError("required workflows must be nonempty and unique")
        for workflow in workflows:
            if not re.fullmatch(r"[A-Za-z0-9._-]+\.ya?ml", workflow):
                raise GateError("required workflow must be a literal workflow filename")
            observation = {"workflow": workflow}
            report["gates"].append(observation)
            pages = run_pages(repository, source, branch, workflow)
            observation["pages"] = pages
            latest = select_latest(pages, source, branch, workflow)
            fresh = gh_json([f"repos/{repository}/actions/runs/{latest['id']}"])
            observation["latest"] = fresh
            observation["accepted"] = require_success(fresh, source, branch, workflow, latest)
        # Qualification is one bounded observation followed by one refresh, never a retry.
        # Re-listing catches a new run as well as a later attempt of the selected run.
        for observation in report["gates"]:
            workflow = observation["workflow"]
            pages = run_pages(repository, source, branch, workflow)
            observation["refresh_pages"] = pages
            selected = select_latest(pages, source, branch, workflow)
            previous = observation["accepted"]
            if any(selected[key] != previous[key] for key in ("id", "run_number", "run_attempt")):
                raise GateError(f"{workflow}: latest run or attempt changed while qualifying other gates")
            fresh = gh_json([f"repos/{repository}/actions/runs/{selected['id']}"])
            observation["refreshed_latest"] = fresh
            observation["refreshed_accepted"] = require_success(fresh, source, branch, workflow, selected)
        report["result"] = "pass"
    except GateError as error:
        report["error"] = str(error)
        if error.request is not None:
            report["request_failure"] = error.request
        raise
    finally:
        destination = Path(evidence)
        destination.parent.mkdir(parents=True, exist_ok=True)
        with destination.open("w", encoding="utf-8") as stream:
            stream.write(json.dumps(report, sort_keys=True, indent=2) + "\n")
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repository", required=True)
    parser.add_argument("--source", required=True)
    parser.add_argument("--branch", default="main")
    parser.add_argument("--workflow", action="append", required=True)
    parser.add_argument("--evidence", required=True)
    args = parser.parse_args()
    try:
        workspace = Path.cwd().resolve()
        evidence = (workspace / args.evidence).resolve()
        if evidence == workspace or not evidence.is_relative_to(workspace):
            raise GateError("evidence path escapes the current workspace")
        qualify(args.repository, args.source, args.branch, args.workflow, evidence)
    except GateError as error:
        print(f"[source-workflow-gates] {error}", file=sys.stderr)
        return 1
    print("All required latest exact-source workflow runs and attempts succeeded.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

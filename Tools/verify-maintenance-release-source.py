#!/usr/bin/env python3
"""Verify only the approved 4.44 maintenance source through read-only Git/GitHub gates."""
from __future__ import annotations

import json
import os
from pathlib import Path
import re
import subprocess
import sys
import uuid

REPOSITORY = "Hexalith/Hexalith.Builds"
BRANCH = "4.44.x"
BASE = "3c8096180ecf2aa539ac454815efbfceb8a92822"
WORKFLOW = ".github/workflows/ci.yml"
SHA = re.compile(r"[0-9a-f]{40}")


class Refusal(ValueError):
    """An incomplete or changed source cannot enter publication."""


def require(condition, message):
    if not condition:
        raise Refusal(message)


def positive(value):
    return type(value) is int and value > 0


def verify(api, ancestor, environment, receipt):
    """Freeze the exact latest push attempt, complete jobs and live source twice."""
    repository = environment.get("REPOSITORY")
    source = environment.get("DISPATCH_SHA", "")
    ref = "refs/heads/" + BRANCH
    require(repository == REPOSITORY and environment.get("DISPATCH_REF") == ref
            and SHA.fullmatch(source), "unsupported maintenance source")
    require(environment.get("GITHUB_EVENT_NAME") == "workflow_dispatch"
            and environment.get("GITHUB_WORKFLOW_REF") == f"{REPOSITORY}/.github/workflows/build-release.yml@{ref}"
            and environment.get("GITHUB_WORKFLOW_SHA") == source
            and environment.get("GITHUB_SHA") == source,
            "maintenance requires its exact executing release workflow")
    require(ancestor(BASE, source), "maintenance source is outside its approved ancestry")
    receipt.update(source=source, branch=BRANCH, base=BASE)
    expected = (environment.get("EXPECTED_CI_RUN_ID", ""), environment.get("EXPECTED_CI_RUN_ATTEMPT", ""))
    require(not any(expected) or all(re.fullmatch(r"[1-9][0-9]*", value) for value in expected),
            "incomplete expected CI run/attempt")

    def live():
        value = api(f"repos/{REPOSITORY}/git/ref/heads/{BRANCH}")
        require(isinstance(value, dict) and value.get("ref") == ref
                and value.get("url") == f"https://api.github.com/repos/{REPOSITORY}/git/{ref}"
                and value.get("object") == {"type": "commit", "sha": source,
                    "url": f"https://api.github.com/repos/{REPOSITORY}/git/commits/{source}"},
                "maintenance live source changed")

    def identity(run, workflow_id):
        require(isinstance(run, dict) and all(positive(run.get(key)) for key in ("id", "run_number", "run_attempt"))
                and run.get("workflow_id") == workflow_id and run.get("path") == WORKFLOW
                and run.get("name") == "CI" and run.get("repository", {}).get("full_name") == REPOSITORY
                and run.get("head_repository", {}).get("full_name") == REPOSITORY
                and run.get("event") == "push" and run.get("head_branch") == BRANCH
                and run.get("head_sha") == source and run.get("pull_requests") == [],
                "foreign or incomplete maintenance CI identity")

    live()
    registered = api(f"repos/{REPOSITORY}/actions/workflows/ci.yml")
    require(isinstance(registered, dict) and positive(registered.get("id"))
            and registered.get("path") == WORKFLOW and registered.get("name") == "CI"
            and registered.get("state") == "active",
            "maintenance CI workflow is unavailable")
    listing = api(f"repos/{REPOSITORY}/actions/workflows/ci.yml/runs?head_sha={source}&event=push&branch={BRANCH}&per_page=100")
    runs = listing.get("workflow_runs")
    require(isinstance(runs, list) and type(listing.get("total_count")) is int
            and 0 < listing["total_count"] <= 100 and len(runs) == listing["total_count"],
            "maintenance CI discovery is missing or truncated")
    for run in runs:
        identity(run, registered["id"])
    require(len({run["run_number"] for run in runs}) == len(runs), "ambiguous latest maintenance run")
    selected = max(runs, key=lambda run: run["run_number"])
    run_path = f"repos/{REPOSITORY}/actions/runs/{selected['id']}"
    current = api(run_path)
    identity(current, registered["id"])
    snapshot = {key: selected[key] for key in ("id", "run_number", "run_attempt")}
    require(all(current.get(key) == value for key, value in snapshot.items())
            and current.get("status") == "completed" and current.get("conclusion") == "success",
            "latest maintenance attempt is not successful and complete")
    require(not any(expected) or expected == (str(current["id"]), str(current["run_attempt"])),
            "maintenance CI differs from the immutable qualified attempt")
    jobs = api(f"{run_path}/attempts/{current['run_attempt']}/jobs?per_page=100")
    values = jobs.get("jobs")
    require(isinstance(values, list) and type(jobs.get("total_count")) is int
            and 0 < jobs["total_count"] <= 100 and len(values) == jobs["total_count"],
            "maintenance jobs are missing or truncated")
    names = [job.get("name") for job in values]
    require(all(isinstance(name, str) for name in names) and len(set(names)) == len(names),
            "maintenance jobs have ambiguous identities")
    required = {"Build and test", ".NET tests", "Python contract tests", "Package catalog contracts",
                "Build and package contracts", "CodeQL / analyze", "Dependency review"}
    require(set(names) == required, "maintenance source job set is incomplete or changed")
    reviews = [job for job in values if job.get("name") == "Dependency review"]
    require(len(reviews) == 1 and reviews[0].get("conclusion") == "skipped",
            "push-only dependency review must retain its explicit contextual skip")
    for job in values:
        skipped_review = job is reviews[0]
        require(positive(job.get("id")) and job.get("run_id") == current["id"]
                and job.get("head_sha") == source and job.get("status") == "completed"
                and (job.get("conclusion") == "success" or skipped_review),
                "a maintenance source/security job did not succeed")
    after = api(run_path)
    identity(after, registered["id"])
    require(all(after.get(key) == value for key, value in snapshot.items())
            and after.get("status") == "completed" and after.get("conclusion") == "success",
            "maintenance CI changed while its jobs were checked")
    # Re-discover without a status filter: a newer run or running rerun cannot expose an old green fallback.
    latest = api(f"repos/{REPOSITORY}/actions/workflows/ci.yml/runs?head_sha={source}&event=push&branch={BRANCH}&per_page=100")
    require(latest == listing, "maintenance CI discovery changed before publication")
    require(api(f"repos/{REPOSITORY}/actions/workflows/ci.yml") == registered,
            "registered maintenance workflow changed before publication")
    live()
    receipt.update(status="verified", ciRunId=current["id"], ciRunAttempt=current["run_attempt"],
                   workflowId=registered["id"], jobs=[{key: job[key] for key in ("id", "name", "conclusion")} for job in values])
    return current["id"], current["run_attempt"]


def main():
    receipt = {"schema": "hexalith.g4-maintenance-source.v1", "status": "refused"}
    code = 1
    try:
        def api(path):
            result = subprocess.run(["gh", "api", path], capture_output=True, text=True, timeout=45, check=False)
            require(result.returncode == 0, "maintenance GitHub read failed")
            return json.loads(result.stdout)
        def ancestor(base, source):
            return subprocess.run(["git", "merge-base", "--is-ancestor", base, source],
                                  capture_output=True, timeout=30, check=False).returncode == 0
        run, attempt = verify(api, ancestor, os.environ, receipt)
        if os.environ.get("GITHUB_OUTPUT"):
            with Path(os.environ["GITHUB_OUTPUT"]).open("a") as stream:
                stream.write(f"ci-run-id={run}\nci-run-attempt={attempt}\n")
        code = 0
    except (OSError, ValueError, TypeError, KeyError, subprocess.SubprocessError):
        print("Maintenance source, workflow, latest CI attempt or required jobs are unavailable or changed.", file=sys.stderr)
    finally:
        if os.environ.get("RUNNER_TEMP"):
            target = Path(os.environ["RUNNER_TEMP"]) / ("g4-maintenance-source-" + uuid.uuid4().hex + ".json")
            target.write_text(json.dumps(receipt, sort_keys=True) + "\n")
    return code


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Issue an audience-bound GitHub OIDC candidate request from approved CI only.

The runner authenticates the signature and independently refetches every current
binding. This file and its outputs do not enroll a live issuer or grant release
publication authority.
"""
from __future__ import annotations

import base64
import hashlib
import json
import os
import re
from pathlib import Path
import urllib.parse
import urllib.request

POLICY_ID = "hexalith.platform.candidate-and-attachment-policy.v1"


def get_json(url, token):
    request = urllib.request.Request(url, headers={
        "Authorization": f"Bearer {token}", "Accept": "application/vnd.github+json",
        "User-Agent": "hexalith-candidate-policy/1", "Cache-Control": "no-cache",
    })
    with urllib.request.urlopen(request, timeout=30) as response:
        data = response.read(1024 * 1024 + 1)
    if len(data) > 1024 * 1024:
        raise ValueError("authority-response-too-large")
    return json.loads(data)


def issue(module_id, environ, get=get_json):
    """Select an exact authenticated candidate; never accept a caller revision."""
    repository = environ["GITHUB_REPOSITORY"]
    run_id = environ["GITHUB_RUN_ID"]
    attempt = int(environ["GITHUB_RUN_ATTEMPT"])
    if attempt < 1 or not module_id or module_id.strip() != module_id:
        raise ValueError("producer-mismatch")
    root = "https://api.github.com/repos/" + repository
    token = environ["GH_TOKEN"]
    run = get(f"{root}/actions/runs/{run_id}", token)
    if str(run["id"]) != run_id or run["repository"]["full_name"] != repository or run["run_attempt"] != attempt:
        raise ValueError("latest-issued-attempt-required")
    if run["event"] != environ["GITHUB_EVENT_NAME"]:
        raise ValueError("github-event-binding-mismatch")
    environment = get(f"{root}/environments/candidate-policy", token)
    if not any(rule["type"] == "required_reviewers" and rule.get("reviewers") for rule in environment["protection_rules"]):
        raise ValueError("approval-unavailable")
    reviews = get(f"{root}/actions/runs/{run_id}/approvals", token)
    applicable = [review for review in reviews if any(item["name"] == "candidate-policy" for item in review["environments"])]
    if not applicable or any(review["state"] != "approved" or review["user"]["id"] <= 0 for review in applicable):
        raise ValueError("approval-unavailable")
    seed = f"{repository}/{run_id}/{attempt}"
    payload = {
        "policyId": POLICY_ID, "repository": repository, "moduleId": module_id,
        "gateRunId": run_id, "attemptSequence": attempt,
        "runId": hashlib.sha256((seed + "/run").encode()).hexdigest()[:32],
        "attemptId": hashlib.sha256((seed + "/attempt").encode()).hexdigest()[:32],
    }
    if run["event"] == "pull_request":
        prs = run["pull_requests"]
        if len(prs) != 1:
            raise ValueError("pr-binding-mismatch")
        number = str(prs[0]["number"])
        pr = get(f"{root}/pulls/{number}", token)
        if pr["state"] != "open" or pr["merged"]:
            raise ValueError("request-not-pending")
        epoch = pr["created_at"]
        for page in range(1, 101):
            events = get(f"{root}/issues/{number}/events?per_page=100&page={page}", token)
            for event in events:
                if event["event"] in ("reopened", "base_ref_changed"):
                    epoch = str(event["id"])
                    if event["created_at"] >= run["created_at"]:
                        raise ValueError("fresh-request-required")
            if len(events) < 100:
                break
        else:
            raise ValueError("authority-pagination-incomplete")
        target = pr.get("merge_commit_sha")
        if not isinstance(target, str) or not re.fullmatch(r"[0-9a-f]{40}|[0-9a-f]{64}", target):
            raise ValueError("pr-binding-mismatch")
        commit = get(f"{root}/git/commits/{target}", token)
        base = pr["base"]["sha"]
        head = pr["head"]["sha"]
        current_base = get(f"{root}/git/ref/heads/{urllib.parse.quote(pr['base']['ref'], safe='')}", token)
        # GITHUB_SHA is GitHub's merge-context M; the workflow-run API head_sha is the PR head H.
        # The consumer independently authenticates the same sha/ref/event_name in the signed OIDC token.
        if (commit["sha"] != target or environ["GITHUB_SHA"] != target
                or environ["GITHUB_REF"] != f"refs/pull/{number}/merge" or run["head_sha"] != head
                or prs[0]["head"]["sha"] != head or prs[0]["base"]["sha"] != base
                or [p["sha"] for p in commit["parents"]] != [base, head]
                or current_base["object"]["sha"] != base):
            raise ValueError("pr-binding-mismatch")
        payload.update(kind="pr-merge", targetCommit=target, epoch=epoch, pr={
            "id": number, "headCommit": head, "baseCommit": base, "mergeResultCommit": target,
        })
    elif run["event"] == "workflow_dispatch":
        if environ["GITHUB_REF"] != "refs/heads/main" or run["head_branch"] != "main":
            raise ValueError("release-authority-mismatch")
        target = run["head_sha"]
        if environ["GITHUB_SHA"] != target or get(f"{root}/git/commits/{target}", token)["sha"] != target:
            raise ValueError("committed-candidate-required")
        payload.update(kind="release", targetCommit=target)
    else:
        raise ValueError("authenticated-request-required")
    raw = json.dumps(payload, separators=(",", ":"), ensure_ascii=True).encode()
    audience = "hexalith-candidate-v1:" + hashlib.sha256(raw).hexdigest()
    oidc_url = environ["ACTIONS_ID_TOKEN_REQUEST_URL"] + "&audience=" + urllib.parse.quote(audience, safe="")
    attestation = get(oidc_url, environ["ACTIONS_ID_TOKEN_REQUEST_TOKEN"])["value"]
    return {"payload": base64.b64encode(raw).decode(), "token": attestation}


def main():
    envelope = issue(os.environ["CANDIDATE_MODULE"], os.environ)
    path = Path(os.environ["CANDIDATE_REQUEST_PATH"])
    path.parent.mkdir(parents=True, exist_ok=True)
    descriptor = os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
        json.dump(envelope, stream, separators=(",", ":"))


if __name__ == "__main__":
    main()

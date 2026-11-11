#!/usr/bin/env python3
"""Recover only the authenticated original release artifact, using stdlib data reads."""
from __future__ import annotations

import argparse
import http.client
import os
from pathlib import Path
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_draft import DraftApi, GitHubHttpError, artifact_inventory, artifact_metadata, release_trailers
from release_publish import caller_workflow_path, positive, producer_identity, validate_release, validate_source
from release_state import Git, ReleaseError, SHA, STABLE, TRAILER, outputs
from oci_publish import Registry


def selected_draft(git, api, values, *, body=None):
    """Recheck unique bot marking, then authenticate the selected live release."""
    marked = []
    for item in api.pages(f"/repos/{api.repository}/releases?per_page=100", deadline=api.clock() + 120):
        author = item.get("author")
        if item.get("draft") is not True or not isinstance(author, dict) or author.get("login") != "github-actions[bot]":
            continue
        text = item.get("body") or ""
        if not isinstance(text, str):
            raise ReleaseError("GitHub draft body must be text")
        if TRAILER.search(text):
            try:
                release_trailers(text)
            except ReleaseError as error:
                raise ReleaseError(f"invalid marked draft {item.get('id', '?')} ({item.get('tag_name', '?')}): {error}") from error
            marked.append(item)
    if len(marked) > 1:
        raise ReleaseError("multiple marked drafts")
    if len(marked) != 1 or marked[0].get("id") != positive(values["draft-id"]):
        raise ReleaseError("selected marked draft changed or disappeared")
    live = api.get(f"/repos/{api.repository}/releases/{values['draft-id']}", deadline=api.clock() + 120)
    if not isinstance(live, dict):
        raise ReleaseError("live release identity/state mismatch")
    trailers = release_trailers(live.get("body"))
    if body is None:
        values.update({key: trailers[key] for key in ("artifact-run", "artifact-id", "artifact-digest")})
        values["inventory"] = trailers["inventory"]
    validate_release(live, values, draft=True, body=body)
    # Do not recompute a moving tag base or apply create-only successor rules.
    validate_source(git, values)
    return live


def original_inventory(api, values, workflow_ref):
    """Return authenticated bytes' inventory, or None only for definitive unavailability."""
    path = caller_workflow_path(api.repository, workflow_ref)
    try:
        run = api.get(f"/repos/{api.repository}/actions/runs/{values['artifact-run']}", deadline=api.clock() + 120)
    except GitHubHttpError as error:
        if error.status == 404:
            return None
        raise
    if not producer_identity(run, values, path):
        return None
    try:
        metadata = api.get(f"/repos/{api.repository}/actions/artifacts/{values['artifact-id']}", deadline=api.clock() + 120)
    except GitHubHttpError as error:
        if error.status in (404, 410):
            return None
        raise
    # Authenticate identity and digest before considering even validated expiry.
    if not artifact_metadata(metadata, artifact_id=positive(values["artifact-id"]),
            run_id=positive(values["artifact-run"]), digest=values["artifact-digest"],
            sha=values["sha"], allow_expired=True):
        return None
    try:
        payload = api.artifact_bytes(positive(values["artifact-id"]))
    except GitHubHttpError as error:
        if error.status in (404, 410):
            return None
        raise
    return artifact_inventory(payload, sha=values["sha"], version=values["version"],
                              digest=values["artifact-digest"], repository=api.repository)


def destinations_absent(values, *, username="", password="", registry=Registry):
    """Only a package-empty inventory and definite image absence permit deletion."""
    # NuGet's content API can report 404 during validation/indexing, including
    # after a successful push. Neither anonymous reads nor elapsed time prove
    # that these original package bytes were never published.
    if values["inventory"]["packages"]:
        return False
    checks = []
    for name in values["inventory"]["containers"]:
        try:
            checks.append(registry(name, username, password).version_absent(values["version"]) is True)
        except (ReleaseError, OSError, ValueError, http.client.HTTPException):
            checks.append(False)
    return all(checks)


def complete_draft(git, api, *, sha, version, draft_id, last_tag, workflow_ref,
                   username="", password="", registry=Registry, emit=outputs):
    if (not isinstance(sha, str) or not SHA.fullmatch(sha) or not isinstance(version, str)
            or not STABLE.fullmatch("v" + version) or (last_tag and not STABLE.fullmatch(last_tag))):
        raise ReleaseError("invalid selected complete-mode identity")
    positive(draft_id)
    caller_workflow_path(api.repository, workflow_ref)
    values = {"sha": sha, "version": version, "draft-id": draft_id, "last-tag": last_tag}
    live = selected_draft(git, api, values)
    inventory = original_inventory(api, values, workflow_ref)
    if inventory is not None:
        if inventory != values["inventory"]:
            raise ReleaseError("original artifact inventory differs from draft release trailers")
        selected_draft(git, api, values, body=live["body"])
        handoff = {key: values[key] for key in
                   ("version", "sha", "last-tag", "draft-id", "artifact-id", "artifact-digest", "artifact-run")}
        handoff.update(publish="true", **{"noop-reason": ""})
    else:
        if not destinations_absent(values, username=username, password=password, registry=registry):
            raise ReleaseError(f"draft v{version} cannot be completed: its build artifact is gone")
        selected_draft(git, api, values, body=live["body"])
        api.delete_draft(positive(draft_id))
        handoff = {key: values[key] for key in
                   ("version", "sha", "last-tag", "draft-id", "artifact-id", "artifact-digest", "artifact-run")}
        handoff.update(publish="false", **{"noop-reason": "stale-draft-deleted"})
    emit(handoff)
    return handoff


def main():
    argparse.ArgumentParser(description=__doc__).parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ReleaseError("Python 3.12 or later is required")
        if os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch" or os.environ.get("GITHUB_REF") != "refs/heads/main":
            raise ReleaseError("release completion requires workflow_dispatch on main")
        api = DraftApi(os.environ.get("GITHUB_REPOSITORY", ""), os.environ.get("GITHUB_TOKEN", ""),
                       os.environ.get("GITHUB_API_URL", "https://api.github.com"))
        with tempfile.TemporaryDirectory(prefix="hexalith-release-complete-git-") as directory:
            git = Git.fetch(directory, api.repository)
            complete_draft(git, api, sha=os.environ.get("RELEASE_SHA", ""), version=os.environ.get("RELEASE_VERSION", ""),
                draft_id=os.environ.get("RELEASE_DRAFT_ID", ""), last_tag=os.environ.get("RELEASE_LAST_TAG", ""),
                workflow_ref=os.environ.get("GITHUB_WORKFLOW_REF", ""),
                username=os.environ.get("REGISTRY_PULL_USERNAME", ""), password=os.environ.get("REGISTRY_PULL_PASSWORD", ""))
        return 0
    except (ReleaseError, OSError, UnicodeError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

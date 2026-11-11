#!/usr/bin/env python3
"""Recover only the authenticated original release artifact, using stdlib data reads."""
from __future__ import annotations

import argparse
import http.client
import os
from pathlib import Path
import sys
import tempfile
import time
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_draft import (DraftApi, GitHubHttpError, MAX_JSON, artifact_inventory, artifact_metadata,
                           bounded, limited_read, release_trailers, strict_json)
from release_publish import caller_workflow_path, positive, producer_identity, validate_release, validate_source
from release_state import Git, NoRedirect, ReleaseError, SHA, STABLE, TRAILER, outputs
from oci_publish import Registry


class NuGet:
    """Credential-free, redirect-free exact-version NuGet content existence checks."""

    def __init__(self):
        self.opener = urllib.request.build_opener(NoRedirect())
        self.base = None

    @staticmethod
    def resource_url(value):
        if not isinstance(value, str):
            raise ReleaseError("NuGet package content resource must be a URL")
        parsed = urllib.parse.urlsplit(value)
        try:
            port = parsed.port
        except ValueError as error:
            raise ReleaseError("invalid NuGet package content resource") from error
        if (parsed.scheme != "https" or parsed.hostname != "api.nuget.org" or port not in (None, 443)
                or parsed.username or parsed.password or parsed.query or parsed.fragment
                or not parsed.path.endswith("/") or "\\" in value
                or any(part in (".", "..") for part in urllib.parse.unquote(parsed.path).split("/"))):
            raise ReleaseError("NuGet package content resource escaped the public endpoint")
        return value

    def version_absent(self, package, version):
        from release_draft import PACKAGE_ID
        if (not isinstance(package, str) or PACKAGE_ID.fullmatch(package) is None
                or not isinstance(version, str) or STABLE.fullmatch("v" + version) is None):
            raise ReleaseError("invalid NuGet package/version identity")
        deadline = time.monotonic() + 120
        def request():
            try:
                if self.base is None:
                    with self.opener.open("https://api.nuget.org/v3/index.json", timeout=60) as response:
                        if response.status != 200:
                            raise ReleaseError("NuGet service index did not return HTTP 200")
                        document = strict_json(limited_read(response, MAX_JSON))
                    if (not isinstance(document, dict) or not isinstance(document.get("resources"), list)
                            or any(not isinstance(item, dict) for item in document["resources"])):
                        raise ReleaseError("malformed NuGet service index")
                    resources = [item for item in document["resources"]
                                 if item.get("@type") == "PackageBaseAddress/3.0.0"]
                    if len(resources) != 1:
                        raise ReleaseError("NuGet package content resource is missing or ambiguous")
                    self.base = self.resource_url(resources[0].get("@id"))
                identity = package.lower()
                url = self.base + f"{identity}/{version}/{identity}.{version}.nupkg"
                try:
                    with self.opener.open(urllib.request.Request(url, method="HEAD"), timeout=60) as response:
                        if response.status != 200:
                            raise ReleaseError("NuGet package HEAD did not return HTTP 200")
                        return False
                except urllib.error.HTTPError as error:
                    with error:
                        if error.code == 404:
                            return True
                        raise ReleaseError(f"NuGet package HEAD failed: HTTP {error.code}") from error
            except urllib.error.HTTPError as error:
                with error:
                    raise ReleaseError(f"NuGet service index failed: HTTP {error.code}") from error
            except (OSError, http.client.HTTPException) as error:
                raise ReleaseError("NuGet existence transport failed") from error
        return bounded(request, deadline=deadline)


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


def destinations_absent(values, *, username="", password="", nuget=NuGet, registry=Registry):
    """Check every listed identity; errors and uncertainty retain the draft."""
    checks = []
    packages = values["inventory"]["packages"]
    public = nuget() if packages else None
    for package in packages:
        try:
            checks.append(public.version_absent(package, values["version"]) is True)
        except (ReleaseError, OSError, ValueError, http.client.HTTPException):
            checks.append(False)
    for name in values["inventory"]["containers"]:
        try:
            checks.append(registry(name, username, password).version_absent(values["version"]) is True)
        except (ReleaseError, OSError, ValueError, http.client.HTTPException):
            checks.append(False)
    return all(checks)


def complete_draft(git, api, *, sha, version, draft_id, last_tag, workflow_ref,
                   username="", password="", nuget=NuGet, registry=Registry, emit=outputs):
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
        if not destinations_absent(values, username=username, password=password, nuget=nuget, registry=registry):
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

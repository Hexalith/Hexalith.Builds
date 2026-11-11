#!/usr/bin/env python3
"""Authenticate, publish and finalize the original release artifact; stdlib only."""
from __future__ import annotations

import argparse
import http.client
import io
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import tarfile
import tempfile
import urllib.error
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))
from oci_archive import inspect_archive
from oci_publish import Registry, publish as publish_oci
from release_draft import (DIGEST, MARKERS, MAX_JSON, RESERVED, DraftApi, artifact_inventory,
                           artifact_metadata, bounded, limited_read, release_body, release_trailers,
                           strict_json, workflows_match)
from release_outcome import outcome_document, validate_outcome, write_document
from release_state import Git, ReleaseError, SHA, STABLE, outputs


class PublishApi(DraftApi):
    def publish_draft(self, identity, document):
        deadline = self.clock() + 120
        def request():
            url = self._url(f"/repos/{self.repository}/releases/{identity}", deadline)
            request = urllib.request.Request(url, method="PATCH", data=json.dumps(document).encode(),
                headers={"Authorization": "Bearer " + self.token, "Accept": "application/vnd.github+json",
                         "Content-Type": "application/json", "X-GitHub-Api-Version": "2022-11-28"})
            try:
                with self.opener.open(request, timeout=60) as response:
                    if response.status != 200:
                        raise ReleaseError("GitHub publication did not return HTTP 200")
                    return strict_json(limited_read(response, MAX_JSON))
            except urllib.error.HTTPError as error:
                with error:
                    raise ReleaseError(f"GitHub draft publication returned HTTP {error.code}") from error
            except (OSError, http.client.HTTPException) as error:
                raise ReleaseError("GitHub draft publication transport failed") from error
        return bounded(request, deadline=deadline, clock=self.clock)


def positive(value):
    if not isinstance(value, str) or not re.fullmatch(r"[1-9][0-9]*", value):
        raise ReleaseError("release ID must be a positive decimal integer")
    return int(value)


def handoff(environment):
    values = {name: environment.get("RELEASE_" + name.upper().replace("-", "_"), "") for name in
              ("publish", "sha", "version", "last-tag", "draft-id", "artifact-id", "artifact-digest", "artifact-run")}
    if (values["publish"] != "true" or not SHA.fullmatch(values["sha"])
            or not STABLE.fullmatch("v" + values["version"]) or not DIGEST.fullmatch(values["artifact-digest"])
            or (values["last-tag"] and not STABLE.fullmatch(values["last-tag"]))):
        raise ReleaseError("invalid authorized release handoff")
    for name in ("draft-id", "artifact-id", "artifact-run"):
        positive(values[name])
    return values


def validate_source(git, values):
    sha, last = values["sha"], values["last-tag"]
    if git.run("cat-file", "-t", sha).stdout.strip() != b"commit" or not git.ancestor(sha, "refs/heads/main"):
        raise ReleaseError("release source is not a commit ancestor of main")
    if last:
        base = git.run("rev-parse", "--verify", "refs/tags/" + last + "^{commit}").stdout.decode().strip()
        if not SHA.fullmatch(base) or not git.ancestor(base, sha):
            raise ReleaseError("captured last tag is not an ancestor of the built commit")
        if tuple(map(int, values["version"].split("."))) <= tuple(map(int, last[1:].split("."))):
            raise ReleaseError("release version is not above its captured last tag")
    elif values["version"] != "1.0.0":
        raise ReleaseError("a tagless release must have version 1.0.0")


def validate_release(document, values, *, draft, body=None):
    if (not isinstance(document, dict) or type(document.get("id")) is not int
            or document["id"] != positive(values["draft-id"])
            or not isinstance(document.get("author"), dict) or document["author"].get("login") != "github-actions[bot]"
            or document.get("draft") is not draft or document.get("prerelease") is not False
            or document.get("tag_name") != "v" + values["version"]
            or document.get("target_commitish") != values["sha"] or not isinstance(document.get("body"), str)):
        raise ReleaseError("live release identity/state mismatch")
    actual = document["body"]
    release_trailers(actual)
    if len(RESERVED.findall(actual)) != len(MARKERS):
        raise ReleaseError("draft release trailer identity mismatch")
    expected = release_body("", sha=values["sha"], run_id=positive(values["artifact-run"]),
        artifact_id=positive(values["artifact-id"]), digest=values["artifact-digest"], inventory=values["inventory"])
    for marker in MARKERS:
        pattern = "^" + re.escape(marker) + r":[ \t]*(.*?)\r?$"
        if re.findall(pattern, actual, re.MULTILINE) != re.findall(pattern, expected, re.MULTILINE):
            raise ReleaseError("draft release trailer identity mismatch")
    if body is not None and body != actual:
        raise ReleaseError("draft changed during publication")


def version_tag(api, values, *, required=False):
    """Only an explicit empty successful matching-refs response establishes absence."""
    tag = "v" + values["version"]
    ref = "refs/tags/" + tag
    refs = api.get(f"/repos/{api.repository}/git/matching-refs/tags/{tag}", deadline=api.clock() + 120)
    if not isinstance(refs, list) or any(not isinstance(item, dict) or not isinstance(item.get("ref"), str) for item in refs):
        raise ReleaseError("invalid live Git tag response")
    matches = [item for item in refs if item["ref"] == ref]
    if not matches and not required:
        return
    message = "tag " + tag + " does not point at the built commit"
    if len(matches) != 1:
        raise ReleaseError(message)
    target = matches[0].get("object")
    seen = set()
    for _ in range(16):
        if not isinstance(target, dict) or not SHA.fullmatch(str(target.get("sha", ""))):
            raise ReleaseError(message)
        if target.get("type") == "commit":
            if target["sha"] != values["sha"]:
                raise ReleaseError(message)
            return
        if target.get("type") != "tag" or target["sha"] in seen:
            raise ReleaseError(message)
        seen.add(target["sha"])
        response = api.get(f"/repos/{api.repository}/git/tags/{target['sha']}", deadline=api.clock() + 120)
        if not isinstance(response, dict) or response.get("sha") != target["sha"]:
            raise ReleaseError(message)
        target = response.get("object")
    raise ReleaseError(message)


def caller_workflow_path(repository, workflow_ref):
    """Authenticate the current caller's main-branch workflow identity."""
    prefix = repository + "/"
    if (not workflow_ref.startswith(prefix) or not workflow_ref.endswith("@refs/heads/main")
            or not re.fullmatch(r"\.github/workflows/[A-Za-z0-9_.-]+\.ya?ml", workflow_ref[len(prefix):].split("@")[0])):
        raise ReleaseError("publisher must be dispatched from a caller workflow on main")
    return workflow_ref[len(prefix):].split("@")[0]


def producer_identity(run, values, path):
    """Separate a well-formed identity mismatch from malformed service data."""
    if (not isinstance(run, dict) or type(run.get("id")) is not int or run["id"] < 1
            or any(not isinstance(run.get(key), str) or not run[key]
                   for key in ("path", "event", "head_branch", "head_sha"))
            or SHA.fullmatch(run["head_sha"]) is None):
        raise ReleaseError("original producer run metadata is malformed")
    return (run["id"] == positive(values["artifact-run"]) and run["path"] == path
            and run["event"] == "workflow_dispatch" and run["head_branch"] == "main"
            and run["head_sha"] == values["sha"])


def producer(api, values, workflow_ref):
    path = caller_workflow_path(api.repository, workflow_ref)
    run = api.get(f"/repos/{api.repository}/actions/runs/{values['artifact-run']}", deadline=api.clock() + 120)
    if not producer_identity(run, values, path):
        raise ReleaseError("original producer run/caller workflow/source identity mismatch")


def authenticate(git, api, values, directory, workflow_ref, *, registry=Registry, authenticated=lambda _values: None):
    validate_source(git, values)
    producer(api, values, workflow_ref)
    metadata = api.get(f"/repos/{api.repository}/actions/artifacts/{values['artifact-id']}", deadline=api.clock() + 120)
    artifact_metadata(metadata, artifact_id=positive(values["artifact-id"]), run_id=positive(values["artifact-run"]),
                      digest=values["artifact-digest"], sha=values["sha"])
    payload = api.artifact_bytes(positive(values["artifact-id"]))
    files = {}
    values["inventory"] = artifact_inventory(payload, sha=values["sha"], version=values["version"],
        digest=values["artifact-digest"], repository=api.repository, files=files)
    live = api.get(f"/repos/{api.repository}/releases/{values['draft-id']}", deadline=api.clock() + 120)
    validate_release(live, values, draft=True)
    values["body"] = live["body"]
    authenticated(values)
    version_tag(api, values)
    values["packages"], values["containers"] = [], []
    for name, data in files.items():
        destination = directory / name
        destination.parent.mkdir(exist_ok=True)
        destination.write_bytes(data)
        if name.startswith("packages/"):
            values["packages"].append(name)
        else:
            # Production handoff is uncompressed; gzip is only a build intermediate.
            try:
                with tarfile.open(fileobj=io.BytesIO(data), mode="r:"):
                    pass
            except tarfile.TarError as error:
                raise ReleaseError("release OCI archive must be an uncompressed tar") from error
            from oci_archive import archive_files, document
            container_name = document(archive_files(io.BytesIO(data))["index.json"])["manifests"][0]["annotations"]["org.opencontainers.image.ref.name"]
            container_name = container_name.removesuffix(":" + values["version"])
            graph = inspect_archive(io.BytesIO(data), revision=values["sha"], version=values["version"],
                                    repository=api.repository, name=container_name)
            values["containers"].append({"name": container_name, "path": name, "graph": graph})
    # Preflight every root before the first NuGet or registry publication write.
    for item in values["containers"]:
        registry(item["name"], os.environ.get("REGISTRY_USERNAME", ""), os.environ.get("REGISTRY_PASSWORD", "")).preflight(
            values["version"], item["graph"])
    return values


def push_packages(directory, values, key, *, run=subprocess.run):
    if values["packages"] and (not key or key.isspace()):
        raise ReleaseError("NuGet login returned an empty temporary key")
    empty = directory / "empty"
    empty.mkdir(exist_ok=True)
    environment = {name: value for name, value in os.environ.items() if name in {"PATH", "LANG", "TMPDIR"}}
    environment.update(HOME=str(empty), DOTNET_CLI_HOME=str(empty), DOTNET_NOLOGO="true",
                       DOTNET_SKIP_FIRST_TIME_EXPERIENCE="true", DOTNET_CLI_TELEMETRY_OPTOUT="true")
    for name in values["packages"]:
        try:
            result = run(["dotnet", "nuget", "push", str(directory / name), "--source",
                          "https://api.nuget.org/v3/index.json", "--skip-duplicate", "--api-key", key],
                         cwd=empty, env=environment, capture_output=True, check=False, timeout=600)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise ReleaseError("NuGet publication execution failed or timed out") from error
        if result.returncode:
            raise ReleaseError("NuGet publication failed")
        # Emit only fixed, recognized statuses; command output can contain credentials.
        output = result.stdout or b""
        if b"already exists" in output:
            print("NuGet package version already exists; skipped duplicate", flush=True)
        elif b"Your package was pushed" in output:
            print("NuGet package version published", flush=True)
        else:
            print("NuGet package push completed", flush=True)


def finalize_release(git, api, values, *, refresh=workflows_match):
    equal = refresh(git, api.repository, values["sha"])
    validate_source(git, values)
    live = api.get(f"/repos/{api.repository}/releases/{values['draft-id']}", deadline=api.clock() + 120)
    validate_release(live, values, draft=True, body=values["body"])
    version_tag(api, values)
    if not equal:
        document = outcome_document(values["sha"], "failed", values["version"])
        document.update(outcome="ready", draftId=positive(values["draft-id"]), lastTag=values["last-tag"])
        return validate_outcome(document)
    response = api.publish_draft(positive(values["draft-id"]), {
        "tag_name": "v" + values["version"], "target_commitish": values["sha"], "draft": False})
    validate_release(response, values, draft=False, body=values["body"])
    version_tag(api, values, required=True)
    return outcome_document(values["sha"], "published", values["version"])


def private_directory():
    directory = Path(tempfile.mkdtemp(prefix="hexalith-release-publish-", dir="/tmp"))
    workspace = Path(os.environ.get("GITHUB_WORKSPACE", os.getcwd())).resolve()
    if directory.is_relative_to(workspace):
        directory.rmdir()
        raise ReleaseError("publisher temporary storage must be outside the workspace")
    return directory


def write_state(directory, state):
    descriptor, name = tempfile.mkstemp(prefix=".state-", dir=directory)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8") as stream:
            json.dump(state, stream)
        temporary.replace(directory / "state.json")
    finally:
        temporary.unlink(missing_ok=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("phase", choices=("prepare", "publish", "outcome"))
    args = parser.parse_args()
    directory = None
    try:
        if sys.version_info < (3, 12):
            raise ReleaseError("Python 3.12 or later is required")
        if args.phase == "prepare":
            directory = private_directory()
            outputs({"state": str(directory)})
            state = {"document": outcome_document(os.environ.get("GITHUB_SHA", ""), "failed")}
            write_state(directory, state)
            if os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch" or os.environ.get("GITHUB_REF") != "refs/heads/main":
                raise ReleaseError("publisher must run from workflow_dispatch on main")
            values = handoff(os.environ)
            api = PublishApi(os.environ.get("GITHUB_REPOSITORY", ""), os.environ.get("GITHUB_TOKEN", ""),
                             os.environ.get("GITHUB_API_URL", "https://api.github.com"))
            (directory / "git").mkdir()
            git = Git.fetch(directory / "git", api.repository)
            def authenticated(identity):
                state["document"] = outcome_document(identity["sha"], "failed", identity["version"])
                write_state(directory, state)
            authenticate(git, api, values, directory, os.environ.get("GITHUB_WORKFLOW_REF", ""), authenticated=authenticated)
            state.update(values=values, document=outcome_document(values["sha"], "failed", values["version"]))
            write_state(directory, state)
            outputs({"packages": str(bool(values["packages"])).lower()})
        elif args.phase == "publish":
            directory = Path(os.environ["RELEASE_STATE"])
            state = strict_json((directory / "state.json").read_bytes())
            values = state["values"]
            push_packages(directory, values, os.environ.get("NUGET_API_KEY", ""))
            for item in values["containers"]:
                publish_oci(directory / item["path"], item["name"], values["version"], item["graph"],
                            os.environ.get("REGISTRY_USERNAME", ""), os.environ.get("REGISTRY_PASSWORD", ""))
            api = PublishApi(os.environ.get("GITHUB_REPOSITORY", ""), os.environ.get("GITHUB_TOKEN", ""),
                             os.environ.get("GITHUB_API_URL", "https://api.github.com"))
            state["document"] = finalize_release(Git(directory / "git"), api, values)
            write_state(directory, state)
        else:
            try:
                state_path = os.environ.get("RELEASE_STATE", "")
                document = outcome_document(os.environ.get("GITHUB_SHA", ""), "failed")
                if state_path:
                    directory = Path(state_path)
                    try:
                        state = strict_json((directory / "state.json").read_bytes())
                        document = validate_outcome(state["document"])
                    except (ReleaseError, OSError, ValueError, KeyError, TypeError) as error:
                        print(f"release publication state unavailable ({type(error).__name__}); reporting failed dispatch",
                              file=sys.stderr)
                if (os.environ.get("RELEASE_PREPARE_RESULT") != "success"
                        or os.environ.get("RELEASE_LOGIN_RESULT") not in {"success", "skipped"}
                        or os.environ.get("RELEASE_PUBLISH_RESULT") != "success"):
                    document = outcome_document(document["sha"], "failed", document.get("version", ""))
                output = private_directory()
                path = write_document(output, document)
                outputs({"path": str(path), "outcome": document["outcome"]})
            finally:
                if directory and directory.exists():
                    shutil.rmtree(directory)
        return 0
    except (ReleaseError, OSError, ValueError, KeyError, TypeError) as error:
        print("release publication failed: " + json.dumps(str(error), ensure_ascii=True), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

#!/usr/bin/env python3
"""Resolve release identity and exact-source CI using trusted stdlib code only.

Repository objects are read as data in a fresh bare repository. No checkout,
module script, filter, hook, submodule, build tool or authenticated git runs.
"""
from __future__ import annotations

import argparse
import http.client
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import BUILDS_REPOSITORY, ConfigError, load_config

SHA = re.compile(r"[0-9a-f]{40}\Z")
STABLE = re.compile(r"v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\Z")
TRAILER = re.compile(r"^hexalith-release:[ \t]*([^\r\n]*)\r?$", re.MULTILINE)


class ReleaseError(ValueError):
    """A release prerequisite could not be established."""

    def __init__(self, message):
        super().__init__("".join(f"\\u{ord(c):04x}" if ord(c) < 32 or 127 <= ord(c) <= 159
                                 or c in "\u2028\u2029" else c for c in str(message)))


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        return None


class Api:
    """Authenticated JSON GETs with same-repository, no-redirect pagination."""

    def __init__(self, repository, token, base_url="https://api.github.com", *, clock=time.monotonic):
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
            raise ReleaseError("invalid repository identity")
        parsed = urllib.parse.urlsplit(base_url)
        if (parsed.username or parsed.password or parsed.query or parsed.fragment
                or not parsed.netloc or (parsed.scheme != "https" and not
                    (parsed.scheme == "http" and parsed.hostname in ("127.0.0.1", "localhost", "::1")))):
            raise ReleaseError("API base must use HTTPS (loopback HTTP is test-only)")
        if not token:
            raise ReleaseError("a GitHub read token is required")
        self.repository, self.token, self.base_url, self.clock = repository, token, base_url.rstrip("/"), clock
        self.opener = urllib.request.build_opener(NoRedirect())
        self._repository_id = None

    def _url(self, path, deadline=None):
        url = urllib.parse.urljoin(self.base_url + "/", path.lstrip("/")) if not path.startswith(("https://", "http://")) else path
        parsed, base = urllib.parse.urlsplit(url), urllib.parse.urlsplit(self.base_url)
        root = base.path.rstrip("/") + "/repos/" + self.repository
        if ((parsed.scheme, parsed.netloc) != (base.scheme, base.netloc)
                or parsed.fragment or parsed.username or parsed.password
                or any(p in (".", "..") for p in urllib.parse.unquote(parsed.path).split("/"))):
            raise ReleaseError("API request escaped the repository credential boundary")
        if parsed.path == root or parsed.path.startswith(root + "/"):
            return url
        # GitHub's Link headers may use /repositories/<id>/... instead of
        # /repos/<owner>/<name>/.... Authenticate the repository identity via
        # its named endpoint before accepting an alias, then keep all actual
        # collection requests on that named endpoint. No numeric destination
        # receives credentials, including a mismatching repository ID.
        numeric = re.fullmatch(re.escape(base.path.rstrip("/")) + r"/repositories/([1-9][0-9]*)(/.*)", parsed.path)
        if numeric is None:
            raise ReleaseError("API request escaped the repository credential boundary")
        if self._repository_id is None:
            metadata = self.get("/repos/" + self.repository, deadline=deadline)
            if (not isinstance(metadata, dict) or type(metadata.get("id")) is not int or metadata["id"] < 1
                    or not isinstance(metadata.get("full_name"), str)
                    or metadata["full_name"].casefold() != self.repository.casefold()):
                raise ReleaseError("GitHub API repository metadata identity mismatch")
            self._repository_id = metadata["id"]
        if numeric[1] != str(self._repository_id):
            raise ReleaseError("API pagination repository ID mismatch")
        return urllib.parse.urlunsplit(parsed._replace(path=root + numeric[2]))

    def _get(self, path, deadline=None):
        if deadline is None:
            return self._request(path)
        if deadline <= self.clock():
            raise ReleaseError("GitHub API deadline exceeded")
        completed = threading.Event()
        result = []

        def request():
            try:
                result.append((True, self._request(path, deadline)))
            except BaseException as error:
                result.append((False, error))
            finally:
                completed.set()

        # Socket timeouts measure inactivity, so a dribbling response or slow
        # name resolution can outlive them. Bound acquisition and JSON reads
        # together; a timed-out daemon cannot delay the caller or process exit.
        threading.Thread(target=request, daemon=True).start()
        if not completed.wait(max(0, deadline - self.clock())) or self.clock() >= deadline:
            raise ReleaseError("GitHub API deadline exceeded")
        success, value = result[0]
        if not success:
            raise value
        return value

    def _request(self, path, deadline=None):
        url = self._url(path, deadline)
        timeout = 60 if deadline is None else min(60, deadline - self.clock())
        if timeout <= 0:
            raise ReleaseError("GitHub API deadline exceeded")
        request = urllib.request.Request(url, headers={"Authorization": "Bearer " + self.token,
            "Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"})
        try:
            with self.opener.open(request, timeout=timeout) as response:
                return json.load(response), response.headers.get("Link", "")
        except urllib.error.HTTPError as error:
            with error:
                prefix = urllib.parse.urlsplit(self.base_url).path.rstrip("/") + "/repos/" + self.repository
                endpoint = urllib.parse.urlsplit(url).path.removeprefix(prefix) or "/"
                raise ReleaseError(f"GitHub API {endpoint} returned HTTP {error.code}") from error
        except (OSError, ValueError, RecursionError, http.client.HTTPException) as error:
            raise ReleaseError("GitHub API transport or JSON decoding failed") from error

    def get(self, path, deadline=None):
        return self._get(path, deadline)[0]

    def pages(self, path, key=None, deadline=None):
        seen = set()
        while path:
            url = self._url(path, deadline)
            if url in seen:
                raise ReleaseError("GitHub API pagination cycle")
            seen.add(url)
            document, link = self._get(url, deadline)
            if key is not None:
                if not isinstance(document, dict) or key not in document:
                    raise ReleaseError("GitHub API missing paginated collection")
                document = document[key]
            if not isinstance(document, list) or any(not isinstance(item, dict) for item in document):
                raise ReleaseError("GitHub API expected a collection of objects")
            yield from document
            next_links = re.findall(r'<([^>]+)>;\s*rel="next"', link)
            if len(next_links) > 1:
                raise ReleaseError("GitHub API ambiguous pagination")
            path = next_links[0] if next_links else ""


class Git:
    """Read-only Git object operations with executable repository config disabled."""

    def __init__(self, directory):
        self.directory = Path(directory)

    def run(self, *args, allowed=(0,)):
        environment = {key: value for key, value in os.environ.items()
                       if not key.startswith(("GIT_", "GH_", "GITHUB_"))}
        environment.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull,
                           GIT_TERMINAL_PROMPT="0", GIT_NO_REPLACE_OBJECTS="1")
        result = subprocess.run(["git", "-c", "core.hooksPath=" + os.devnull,
            "-c", "protocol.ext.allow=never", "-c", "protocol.file.allow=never",
            "-C", str(self.directory), *args], env=environment, capture_output=True, check=False)
        if result.returncode not in allowed:
            # Never forward remote/config-controlled diagnostics or credentials.
            raise ReleaseError("Git object operation failed: " + args[0])
        return result

    def ancestor(self, older, newer):
        return self.run("merge-base", "--is-ancestor", older, newer, allowed=(0, 1)).returncode == 0

    def blob(self, sha, path):
        if not SHA.fullmatch(sha) or path.startswith("/") or any(p in (".", "..") for p in path.split("/")):
            raise ReleaseError("invalid source object identity")
        return self.run("cat-file", "blob", sha + ":" + path).stdout

    @classmethod
    def fetch(cls, directory, repository):
        git = cls(directory)
        git.run("init", "--bare", "--template=")
        git.run("fetch", "--no-recurse-submodules", "--no-write-fetch-head",
                "https://github.com/" + repository + ".git",
                "+refs/heads/main:refs/heads/main", "+refs/tags/*:refs/tags/*")
        return git


def highest_reachable_stable_tag(git, sha, *, exclude=""):
    """Return the highest stable tag reachable from sha, including annotated tags."""
    if not SHA.fullmatch(sha):
        raise ReleaseError("expected full source commit SHA")
    tags = git.run("tag", "--merged", sha).stdout.decode("utf-8").splitlines()
    candidates = [(tuple(map(int, match.groups())), tag) for tag in tags
                  if tag != exclude and (match := STABLE.fullmatch(tag))]
    return max(candidates)[1] if candidates else ""


def select_release(git, drafts, dispatch_sha):
    """Validate every bot trailer-bearing draft before selecting any one draft."""
    valid, invalid = [], []
    for draft in drafts:
        if draft.get("draft") is not True:
            continue
        author = draft.get("author")
        body = draft.get("body") or ""
        if not isinstance(author, dict) or author.get("login") != "github-actions[bot]":
            continue
        if not isinstance(body, str):
            raise ReleaseError("GitHub draft body must be text")
        if not TRAILER.search(body):
            continue
        identity = f"draft {draft.get('id', '?')} ({draft.get('tag_name', '?')})"
        try:
            trailers = TRAILER.findall(body)
            sha, tag, draft_id = draft.get("target_commitish", ""), draft.get("tag_name", ""), draft.get("id")
            match = STABLE.fullmatch(tag) if isinstance(tag, str) else None
            if (len(trailers) != 1 or not isinstance(sha, str) or not SHA.fullmatch(sha)
                    or trailers[0] != sha or match is None or type(draft_id) is not int or draft_id < 1
                    or draft.get("prerelease") is not False or not git.ancestor(sha, "refs/heads/main")):
                raise ReleaseError("invalid marker, stable tag or ancestry")
            last_tag = highest_reachable_stable_tag(git, sha, exclude=tag)
            version = tuple(map(int, match.groups()))
            if (not last_tag and version != (1, 0, 0)) or (last_tag and version <= tuple(map(int, STABLE.fullmatch(last_tag).groups()))):
                raise ReleaseError("draft version is not above the highest reachable stable tag")
            valid.append({"sha": sha, "mode": "complete", "version": tag[1:],
                          "draft-id": str(draft_id), "last-tag": last_tag})
        except ReleaseError as error:
            invalid.append(identity + ": " + str(error))
    if invalid:
        raise ReleaseError("invalid marked " + "; ".join(invalid))
    if len(valid) > 1:
        raise ReleaseError("multiple marked drafts: " + ", ".join(item["draft-id"] for item in valid))
    return valid[0] if valid else {"sha": dispatch_sha, "mode": "create"}


def ci_workflow(repository, workflow):
    if workflow != "ci.yml" and not (repository == BUILDS_REPOSITORY and workflow == "builds-ci.yml"):
        raise ReleaseError("ci-workflow must be ci.yml (only Builds may use builds-ci.yml)")
    return workflow


def require_green_ci(api, sha, workflow, *, clock=time.monotonic, sleeper=time.sleep):
    """Latest exact-source push/manual run governs; waiting has one fixed deadline."""
    workflow = ci_workflow(api.repository, workflow)
    deadline = clock() + 30 * 60
    path = f"/repos/{api.repository}/actions/workflows/{workflow}/runs?head_sha={sha}&per_page=100"
    while True:
        try:
            runs = [run for run in api.pages(path, key="workflow_runs", deadline=deadline)
                    if run.get("head_sha") == sha and run.get("event") in ("push", "workflow_dispatch")]
            if clock() >= deadline:
                raise ReleaseError("main is red, nothing released (CI wait exceeded 30 minutes)")
            if any(type(run.get("id")) is not int or run["id"] < 1 for run in runs):
                raise ReleaseError("GitHub CI run has invalid identity")
            latest = max(runs, key=lambda run: run["id"]) if runs else None
            if latest is None or latest.get("status") == "completed":
                if latest and latest.get("conclusion") == "success":
                    return
                raise ReleaseError("main is red, nothing released")
            if latest.get("status") not in ("queued", "in_progress", "waiting", "pending", "requested"):
                raise ReleaseError("GitHub CI run has unknown status")
            remaining = deadline - clock()
            if remaining <= 0:
                raise ReleaseError("main is red, nothing released (CI wait exceeded 30 minutes)")
            sleeper(min(60, remaining))
            if clock() >= deadline:
                raise ReleaseError("main is red, nothing released (CI wait exceeded 30 minutes)")
        except ReleaseError as error:
            if clock() >= deadline and "deadline" in str(error):
                raise ReleaseError("main is red, nothing released (CI wait exceeded 30 minutes)") from error
            raise


def outputs(values):
    path = os.environ.get("GITHUB_OUTPUT")
    if not path:
        raise ReleaseError("GITHUB_OUTPUT is required")
    with open(path, "a", encoding="utf-8") as output:
        for key, value in values.items():
            value = str(value)
            if "\r" in value or "\n" in value:
                raise ReleaseError("multiline release output refused")
            output.write(f"{key}={value}\n")


def dispatch_settings(git, sha, repository, module_root=""):
    if module_root and (repository != BUILDS_REPOSITORY or module_root != "Samples/Hexalith.Sample"):
        raise ReleaseError("module-root is a test-only Builds sample input")
    relative = (module_root + "/" if module_root else "") + ".github/hexalith-ci.json"
    with tempfile.TemporaryDirectory(prefix="hexalith-release-config-") as directory:
        path = Path(directory) / "hexalith-ci.json"
        path.write_bytes(git.blob(sha, relative))
        config = load_config(path, repository=repository, module_root=module_root)
    return {"codeql": json.dumps(config["codeql"], separators=(",", ":")),
            "nightly-hook": str("nightly" in config["hooks"]).lower(), "config-valid": "true"}


def resolve(git, api, *, sha, ref, switch, workflow="ci.yml", module_root="", delivered_sha="", emit=outputs):
    if not SHA.fullmatch(sha):
        raise ReleaseError("dispatch SHA must be a full commit")
    emit(dispatch_settings(git, sha, api.repository, module_root))
    if ref != "refs/heads/main":
        raise ReleaseError("release requires refs/heads/main")
    ci_workflow(api.repository, workflow)
    if delivered_sha and not SHA.fullmatch(delivered_sha):
        raise ReleaseError("delivered-sha must be a full commit SHA")
    selected = select_release(git, api.pages(f"/repos/{api.repository}/releases?per_page=100"), sha)
    emit(selected)
    # Story 5-1 installs the perimeter comparison here, after source selection.
    # Accepting delivered-sha does not assert or implement drift protection.
    if switch not in ("true", "false"):
        raise ReleaseError("HEXALITH_RELEASE_PUBLISH_ENABLED must be exactly true or false")
    emit({"enabled": switch})
    if switch == "false":
        print("publishing disabled")
        return selected
    if selected["mode"] == "create":
        require_green_ci(api, selected["sha"], workflow)
    return selected


def main():
    argparse.ArgumentParser(description=__doc__).parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ReleaseError("Python 3.12 or later is required")
        repository = os.environ.get("GITHUB_REPOSITORY", "")
        api = Api(repository, os.environ.get("GITHUB_TOKEN", ""), os.environ.get("GITHUB_API_URL", "https://api.github.com"))
        with tempfile.TemporaryDirectory(prefix="hexalith-release-git-") as directory:
            git = Git.fetch(directory, repository)
            resolve(git, api, sha=os.environ.get("GITHUB_SHA", ""), ref=os.environ.get("GITHUB_REF", ""),
                    switch=os.environ.get("RELEASE_SWITCH", ""), workflow=os.environ.get("RELEASE_CI_WORKFLOW", "ci.yml"),
                    module_root=os.environ.get("RELEASE_MODULE_ROOT", ""), delivered_sha=os.environ.get("RELEASE_DELIVERED_SHA", ""))
        return 0
    except (ConfigError, ReleaseError, OSError, UnicodeError) as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

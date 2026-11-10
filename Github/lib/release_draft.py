#!/usr/bin/env python3
"""Authenticate original artifact data and create a marked draft; stdlib only.

No source checkout, executable payload, build tool or registry client runs here.
API credentials never follow signed artifact download redirects.
"""
from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import http.client
import io
import json
import os
from pathlib import Path
import re
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.parse
import urllib.request
import xml.etree.ElementTree as ET
import zipfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
from release_state import Api, Git, NoRedirect, ReleaseError, SHA, STABLE, highest_reachable_stable_tag, outputs, select_release

DIGEST = re.compile(r"sha256:[0-9a-f]{64}\Z")
PACKAGE_ID = re.compile(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,99}\Z")
CONTAINER_NAME = re.compile(r"[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*(?:/[a-z0-9]+(?:(?:[._]|__|-+)[a-z0-9]+)*)*\Z")
MARKERS = ("hexalith-release", "hexalith-release-run", "hexalith-release-artifact",
           "hexalith-release-packages", "hexalith-release-containers")
RESERVED = re.compile(r"^[ \t]*(?:" + "|".join(MARKERS) + r")[ \t]*:", re.MULTILINE | re.IGNORECASE)
MAX_ARCHIVE = 2 * 1024 ** 3
MAX_MANIFEST = 1024 * 1024
MAX_JSON = 4 * 1024 * 1024


def strict_json(data):
    def pairs(values):
        result = {}
        for key, value in values:
            if key in result:
                raise ReleaseError("duplicate JSON key")
            result[key] = value
        return result
    try:
        return json.loads(data, object_pairs_hook=pairs,
                          parse_constant=lambda _value: (_ for _ in ()).throw(ReleaseError("invalid JSON constant")))
    except (ValueError, UnicodeError, RecursionError) as error:
        raise ReleaseError("invalid release JSON data") from error


def strict_successor(git, sha, version):
    """AD-17 creation is stricter than selecting an existing AD-16 draft."""
    if not isinstance(version, str) or not STABLE.fullmatch("v" + version):
        raise ReleaseError("release version must be stable major.minor.patch")
    last_tag = highest_reachable_stable_tag(git, sha)
    candidate = tuple(map(int, version.split(".")))
    if last_tag:
        major, minor, patch = map(int, STABLE.fullmatch(last_tag).groups())
        accepted = {(major + 1, 0, 0), (major, minor + 1, 0), (major, minor, patch + 1)}
    else:
        accepted = {(1, 0, 0)}
    if candidate not in accepted:
        raise ReleaseError("release version is not one exact stable increment above the reachable base")
    return last_tag


def bounded(operation, *, deadline, clock=time.monotonic):
    """Bound whole network reads, including dribbling bodies and DNS resolution."""
    if deadline <= clock():
        raise ReleaseError("GitHub API deadline exceeded")
    completed, result = threading.Event(), []
    def run():
        try:
            result.append((True, operation()))
        except BaseException as error:
            result.append((False, error))
        finally:
            completed.set()
    threading.Thread(target=run, daemon=True).start()
    if not completed.wait(max(0, deadline - clock())) or clock() >= deadline:
        raise ReleaseError("GitHub API deadline exceeded")
    success, value = result[0]
    if not success:
        raise value
    return value


def limited_read(response, limit):
    length = response.headers.get("Content-Length")
    if length is not None:
        try:
            if int(length) < 0 or int(length) > limit:
                raise ReleaseError("release response exceeds size limit")
        except ValueError as error:
            raise ReleaseError("invalid release response length") from error
    data = response.read(limit + 1)
    if len(data) > limit:
        raise ReleaseError("release response exceeds size limit")
    return data


class DraftApi(Api):
    """Restricted release POST and credential-free signed artifact download."""

    def _request(self, path, deadline=None):
        url = self._url(path, deadline)
        timeout = 60 if deadline is None else min(60, deadline - self.clock())
        if timeout <= 0:
            raise ReleaseError("GitHub API deadline exceeded")
        request = urllib.request.Request(url, headers={"Authorization": "Bearer " + self.token,
            "Accept": "application/vnd.github+json", "X-GitHub-Api-Version": "2022-11-28"})
        try:
            with self.opener.open(request, timeout=timeout) as response:
                return strict_json(limited_read(response, MAX_JSON)), response.headers.get("Link", "")
        except urllib.error.HTTPError as error:
            with error:
                prefix = urllib.parse.urlsplit(self.base_url).path.rstrip("/") + "/repos/" + self.repository
                endpoint = urllib.parse.urlsplit(url).path.removeprefix(prefix) or "/"
                raise ReleaseError(f"GitHub API {endpoint} returned HTTP {error.code}") from error
        except (OSError, http.client.HTTPException) as error:
            raise ReleaseError("GitHub API transport failed") from error

    def post_draft(self, document):
        deadline = self.clock() + 120
        def request():
            url = self._url(f"/repos/{self.repository}/releases", deadline)
            request = urllib.request.Request(url, method="POST", data=json.dumps(document).encode("utf-8"),
                headers={"Authorization": "Bearer " + self.token, "Accept": "application/vnd.github+json",
                         "Content-Type": "application/json", "X-GitHub-Api-Version": "2022-11-28"})
            try:
                with self.opener.open(request, timeout=min(60, deadline - self.clock())) as response:
                    if response.status != 201:
                        raise ReleaseError("GitHub draft creation did not return HTTP 201")
                    return strict_json(limited_read(response, MAX_JSON))
            except urllib.error.HTTPError as error:
                with error:
                    raise ReleaseError(f"GitHub API /releases returned HTTP {error.code}") from error
            except (OSError, http.client.HTTPException) as error:
                raise ReleaseError("GitHub draft creation transport failed") from error
        return bounded(request, deadline=deadline, clock=self.clock)

    def artifact_bytes(self, artifact_id):
        deadline = self.clock() + 600
        def request():
            url = self._url(f"/repos/{self.repository}/actions/artifacts/{artifact_id}/zip", deadline)
            headers = {"Authorization": "Bearer " + self.token, "Accept": "application/vnd.github+json",
                       "X-GitHub-Api-Version": "2022-11-28"}
            try:
                with self.opener.open(urllib.request.Request(url, headers=headers), timeout=60):
                    raise ReleaseError("artifact endpoint did not return a signed download redirect")
            except urllib.error.HTTPError as error:
                with error:
                    if error.code != 302:
                        raise ReleaseError(f"GitHub artifact archive returned HTTP {error.code}") from error
                    location = error.headers.get("Location", "")
            except (OSError, http.client.HTTPException) as error:
                raise ReleaseError("GitHub artifact redirect transport failed") from error
            parsed = urllib.parse.urlsplit(location)
            if (parsed.scheme != "https" or not parsed.hostname or parsed.username or parsed.password
                    or parsed.fragment):
                raise ReleaseError("artifact download location must be an uncredentialed HTTPS URL")
            try:
                with urllib.request.build_opener(NoRedirect()).open(location, timeout=60) as response:
                    return limited_read(response, MAX_ARCHIVE)
            except urllib.error.HTTPError as error:
                with error:
                    raise ReleaseError(f"signed artifact download returned HTTP {error.code}") from error
            except (OSError, http.client.HTTPException) as error:
                raise ReleaseError("signed artifact download transport failed") from error
        return bounded(request, deadline=deadline, clock=self.clock)


def artifact_metadata(metadata, *, artifact_id, run_id, digest, sha, now=None):
    """Authenticate artifact name, producer, expiry and digest before download."""
    if not isinstance(metadata, dict):
        raise ReleaseError("invalid artifact metadata")
    run = metadata.get("workflow_run")
    if (type(metadata.get("id")) is not int or metadata["id"] != artifact_id
            or metadata.get("name") != "release" or metadata.get("expired") is not False
            or metadata.get("digest") != digest or not isinstance(run, dict)
            or type(run.get("id")) is not int or run["id"] != run_id or run.get("head_sha") != sha):
        raise ReleaseError("artifact ID/name/run/digest/source identity mismatch")
    try:
        expires_at = datetime.fromisoformat(metadata.get("expires_at").replace("Z", "+00:00"))
        if expires_at.tzinfo is None or expires_at <= (now or datetime.now(timezone.utc)):
            raise ValueError("expired")
    except (AttributeError, TypeError, ValueError) as error:
        raise ReleaseError("artifact expiry could not be validated") from error


def safe_member(name):
    return (isinstance(name, str) and bool(name) and not name.startswith("/") and "\\" not in name
            and all(part not in ("", ".", "..") for part in name.split("/"))
            and not any(ord(char) < 32 or ord(char) == 127 for char in name))


class NoDeclarations(ET.TreeBuilder):
    """Reject DTDs before entity expansion, independent of XML byte encoding."""

    def doctype(self, name, pubid, system):
        raise ReleaseError("package XML declarations are forbidden")


def package_identity(data, *, package_id, version, sha):
    """Read NuGet metadata as data; reject ambiguous identities and commit stamps."""
    try:
        with zipfile.ZipFile(io.BytesIO(data)) as archive:
            names = [entry.filename for entry in archive.infolist()]
            if len(set(names)) != len(names) or any(not safe_member(name.rstrip("/")) for name in names):
                raise ReleaseError("unsafe or duplicate package member")
            specs = [entry for entry in archive.infolist() if entry.filename.endswith(".nuspec")]
            if len(specs) != 1 or "/" in specs[0].filename or specs[0].file_size > MAX_MANIFEST:
                raise ReleaseError("package must contain one bounded root nuspec")
            xml = archive.read(specs[0])
        root = ET.fromstring(xml, parser=ET.XMLParser(target=NoDeclarations()))
        metadata = [element for element in root if element.tag.rsplit("}", 1)[-1] == "metadata"]
        if len(metadata) != 1:
            raise ReleaseError("package metadata is ambiguous")
        def elements(name):
            return [element for element in metadata[0] if element.tag.rsplit("}", 1)[-1] == name]
        ids, versions, repositories = elements("id"), elements("version"), elements("repository")
        if (len(ids) != 1 or ids[0].text != package_id or len(versions) != 1 or versions[0].text != version
                or len(repositories) != 1 or repositories[0].get("commit") != sha):
            raise ReleaseError("package ID/version/repository commit mismatch")
    except (zipfile.BadZipFile, ET.ParseError, OSError, RuntimeError) as error:
        raise ReleaseError("invalid package metadata") from error


def artifact_inventory(payload, *, sha, version, digest, repository):
    """Read bounded ZIP members in memory; inventory never supplies executable code."""
    if len(payload) > MAX_ARCHIVE or "sha256:" + hashlib.sha256(payload).hexdigest() != digest:
        raise ReleaseError("downloaded artifact digest mismatch")
    try:
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            members, seen = {}, set()
            total = 0
            for entry in archive.infolist():
                name = entry.filename.removesuffix("/") if entry.is_dir() else entry.filename
                if not safe_member(name) or name in seen:
                    raise ReleaseError("unsafe or duplicate artifact member")
                seen.add(name)
                mode = (entry.external_attr >> 16) & 0o170000
                if mode not in (0, 0o040000 if entry.is_dir() else 0o100000):
                    raise ReleaseError("nonregular artifact member")
                if entry.is_dir():
                    if name not in ("packages", "containers"):
                        raise ReleaseError("unexpected artifact directory")
                    continue
                total += entry.file_size
                if entry.file_size > MAX_ARCHIVE or total > MAX_ARCHIVE:
                    raise ReleaseError("artifact payload exceeds size limit")
                members[entry.filename] = entry
            manifest_entry = members.get("release-manifest.json")
            if manifest_entry is None or manifest_entry.file_size > MAX_MANIFEST:
                raise ReleaseError("missing or oversized release manifest")
            manifest = strict_json(archive.read(manifest_entry))
            if (not isinstance(manifest, dict) or set(manifest) != {"sha", "version", "packages", "containers"}
                    or manifest["sha"] != sha or manifest["version"] != version):
                raise ReleaseError("release manifest source/version mismatch")
            expected, result = {"release-manifest.json"}, {}
            for kind, key, expression, suffix in (("packages", "id", PACKAGE_ID, ".nupkg"),
                                                  ("containers", "name", CONTAINER_NAME, ".tar")):
                values = manifest[kind]
                if not isinstance(values, list):
                    raise ReleaseError("release manifest inventory must be an array")
                identities = []
                for item in values:
                    if (not isinstance(item, dict) or set(item) != {key, "path"}
                            or not isinstance(item[key], str) or not expression.fullmatch(item[key])
                            or not safe_member(item["path"]) or not item["path"].startswith(kind + "/")
                            or item["path"].count("/") != 1 or not item["path"].endswith(suffix)
                            or item["path"] not in members or item["path"] in expected
                            or item[key].casefold() in {identity.casefold() for identity in identities}):
                        raise ReleaseError("invalid or duplicate release inventory identity/path")
                    expected.add(item["path"])
                    identities.append(item[key])
                    data = archive.read(members[item["path"]])
                    if kind == "packages":
                        package_identity(data, package_id=item[key], version=version, sha=sha)
                    else:
                        from oci_archive import inspect_archive
                        try:
                            inspect_archive(io.BytesIO(data), revision=sha, version=version,
                                            repository=repository, name=item[key])
                        except ValueError as error:
                            raise ReleaseError("invalid release container identity/graph") from error
                result[kind] = sorted(identities)
            if set(members) != expected:
                raise ReleaseError("release inventory and artifact payload disagree")
            return result
    except (zipfile.BadZipFile, OSError, RuntimeError) as error:
        raise ReleaseError("invalid release artifact archive") from error


def release_body(notes, *, sha, run_id, artifact_id, digest, inventory):
    if not isinstance(notes, str):
        raise ReleaseError("release notes must be text")
    notes = notes.replace("\r\n", "\n").replace("\r", "\n")
    if RESERVED.search(notes):
        raise ReleaseError("release notes contain a reserved release trailer")
    values = (sha, str(run_id), f"{artifact_id} {digest}", ",".join(inventory["packages"]),
              ",".join(inventory["containers"]))
    body = notes.rstrip() + ("\n\n" if notes.strip() else "") + "\n".join(
        name + ": " + value for name, value in zip(MARKERS, values)) + "\n"
    if len(body) > 120000:
        raise ReleaseError("release notes and trailers exceed the draft body limit")
    return body


def validate_draft(git, draft, *, sha, version, body):
    if not isinstance(draft, dict):
        raise ReleaseError("GitHub draft response must be an object")
    selected = select_release(git, [draft], sha)
    if (selected.get("mode") != "complete" or selected.get("sha") != sha
            or selected.get("version") != version):
        raise ReleaseError("created or reused draft release identity mismatch")
    actual = draft.get("body", "")
    if not isinstance(actual, str):
        raise ReleaseError("draft body must be text")
    if len(RESERVED.findall(actual)) != len(MARKERS):
        raise ReleaseError("draft release trailer identity mismatch")
    for name in MARKERS:
        pattern = r"^" + re.escape(name) + r":[ \t]*(.*?)\r?$"
        expected = re.findall(pattern, body, re.MULTILINE)
        matches = re.findall(pattern, actual, re.MULTILINE)
        if matches != expected or len(matches) != 1:
            raise ReleaseError("draft release trailer identity mismatch")
    return selected


def refresh_workflows(git, repository, sha):
    """Fetch fresh main immediately before writing and compare the complete tree."""
    git.run("fetch", "--no-recurse-submodules", "--no-write-fetch-head",
            "https://github.com/" + repository + ".git", "+refs/heads/main:refs/heads/main")
    if not git.ancestor(sha, "refs/heads/main"):
        raise ReleaseError("release source is no longer an ancestor of main")
    original = git.run("ls-tree", "-z", sha, "--", ".github/workflows").stdout
    current = git.run("ls-tree", "-z", "refs/heads/main", "--", ".github/workflows").stdout
    if original != current:
        raise ReleaseError("workflows changed on main during the release, nothing released")


def create_draft(git, api, *, sha, version, artifact_id, digest, run_id, notes,
                 refresh=refresh_workflows, emit=outputs):
    if (not SHA.fullmatch(sha) or not DIGEST.fullmatch(digest)
            or type(artifact_id) is not int or artifact_id < 1 or type(run_id) is not int or run_id < 1):
        raise ReleaseError("invalid release source/run/artifact identity")
    last_tag = strict_successor(git, sha, version)
    if not git.ancestor(sha, "refs/heads/main"):
        raise ReleaseError("release source is not an ancestor of main")
    metadata = api.get(f"/repos/{api.repository}/actions/artifacts/{artifact_id}", deadline=api.clock() + 120)
    artifact_metadata(metadata, artifact_id=artifact_id, run_id=run_id, digest=digest, sha=sha)
    inventory = artifact_inventory(api.artifact_bytes(artifact_id), sha=sha, version=version,
                                   digest=digest, repository=api.repository)
    body = release_body(notes, sha=sha, run_id=run_id, artifact_id=artifact_id, digest=digest, inventory=inventory)
    drafts = list(api.pages(f"/repos/{api.repository}/releases?per_page=100", deadline=api.clock() + 120))
    selected = select_release(git, drafts, sha)
    if selected["mode"] == "complete":
        draft = next(item for item in drafts if str(item.get("id")) == selected["draft-id"])
        validate_draft(git, draft, sha=sha, version=version, body=body)
    else:
        refresh(git, api.repository, sha)
        draft = api.post_draft({"tag_name": "v" + version, "target_commitish": sha,
                                "name": "v" + version, "body": body, "draft": True, "prerelease": False})
        validate_draft(git, draft, sha=sha, version=version, body=body)
    handoff = {"publish": "true", "version": version, "sha": sha, "last-tag": last_tag,
               "draft-id": str(draft["id"]), "artifact-id": str(artifact_id),
               "artifact-digest": digest, "artifact-run": str(run_id)}
    emit(handoff)
    return handoff


def main():
    argparse.ArgumentParser(description=__doc__).parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ReleaseError("Python 3.12 or later is required")
        repository = os.environ.get("GITHUB_REPOSITORY", "")
        api = DraftApi(repository, os.environ.get("GITHUB_TOKEN", ""),
                       os.environ.get("GITHUB_API_URL", "https://api.github.com"))
        with tempfile.TemporaryDirectory(prefix="hexalith-release-draft-git-") as directory:
            git = Git.fetch(directory, repository)
            create_draft(git, api, sha=os.environ.get("RELEASE_SHA", ""), version=os.environ.get("RELEASE_VERSION", ""),
                         artifact_id=int(os.environ.get("RELEASE_ARTIFACT_ID", "0")),
                         digest=os.environ.get("RELEASE_ARTIFACT_DIGEST", ""),
                         run_id=int(os.environ.get("GITHUB_RUN_ID", "0")), notes=os.environ.get("RELEASE_NOTES", ""))
        return 0
    except (ReleaseError, OSError, UnicodeError, ValueError) as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

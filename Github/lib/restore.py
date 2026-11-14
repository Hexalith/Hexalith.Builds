"""Isolated AD-8 ownership writer: fresh committed sources, one push, dispatch.

No module checkout, configuration, executable, CI repair plan or path enters
this process. The only CI result accepted is the literal drift authorization.
Production transports have fixed GitHub endpoints; tests inject Python objects.
"""
from __future__ import annotations

import base64
from dataclasses import dataclass
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from urllib.error import HTTPError, URLError
from urllib.request import HTTPRedirectHandler, Request, build_opener

sys.path.insert(0, str(Path(__file__).resolve().parent))
import expected_state as state

perimeter = state.perimeter


WORKFLOWS = ".github/workflows"
CONFIG = ".github/hexalith-ci.json"
BUILDS_PATH = "references/Hexalith.Builds"
STABLE_TAG = re.compile(r"v(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\Z")
COMMIT_MESSAGE = "build(cicd): restore owned CI/CD files\n"
MAX_BYTES = state.MAX_DOCUMENT_BYTES


class RestoreError(ValueError):
    """A failed restore transaction; secrets and raw remote errors stay private."""


@dataclass(frozen=True)
class Context:
    repository: str
    event_name: str
    ref: str
    drift: str
    workflow_ref: str
    workflow_sha: str
    workflow_repository: str
    module_root: str = ""
    expected_source: str = ""
    cancelled: str = "false"

    def validate(self) -> str:
        """Reject forbidden invocations before any source/network operation."""
        state.validate_context(self.repository, self.module_root, self.expected_source)
        if (self.repository.casefold() == state.BUILDS_REPOSITORY.casefold()
                or self.module_root or self.expected_source or self.event_name != "push"
                or self.ref != "refs/heads/main" or self.drift != "true" or self.cancelled != "false"
                or any(part in {".", ".."} for part in self.repository.split("/"))):
            raise RestoreError("ineligible restore invocation")
        prefix = state.BUILDS_REPOSITORY + "/.github/workflows/ci.yml@"
        if (self.workflow_repository != state.BUILDS_REPOSITORY
                or not self.workflow_ref.startswith(prefix)
                or not state.SHA.fullmatch(self.workflow_sha)):
            raise RestoreError("invalid trusted reusable workflow identity")
        reference = self.workflow_ref[len(prefix):]
        tag = reference.removeprefix("refs/tags/")
        if STABLE_TAG.fullmatch(tag):
            return tag
        if state.SHA.fullmatch(reference):
            if reference != self.workflow_sha:
                raise RestoreError("workflow SHA identity mismatch")
            return ""
        if (reference.startswith("refs/heads/") and len(reference) > len("refs/heads/")
                and not any(value in reference for value in ("..", "@{", "//", "\\"))
                and re.fullmatch(r"refs/heads/[A-Za-z0-9_./-]+", reference)
                and not reference.endswith(("/", ".", ".lock"))):
            return ""
        raise RestoreError("workflow ref must identify an exact stable tag, branch or full SHA")


class Git(perimeter.Git):
    """A writable private bare repository with a preserved sanitized environment."""

    def __init__(self, repository: str | Path, *, index_file: str | Path):
        super().__init__(repository, index_file=index_file)
        self.environment = state.Git(repository).environment | {"GIT_INDEX_FILE": str(Path(index_file).resolve())}

    def run(self, *arguments: str, data: bytes | None = None,
            environment: dict[str, str] | None = None, check: bool = True) -> bytes:
        env = self.environment.copy()
        if environment is not None:
            # The engine adds only its candidate index and committed attribute
            # source. No environment supplied by a caller reaches subprocesses.
            for key in ("GIT_INDEX_FILE", "GIT_ATTR_SOURCE"):
                if key in environment:
                    env[key] = environment[key]
        return self._run(arguments, env, data, check)

    def _run(self, arguments, environment, data=None, check=True):
        result = subprocess.run(["git", "-c", "core.hooksPath=" + os.devnull,
            "-c", "credential.helper=", "-c", "http.extraHeader=",
            "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
            "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never",
            "-c", "submodule.recurse=false", "-c", "fetch.recurseSubmodules=false",
            "-c", "filter.lfs.smudge=", "-c", "filter.lfs.process=", "-c", "filter.lfs.required=false",
            "-c", "commit.gpgsign=false", "-c", "user.name=github-actions[bot]",
            "-c", "user.email=41898282+github-actions[bot]@users.noreply.github.com",
            "-c", "core.attributesFile=" + os.devnull, "-c", "index.sparse=false",
            "-C", str(self.root), *arguments], input=data, capture_output=True,
            env=environment, check=False)
        if check and result.returncode:
            raise RestoreError("Git restore operation failed: " + arguments[0])
        return result.stdout if check or result.returncode == 0 else b""

    def blob(self, oid: str) -> bytes:
        if int(self.run("cat-file", "-s", oid).strip()) > MAX_BYTES:
            raise RestoreError("owned source exceeds 4 MiB limit")
        data = super().blob(oid)
        if len(data) > MAX_BYTES:
            raise RestoreError("owned source exceeds 4 MiB limit")
        return data

    def push(self, repository: str, commit: str, token: str) -> None:
        # The push alone gets this credential, scoped to GitHub HTTPS through
        # process-only Git config. It never enters argv, URL, files or readers.
        basic = base64.b64encode(("x-access-token:" + token).encode()).decode()
        env = self.environment | {"GIT_CONFIG_COUNT": "1",
            "GIT_CONFIG_KEY_0": "http.https://github.com/.extraheader",
            "GIT_CONFIG_VALUE_0": "AUTHORIZATION: basic " + basic}
        self._run(("push", "--no-verify", "--no-recurse-submodules", module_remote(repository),
                   commit + ":refs/heads/main"), env)


def module_remote(repository: str) -> str:
    return "https://github.com/" + repository + ".git"


class Transport(state.AnonymousTransport):
    def module(self, git: Git, repository: str) -> str:
        git.run("fetch", "--depth=1", "--no-tags", "--no-recurse-submodules", module_remote(repository),
                "refs/heads/main:refs/heads/main")
        git.run("symbolic-ref", "HEAD", "refs/heads/main")
        return git.run("rev-parse", "--verify", "refs/heads/main^{commit}").decode().strip()

    def push(self, git: Git, repository: str, commit: str, token: str) -> None:
        git.push(repository, commit, token)


class _NoRedirect(HTTPRedirectHandler):
    def redirect_request(self, request, fp, code, message, headers, newurl):
        raise RestoreError("CI dispatch refused a redirect")


class Dispatch:
    """One bounded POST to the fixed GitHub Actions workflow endpoint."""

    def __init__(self, opener=None):
        self.opener = opener or build_opener(_NoRedirect())

    def send(self, repository: str, token: str) -> None:
        request = Request("https://api.github.com/repos/" + repository +
            "/actions/workflows/ci.yml/dispatches", data=b'{"ref":"main"}', method="POST",
            headers={"Authorization": "Bearer " + token, "Accept": "application/vnd.github+json",
                "X-GitHub-Api-Version": "2022-11-28", "Content-Type": "application/json",
                "User-Agent": "Hexalith-Builds-restore"})
        try:
            with self.opener.open(request, timeout=30) as response:
                if len(response.read(MAX_BYTES + 1)) > MAX_BYTES:
                    raise RestoreError("CI dispatch response exceeds 4 MiB limit")
                if response.status != 204:
                    raise RestoreError("CI dispatch failed: HTTP " + str(response.status))
        except HTTPError as error:
            # Do not read error bodies or remote URLs; either may echo tokens.
            error.close()
            raise RestoreError("CI dispatch failed: HTTP " + str(error.code)) from None
        except (URLError, OSError, ValueError) as error:
            if isinstance(error, RestoreError):
                raise
            raise RestoreError("CI dispatch transport failed") from None


def _overlap(left: str, right: str) -> bool:
    return left == right or left.startswith(right + "/") or right.startswith(left + "/")


def _regular_sources(accepted: state.AcceptedState) -> None:
    """Materialize required bounded regular render blobs before index mutation."""
    for entry in accepted.document["entries"]:
        if entry["kind"] not in {"whole", "whole-dir"}:
            continue
        source = accepted.rendered_root + "/" + entry["path"]
        paths = ([source] if entry["kind"] == "whole" else
                 [path for path in accepted.expected.items if path.startswith(source + "/")])
        if not paths:
            raise RestoreError("missing expected source: " + source)
        for path in paths:
            state.read_bounded(accepted.expected, path)


def select(accepted: state.AcceptedState, actual: perimeter.Snapshot, running_tag: str):
    """Enforce restore's exclusions without changing generic ownership semantics."""
    entries = accepted.document["entries"]
    tags = {entry["buildsTag"] for entry in entries if entry["kind"] == "gitlink"}
    lockstep = bool(running_tag) and tags == {running_tag}
    if running_tag:
        oid = accepted.builds.run("rev-parse", "--verify", f"refs/tags/{running_tag}^{{}}").decode().strip()
        if accepted.builds.run("cat-file", "-t", oid).strip() != b"commit":
            raise RestoreError("running workflow tag does not resolve to a commit")
    # Compare every committed workflow path/mode, including unexpected files
    # and a file replacing the directory. Equality is not inferred from ci.yml.
    prefix = accepted.rendered_root + "/"
    wanted = {path[len(prefix):]: item for path, item in accepted.expected.items.items()
              if path.startswith(prefix + WORKFLOWS + "/")}
    current = {path: item for path, item in actual.items.items() if _overlap(path, WORKFLOWS)}
    workflows_clean = current == wanted
    protected = {WORKFLOWS}
    if not lockstep:
        protected.update((BUILDS_PATH, ".gitmodules"))
    if not lockstep or not workflows_clean:
        protected.add(CONFIG)
    selected, skipped = [], []
    for index, entry in enumerate(entries):
        kind = entry["kind"]
        if kind == "absent-glob":
            footprint = [path for path in actual.items if perimeter.wildmatch(entry["pattern"], path)]
        else:
            footprint = [entry["path"]]
        forbidden = any(_overlap(path, protected_path) for path in footprint for protected_path in protected)
        if kind in {"gitlink", "gitmodules-section"} and not lockstep:
            forbidden = True
        if forbidden:
            skipped.append(index)
        else:
            selected.append(index)
    return selected, skipped, protected


def _preserved(before, after, protected):
    for path in set(before.items) | set(after.items):
        if any(_overlap(path, target) for target in protected) and before.items.get(path) != after.items.get(path):
            raise RestoreError("candidate changes a protected path: " + state.escaped(path))


@dataclass(frozen=True)
class Result:
    parent: str
    delivered_sha: str
    commit: str = ""
    dispatched: bool = False
    skipped: tuple[int, ...] = ()


def run(context: Context, token: str, *, transport: Transport | None = None,
        dispatch: Dispatch | None = None, report=print) -> Result:
    running_tag = context.validate()
    if not isinstance(token, str) or not token or any(ord(char) < 32 or ord(char) == 127 for char in token):
        raise RestoreError("restore token is unavailable or malformed")
    transport = transport or Transport()
    dispatch = dispatch or Dispatch()
    with tempfile.TemporaryDirectory(prefix="hexalith-restore-") as directory:
        private = Path(directory)
        bare = private / "module.git"
        bare.mkdir()
        git = Git(bare, index_file=private / "index")
        git.run("init", "--bare", "--template=")
        parent = transport.module(git, context.repository)
        if not state.SHA.fullmatch(parent):
            raise RestoreError("module main did not resolve to a full commit")
        git.run("read-tree", parent)
        actual = git.snapshot(parent)
        with state.acquire_expected(repository=context.repository, transport=transport,
                additional_tags=(running_tag,) if running_tag else ()) as accepted:
            _regular_sources(accepted)
            if running_tag:
                oid = accepted.builds.run("rev-parse", "--verify", f"refs/tags/{running_tag}^{{}}").decode().strip()
                if oid != context.workflow_sha:
                    raise RestoreError("running workflow tag object does not match workflow SHA")
            selected, skipped, protected = select(accepted, actual, running_tag)
            arguments = dict(expected=accepted.expected, rendered_root=accepted.rendered_root, builds=accepted.builds)
            # Full recomputation distinguishes dispatch-only recovery from drift
            # that can only be fixed by delivery. No old repair data is used.
            full_drift = perimeter.compare(accepted.document, actual, **arguments)
            selected_drift = perimeter.compare(accepted.document, actual, selected=selected, **arguments)
            for index in skipped:
                entry = accepted.document["entries"][index]
                report("restore: skipped entry " + str(index) + " " + state.escaped(entry.get("path", entry.get("pattern"))))
            if not selected_drift:
                if not full_drift:
                    dispatch.send(context.repository, token)
                    report("restore: current main is clean; dispatched CI without a commit")
                    return Result(parent, accepted.delivered_sha, dispatched=True, skipped=tuple(skipped))
                report("restore: unchanged; remaining ownership drift requires delivery")
                return Result(parent, accepted.delivered_sha, skipped=tuple(skipped))
            perimeter.apply(accepted.document, git, selected=selected, **arguments)
            candidate = git.snapshot("index")
            _preserved(actual, candidate, protected)
            if perimeter.compare(accepted.document, candidate, selected=selected, **arguments):
                raise RestoreError("candidate ownership validation failed")
            tree = git.run("write-tree").decode().strip()
            if tree == actual.ref:
                raise RestoreError("selected ownership repairs produced no tree changes")
            commit = git.run("commit-tree", tree, "-p", parent, data=COMMIT_MESSAGE.encode()).decode().strip()
            transport.push(git, context.repository, commit, token)
            report("restore: pushed " + commit + " on " + parent + "; accepted " + accepted.delivered_sha)
            dispatch.send(context.repository, token)
            report("restore: dispatched ci.yml on main")
            return Result(parent, accepted.delivered_sha, commit, True, tuple(skipped))


def main() -> int:
    env = os.environ
    context = Context(repository=env.get("RESTORE_REPOSITORY", ""), event_name=env.get("RESTORE_EVENT_NAME", ""),
        ref=env.get("RESTORE_REF", ""), drift=env.get("RESTORE_DRIFT", ""),
        workflow_ref=env.get("RESTORE_WORKFLOW_REF", ""), workflow_sha=env.get("RESTORE_WORKFLOW_SHA", ""),
        workflow_repository=env.get("RESTORE_WORKFLOW_REPOSITORY", ""),
        module_root=env.get("RESTORE_MODULE_ROOT", ""), expected_source=env.get("RESTORE_EXPECTED_SOURCE", ""),
        cancelled=env.get("RESTORE_CANCELLED", ""))
    token = env.get("RESTORE_TOKEN", "")
    try:
        run(context, token)
        return 0
    except (RestoreError, state.ExpectedStateError, perimeter.PerimeterError, OSError, UnicodeError, ValueError) as error:
        # Remote stderr and HTTP bodies never enter errors. Redact even locally
        # authored diagnostics before printing them to the Actions command log.
        message = str(error).replace(token, "[REDACTED]") if token else str(error)
        print("restore: " + state.escaped(message), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

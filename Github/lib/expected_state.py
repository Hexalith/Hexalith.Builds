"""Read accepted delivered Git trees and compare the owned perimeter.

The workflow repository identity is the only selector for the fixed anonymous
remote. Neither module configuration nor a downloaded action directory supplies
an executable Git repository, a remote override, or an ownership path list.
"""

from __future__ import annotations

from dataclasses import dataclass
import difflib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import BUILDS_REPOSITORY, ConfigError, load_config
import perimeter
import perimeter_config


OWNERSHIP_MESSAGE = "CI/CD is owned by Hexalith.CiCd. Fix the product code, not the pipeline."
CICD_REMOTE = "https://github.com/Hexalith/Hexalith.CiCd.git"
BUILDS_REMOTE = "https://github.com/Hexalith/Hexalith.Builds.git"
SHA = re.compile(r"[0-9a-f]{40}\Z")
REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")
SAMPLE_ROOT = "Samples/Hexalith.Sample"


def escaped(value: object) -> str:
    """Keep untrusted diagnostics on one printable, non-command log line."""
    return "".join(f"\\u{ord(char):04x}" if ord(char) < 32 or 127 <= ord(char) <= 159
                   or char in "\u2028\u2029" or 0xd800 <= ord(char) <= 0xdfff else char
                   for char in str(value))


class ExpectedStateError(ValueError):
    """Expected state is unavailable or owned content is different."""

    def __init__(self, *diagnostics: object):
        self.diagnostics = tuple(escaped(line) for line in diagnostics)
        super().__init__("\n".join((*('guard: ' + line for line in self.diagnostics), OWNERSHIP_MESSAGE)))


class Git(perimeter.Git):
    """Perimeter Git with anonymous, non-executing object/transport settings."""

    def __init__(self, repository: str | Path):
        super().__init__(repository)
        # A whitelist also excludes caller tokens with nonstandard names,
        # inherited Git config, URL rewrites, askpass, and credential headers.
        self.environment = {key: value for key, value in os.environ.items()
                            if key in {"PATH", "SystemRoot", "WINDIR", "TMPDIR", "TMP", "TEMP",
                                       "LANG", "LC_ALL", "LC_CTYPE", "SSL_CERT_FILE", "SSL_CERT_DIR"}}
        self.environment.update(HOME=os.devnull, XDG_CONFIG_HOME=os.devnull,
            GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull, GIT_TERMINAL_PROMPT="0",
            GIT_ASKPASS="/bin/false", SSH_ASKPASS="/bin/false", GIT_LFS_SKIP_SMUDGE="1",
            GIT_NO_REPLACE_OBJECTS="1", GIT_LITERAL_PATHSPECS="1")

    def run(self, *arguments: str, data: bytes | None = None,
            environment: dict[str, str] | None = None, check: bool = True) -> bytes:
        if environment is not None:
            raise ExpectedStateError("Git environment overrides are unavailable in read-only comparison")
        result = subprocess.run(["git", "-c", "core.hooksPath=" + os.devnull,
            "-c", "credential.helper=", "-c", "http.extraHeader=",
            "-c", "protocol.allow=never", "-c", "protocol.https.allow=always",
            "-c", "protocol.file.allow=never", "-c", "protocol.ext.allow=never",
            "-c", "submodule.recurse=false", "-c", "fetch.recurseSubmodules=false",
            "-c", "filter.lfs.smudge=", "-c", "filter.lfs.process=", "-c", "filter.lfs.required=false",
            "-c", "index.sparse=false", "-C", str(self.root), *arguments],
            input=data, capture_output=True, env=self.environment, check=False)
        if result.returncode and check:
            # Git stderr may include credentials, server commands, or authored
            # config. A fixed operation diagnostic is sufficient and safe.
            raise ExpectedStateError("Git object/anonymous source operation failed: " + arguments[0])
        return result.stdout if check or result.returncode == 0 else b""


class AnonymousTransport:
    """Fixed production remotes; tests may inject a transport as Python data."""

    def clone(self, directory: Path, branch: str) -> Git:
        Git(directory.parent).run("clone", "--depth=1", "--filter=blob:none", "--sparse",
            "--no-checkout", "--single-branch", "--no-tags", "--template=", "--branch", branch,
            CICD_REMOTE, str(directory))
        return Git(directory)

    def deepen(self, git: Git, branch: str) -> None:
        git.run("fetch", "--unshallow", "--filter=blob:none", "--no-tags", "--no-recurse-submodules", CICD_REMOTE,
                "+refs/heads/" + branch + ":refs/remotes/origin/" + branch)

    def builds(self, directory: Path, tags: tuple[str, ...]) -> Git:
        directory.mkdir()
        git = Git(directory)
        git.run("init", "--bare", "--template=")
        git.run("fetch", "--depth=1", "--no-tags", "--no-recurse-submodules", BUILDS_REMOTE,
                *("+refs/tags/" + tag + ":refs/tags/" + tag for tag in tags))
        return git


def validate_context(repository: str, module_root: str = "", expected_source: str = "") -> None:
    if not isinstance(repository, str) or not REPOSITORY.fullmatch(repository):
        raise ExpectedStateError("invalid trusted repository identity")
    if (module_root or expected_source) and repository != BUILDS_REPOSITORY:
        raise ExpectedStateError("module-root and expected-source are test-only Builds inputs")
    if expected_source and not module_root:
        raise ExpectedStateError("expected-source requires module-root")
    if module_root and not expected_source:
        raise ExpectedStateError("a sample comparison requires both module-root and expected-source")
    if module_root and (module_root != SAMPLE_ROOT or expected_source != SAMPLE_ROOT + "/expected-source"):
        raise ExpectedStateError("unsafe or unsupported Builds sample fixture pair")


def load_ci(snapshot: perimeter.Snapshot, *, repository: str, module_root: str = "") -> dict:
    """Give the sole CI loader a private file materialized from a pinned blob."""
    path = ".github/hexalith-ci.json"
    logical = (module_root + "/" if module_root else "") + path
    item = snapshot.items.get(path)
    if item is None or item.mode not in {"100644", "100755"}:
        raise ConfigError(logical + ": committed regular configuration is unavailable")
    with tempfile.TemporaryDirectory(prefix="hexalith-guard-config-") as directory:
        source = Path(directory) / "hexalith-ci.json"
        source.write_bytes(snapshot.read(path))
        try:
            return load_config(source, repository=repository, module_root=module_root)
        except ConfigError as error:
            raise ConfigError(str(error).replace(str(source), logical)) from error


def tree_snapshot(git: Git, ref: str = "HEAD") -> perimeter.Snapshot:
    """Pin a Git tree explicitly; the perimeter library's index mode is unused."""
    if ref == "index":
        raise ExpectedStateError("guard comparison requires a committed Git tree, not the index")
    tree = git.run("rev-parse", "--verify", "--end-of-options", ref + "^{tree}").decode().strip()
    return git.snapshot(tree)


@dataclass(frozen=True)
class Comparison:
    """Pinned read-only result; diagnostics are materialized before cleanup."""

    drift: tuple[perimeter.Drift, ...]
    actual: perimeter.Snapshot
    workspace: perimeter.Snapshot
    document: dict
    rendered_root: str
    delivered_sha: str
    report: tuple[str, ...]

    def diagnostics(self) -> tuple[str, ...]:
        return self.report

    def require_clean(self) -> None:
        if self.drift:
            raise ExpectedStateError(*self.report)


def _report(drift, actual, expected, document, rendered_root, builds):
    lines = []
    for difference in drift:
        path = difference.path
        item = actual.items.get(path)
        source = rendered_root + "/" + path
        wanted = expected.items.get(source)
        description = f"{json.dumps(path, ensure_ascii=True)} [{difference.kind}]: {difference.reason}"
        if difference.reason == "mode":
            description += f" (actual {item.mode}, expected {wanted.mode})"
        elif difference.reason == "gitlink":
            entry = next(entry for entry in document["entries"]
                         if entry["kind"] == "gitlink" and entry["path"] == path)
            oid = builds.run("rev-parse", "--verify", f"refs/tags/{entry['buildsTag']}^{{}}").decode().strip()
            description += f" (actual {item.mode + ' ' + item.oid if item else 'missing'}, expected 160000 {oid} from {entry['buildsTag']})"
        elif difference.reason == "partial":
            description += " (owned partial entry is missing, malformed, or differs)"
        lines.append(escaped(description))
        if (difference.kind in {"whole", "whole-dir"} and wanted is not None
                and wanted.mode in {"100644", "100755"}
                and (item is None or item.mode in {"100644", "100755"})):
            old = (actual.read(path) or b"").decode("utf-8", "backslashreplace")
            new = expected.read(source).decode("utf-8", "backslashreplace")
            lines.extend(escaped(line) for line in difflib.unified_diff(
                old.split("\n"), new.split("\n"), fromfile="actual/" + path,
                tofile="expected/" + path, lineterm=""))
    return tuple(lines)


def compare_repository(actual: perimeter.Git, *, repository: str, ref: str = "HEAD",
                       module_root: str = "", expected_source: str = "",
                       delivered_sha: str = "", transport: AnonymousTransport | None = None) -> Comparison | None:
    """Compare an exact tree to a pinned accepted branch or committed sample."""
    validate_context(repository, module_root, expected_source)
    if repository == BUILDS_REPOSITORY and not module_root:
        return None
    if delivered_sha and not SHA.fullmatch(delivered_sha):
        raise ExpectedStateError("delivered-sha must be a full commit SHA")
    transport = transport or AnonymousTransport()
    git = Git(actual.root)
    try:
        workspace = tree_snapshot(git, ref)
        actual_tree = git.run("rev-parse", "--verify", workspace.ref + ":" + module_root).decode().strip() if module_root else workspace.ref
        actual_snapshot = git.snapshot(actual_tree) if module_root else workspace
        with tempfile.TemporaryDirectory(prefix="hexalith-expected-") as directory:
            temporary = Path(directory)
            name = module_root.rsplit("/", 1)[-1] if module_root else repository.split("/")[1]
            if module_root:
                if delivered_sha:
                    raise ExpectedStateError("delivered-sha is unavailable for the committed Builds sample fixture")
                expected_tree = git.run("rev-parse", "--verify", workspace.ref + ":" + expected_source).decode().strip()
                expected = git.snapshot(expected_tree)
                pinned = workspace.ref
            else:
                branch = "delivered/" + name
                try:
                    accepted = transport.clone(temporary / "delivered", branch)
                except ExpectedStateError as error:
                    raise ExpectedStateError("accepted branch " + branch + " could not be acquired anonymously",
                                             *error.diagnostics) from error
                tip = accepted.run("rev-parse", "--verify", "HEAD^{commit}").decode().strip()
                if not SHA.fullmatch(tip):
                    raise ExpectedStateError("delivered branch did not resolve to a commit")
                pinned = delivered_sha or tip
                if delivered_sha and delivered_sha != tip:
                    if accepted.run("rev-parse", "--is-shallow-repository").strip() == b"true":
                        try:
                            transport.deepen(accepted, branch)
                        except ExpectedStateError as error:
                            raise ExpectedStateError("cannot establish delivered-sha ancestry in " + branch,
                                                     *error.diagnostics) from error
                    # rev-list is pinned to the original live tip even if the
                    # branch advanced while its shallow history was deepened.
                    # Establish membership before resolving a caller SHA: in
                    # a partial clone, peeling an unknown object could itself
                    # trigger a promisor fetch of that unaccepted bare SHA.
                    ancestors = accepted.run("rev-list", tip).decode().splitlines()
                    if pinned not in ancestors:
                        raise ExpectedStateError("requested delivered-sha is unavailable or not an ancestor of the accepted branch")
                expected = accepted.snapshot(pinned)
            manifest = "rendered/" + name + ".perimeter.json"
            item = expected.items.get(manifest)
            if item is None or item.mode not in {"100644", "100755"}:
                raise ExpectedStateError("missing or non-regular expected perimeter: " + manifest)
            document = perimeter_config.load_bytes(expected.read(manifest), source=manifest)
            tags = tuple(sorted({entry["buildsTag"] for entry in document["entries"] if entry["kind"] == "gitlink"}))
            try:
                builds = transport.builds(temporary / "builds", tags) if tags else git
            except ExpectedStateError as error:
                raise ExpectedStateError("trusted Builds tag objects are unavailable: " + ", ".join(tags),
                                         *error.diagnostics) from error
            rendered_root = "rendered/" + name
            drift = tuple(perimeter.compare(document, actual_snapshot, expected=expected,
                                             rendered_root=rendered_root, builds=builds))
            return Comparison(drift, actual_snapshot, workspace, document, rendered_root, pinned,
                              _report(drift, actual_snapshot, expected, document, rendered_root, builds))
    except ExpectedStateError:
        raise
    except (perimeter.PerimeterError, perimeter_config.ConfigError, OSError, UnicodeError) as error:
        raise ExpectedStateError("expected-state comparison unavailable: " + str(error)) from error

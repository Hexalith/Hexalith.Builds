#!/usr/bin/env python3
"""Materialize a protected cone sparse-index checkout from validated CI inputs.

The excluded tree IDs are immutable archive identities, never ignore patterns.
All Git commands keep NTFS protection enabled; complete-index expansion and the
parent submodule helper are deliberately unavailable in this opt-in path.
"""

from __future__ import annotations

import base64
import ctypes
from dataclasses import dataclass
from functools import cmp_to_key
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from ci_config import ConfigError, _object, _constant, validate_windows_checkout


OID = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")
# GitHub installation tokens can be opaque or JWT-formatted. Accept either as an opaque credential.
JOB_TOKEN = re.compile(r"ghs_[A-Za-z0-9._-]{36,}\Z")
SELECTORS = ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY",
             "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR", "GIT_NAMESPACE",
             "GIT_CONFIG", "GIT_CONFIG_COUNT", "GIT_CONFIG_PARAMETERS")
GIT_NULL = "/dev/null"


def job_authorization_header() -> str | None:
    """Use the hosted job token only as a command-local GitHub HTTPS header."""
    if os.environ.get("GITHUB_ACTIONS") != "true":
        return None
    token = os.environ.get("GITHUB_TOKEN", "")
    if JOB_TOKEN.fullmatch(token) is None:
        raise ConfigError("hosted Windows checkout requires the job token")
    credential = base64.b64encode(b"x-access-token:" + token.encode("ascii")).decode("ascii")
    return "AUTHORIZATION: basic " + credential


def describe_git_failure(args: tuple[str, ...], result: subprocess.CompletedProcess[bytes]) -> str:
    """Classify a Git failure without echoing remotes, credentials or stderr."""
    text = (result.stderr or b"")[:2048].decode("utf-8", "replace").lower()
    if "authentication failed" in text or "error: 401" in text or "error: 403" in text:
        kind = "auth"
    elif "could not resolve host" in text:
        kind = "dns"
    elif "not our ref" in text or "couldn't find remote ref" in text:
        kind = "ref"
    elif "unable to access" in text and ("nul" in text or "/dev/null" in text):
        kind = "config-device"
    elif "unable to access" in text:
        kind = "transport"
    else:
        kind = "command"
    return f"protected Git {args[0]} failed ({result.returncode}/{kind})"


@dataclass(frozen=True, order=True)
class Entry:
    """An exact NUL-delimited Git tree/index record."""

    path: str
    mode: str
    oid: str


def regular_path(root: Path) -> None:
    """Reject reparse/symlink traversal, including through an existing ancestor."""
    for part in (root, *root.parents):
        if part.is_symlink() or (part.exists() and getattr(part.lstat(), "st_file_attributes", 0) & 1024):
            raise ConfigError("checkout path traverses a symbolic link or reparse point")


class Git:
    """Isolated native Git with command-local path protections and no hooks."""

    def __init__(self, root: Path, executable: str = "git", extra_header: str | None = None):
        self.root, self.executable, self.extra_header = root, executable, extra_header
        self.environment = {key: value for key, value in os.environ.items()
                            if key in {"PATH", "SYSTEMROOT", "WINDIR", "TMPDIR", "TMP", "TEMP",
                                       "LANG", "LC_ALL", "LC_CTYPE", "SSL_CERT_FILE", "SSL_CERT_DIR"}}
        self.environment.update(GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=GIT_NULL,
                                GIT_TERMINAL_PROMPT="0", GIT_NO_REPLACE_OBJECTS="1",
                                GIT_LITERAL_PATHSPECS="1", GIT_LFS_SKIP_SMUDGE="1")

    def run(self, *args: str, data: bytes | None = None, index: Path | None = None,
            quiet: bool = False) -> bytes:
        environment = self.environment.copy()
        if index is not None:
            environment["GIT_INDEX_FILE"] = str(index)
        header = self.extra_header or ""
        result = subprocess.run([self.executable, "-c", "core.protectNTFS=true", "-c", "core.longpaths=true",
            "-c", "core.hooksPath=" + GIT_NULL, "-c", "credential.helper=",
            "-c", "http.extraHeader=" + header,
            "-c", "protocol.allow=never", "-c", "protocol.https.allow=always", "-c", "protocol.file.allow=never",
            "-c", "submodule.recurse=false", "-c", "fetch.recurseSubmodules=false",
            "-c", "filter.lfs.smudge=", "-c", "filter.lfs.process=", "-c", "filter.lfs.required=false",
            "-C", str(self.root), *args], env=environment, input=data, capture_output=True, check=False)
        if result.returncode or (quiet and result.stderr):
            # Do not echo authored remote configuration, credentials or terminal controls.
            raise ConfigError(describe_git_failure(args, result))
        return result.stdout


def records(output: bytes, *, index: bool = False) -> tuple[Entry, ...]:
    """Parse strict UTF-8 path records; quoting and line splitting are forbidden."""
    entries = []
    if output and not output.endswith(b"\0"):
        raise ConfigError("unterminated Git metadata")
    for row in output.split(b"\0")[:-1]:
        try:
            metadata, path = row.decode("utf-8", errors="strict").split("\t", 1)
            mode, middle, last = metadata.split(" ")
        except (UnicodeError, ValueError) as error:
            raise ConfigError("malformed Git metadata") from error
        oid = middle if index else last
        if (mode not in {"040000", "100644", "100755", "120000", "160000"}
                or OID.fullmatch(oid) is None or (index and last != "0")
                or (not index and middle != ({"040000": "tree", "160000": "commit"}.get(mode, "blob")))
                or not path or path.startswith("/") or "\\" in path
                or any(part in ("", ".", "..") for part in path.rstrip("/").split("/"))
                or any(ord(char) < 32 or 127 <= ord(char) <= 159 for char in path)):
            raise ConfigError("unsafe or unsupported Git metadata")
        entries.append(Entry(path.rstrip("/") if mode == "040000" else path, mode, oid))
    if len({entry.path for entry in entries}) != len(entries):
        raise ConfigError("duplicate Git metadata path")
    return tuple(sorted(entries))


def ordinal_compare(first: str, second: str) -> int:
    """Use the Windows ordinal comparison at the production trust boundary."""
    if os.name == "nt":
        compare = ctypes.windll.kernel32.CompareStringOrdinal
        compare.argtypes = (ctypes.c_wchar_p, ctypes.c_int, ctypes.c_wchar_p, ctypes.c_int, ctypes.c_bool)
        compare.restype = ctypes.c_int
        value = compare(first, len(first.encode("utf-16-le")) // 2,
                        second, len(second.encode("utf-16-le")) // 2, True)
        if value == 0:
            raise ConfigError("Windows ordinal path comparison failed")
        return value - 2
    # Portable unit qualification; main refuses non-native production execution.
    return (first.casefold() > second.casefold()) - (first.casefold() < second.casefold())


def validate_complement(entries: tuple[Entry, ...]) -> None:
    """Detect case and file/directory aliases before native index insertion."""
    paths = set()
    leaves = {entry.path for entry in entries if entry.mode != "040000"}
    for entry in entries:
        parts = entry.path.split("/")
        for count in range(1, len(parts) + 1):
            path = "/".join(parts[:count])
            # Reuse the Windows name validator without claiming any exclusion.
            validate_windows_checkout({"exclude": [{"path": path, "tree": "0" * 40}]})
            if count < len(parts) and path in leaves:
                raise ConfigError("file/directory prefix collision in materialized source")
            paths.add(path)
    ordered = sorted(paths, key=cmp_to_key(ordinal_compare))
    for first, second in zip(ordered, ordered[1:]):
        if ordinal_compare(first, second) == 0:
            raise ConfigError("Windows ordinal case collision in materialized source")


def cone_patterns(tree: tuple[Entry, ...], excluded: frozenset[str]) -> str:
    """Compute parent-file-preserving cone states from actual tree nodes."""
    directories = {entry.path for entry in tree if entry.mode == "040000"}
    def escape(value: str) -> str:
        # Match Git's cone writer: escaping a closing bracket makes the cone
        # parser reject the whole definition and expand the ordinary index.
        return "".join("\\" + char if char in "\\[*?" else char for char in value)
    patterns = ["/*", "!/*/"]
    def visit(parent: str) -> None:
        children = sorted(path for path in directories if path.rpartition("/")[0] == parent)
        for path in children:
            if path in excluded:
                continue
            patterns.append("/" + escape(path) + "/")
            if any(item.startswith(path + "/") for item in excluded):
                patterns.append("!/" + escape(path) + "/*/")
                visit(path)
    visit("")
    return "\n".join(patterns) + "\n"


def materialize(git: Git, revision: str, checkout: dict, scratch: Path,
                *, protected: tuple[str, ...] = ()) -> tuple[Entry, ...]:
    """Validate and materialize an already fetched, never checked-out commit."""
    validate_windows_checkout(checkout, protected=protected)
    if OID.fullmatch(revision) is None:
        raise ConfigError("checkout requires an exact full lowercase commit ID")
    if git.run("rev-parse", "--verify", revision + "^{commit}").decode().strip() != revision:
        raise ConfigError("requested checkout is not the exact commit")
    tree = records(git.run("ls-tree", "-r", "-t", "-z", revision))
    metadata = {entry.path: entry for entry in tree}
    excluded = frozenset(entry["path"] for entry in checkout["exclude"])
    for item in checkout["exclude"]:
        if metadata.get(item["path"]) != Entry(item["path"], "040000", item["tree"]):
            raise ConfigError("excluded directory tree identity differs from validated CI input")
        if any(entry.mode in {"120000", "160000"} and entry.path.startswith(item["path"] + "/") for entry in tree):
            raise ConfigError("excluded archive contains a symbolic link or gitlink")
    leaves = tuple(entry for entry in tree if entry.mode != "040000")
    complement = tuple(entry for entry in leaves if not any(entry.path.startswith(path + "/") for path in excluded))
    validate_complement(complement)
    # An independent native protected index proves no materialized entry is dropped.
    index = scratch / "protected-index"
    if index.exists():
        raise ConfigError("protected index scratch is occupied")
    payload = b"".join(f"{entry.mode} {entry.oid}\t{entry.path}\0".encode("utf-8") for entry in complement)
    try:
        git.run("read-tree", "--empty", index=index, quiet=True)
        git.run("update-index", "-z", "--index-info", data=payload, index=index, quiet=True)
        if records(git.run("ls-files", "--stage", "-z", index=index, quiet=True), index=True) != complement:
            raise ConfigError("native protected index did not preserve complete source metadata")
    finally:
        index.unlink(missing_ok=True)
        Path(str(index) + ".lock").unlink(missing_ok=True)
    for key in ("core.sparseCheckout", "core.sparseCheckoutCone", "index.sparse"):
        git.run("config", key, "true")
    gitdir = Path(git.run("rev-parse", "--absolute-git-dir").decode().strip())
    (gitdir / "info").mkdir(exist_ok=True)
    (gitdir / "info/sparse-checkout").write_text(cone_patterns(tree, excluded), encoding="utf-8", newline="\n")
    git.run("checkout", "--force", "--detach", revision)
    if git.run("rev-parse", "HEAD").decode().strip() != revision:
        raise ConfigError("checkout HEAD changed")
    if git.run("write-tree").strip() != git.run("rev-parse", "HEAD^{tree}").strip():
        raise ConfigError("protected checkout changed the committed tree")
    expanded = []
    sparse = records(git.run("ls-files", "--sparse", "--stage", "-z"), index=True)
    omitted = set()
    for entry in sparse:
        if entry.mode == "040000":
            if entry.path not in excluded or metadata.get(entry.path) != entry:
                raise ConfigError("sparse index hides an unapproved tree")
            omitted.add(entry.path)
            expanded.extend(Entry(entry.path + "/" + child.path, child.mode, child.oid)
                            for child in records(git.run("ls-tree", "-r", "-z", entry.oid)))
        else:
            expanded.append(entry)
    if omitted != excluded or tuple(sorted(expanded)) != leaves:
        raise ConfigError("sparse metadata differs from complete committed tree")
    for entry in complement:
        path = git.root / entry.path
        if not path.exists() and not path.is_symlink():
            raise ConfigError("protected checkout omitted a materialized source path")
    if any((git.root / path).exists() for path in excluded):
        raise ConfigError("excluded archive was materialized")
    if git.run("status", "--porcelain", "-z", "--untracked-files=all"):
        raise ConfigError("protected checkout is not clean")
    return leaves


def initialize_dependencies(git: Git, paths: list[str], entries: tuple[Entry, ...]) -> None:
    """Create only exact requested direct dependencies without the parent helper."""
    if not isinstance(paths, list) or any(not isinstance(path, str) for path in paths):
        raise ConfigError("direct dependencies must be a JSON string array")
    if len(paths) != len(set(path.casefold() for path in paths)):
        raise ConfigError("duplicate direct dependency paths")
    if not paths:
        return
    rows = git.run("config", "-z", "--blob", "HEAD:.gitmodules", "--get-regexp", r"^submodule\..*\.(path|url)$")
    declarations = {}
    for record in rows.decode("utf-8", errors="strict").split("\0")[:-1]:
        key, value = record.split("\n", 1)
        name, _, field = key.rpartition(".")
        if field in declarations.setdefault(name, {}):
            raise ConfigError("duplicate submodule declaration")
        declarations[name][field] = value
    pin = {entry.path: entry.oid for entry in entries if entry.mode == "160000"}
    by_path = {}
    for declaration in declarations.values():
        if set(declaration) != {"path", "url"} or declaration["path"] in by_path:
            raise ConfigError("ambiguous direct submodule declaration")
        by_path[declaration["path"]] = declaration["url"]
    for path in paths:
        validate_windows_checkout({"exclude": [{"path": path, "tree": "0" * 40}]})
        url = by_path.get(path, "")
        if (path not in pin or re.fullmatch(r"https://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\.git", url) is None
                or url.rsplit("/", 1)[1][:-4] != path.rsplit("/", 1)[-1]):
            raise ConfigError("direct dependency is not an exact declared public GitHub gitlink")
        root = git.root / path
        regular_path(root)
        if root.exists() and (not root.is_dir() or any(root.iterdir())):
            raise ConfigError("direct dependency checkout is unexpectedly occupied")
        root.mkdir(parents=True, exist_ok=True)
        child = Git(root, git.executable, extra_header=git.extra_header)
        child.run("init", "--template=")
        child.run("remote", "add", "origin", url)
        child.run("fetch", "--no-recurse-submodules", "--no-tags", "--depth=1", "origin", pin[path])
        child.run("checkout", "--force", "--detach", pin[path])
        if child.run("rev-parse", "HEAD").decode().strip() != pin[path] or child.run("status", "--porcelain", "-z"):
            raise ConfigError("direct dependency pin or cleanliness changed")
    if git.run("status", "--porcelain", "-z") or git.run("write-tree").strip() != git.run("rev-parse", "HEAD^{tree}").strip():
        raise ConfigError("direct dependency initialization changed the parent tree")


def fetch_source(git: Git, revision: str) -> None:
    """Fetch the exact source commit with complete ancestry for retained product reads."""
    git.run("fetch", "--no-recurse-submodules", "--no-tags", "origin", revision)
    if git.run("rev-parse", "--is-shallow-repository").strip() != b"false":
        raise ConfigError("protected source checkout requires complete ancestor history")
    git.run("rev-list", "--count", revision)


def main() -> int:
    try:
        if sys.version_info < (3, 12) or os.name != "nt":
            raise ConfigError("protected checkout requires native Windows Python 3.12 or later")
        if any(key in os.environ for key in SELECTORS):
            raise ConfigError("ambient Git repository/index/config selectors are forbidden")
        root = Path(os.environ["GITHUB_WORKSPACE"])
        regular_path(root)
        if not root.is_dir() or any(root.iterdir()):
            raise ConfigError("protected checkout requires a fresh empty workspace")
        git = Git(root, extra_header=job_authorization_header())
        if b".windows." not in git.run("--version"):
            raise ConfigError("protected checkout requires native Windows Git")
        repository = os.environ["GITHUB_REPOSITORY"]
        if REPOSITORY.fullmatch(repository) is None:
            raise ConfigError("invalid running repository identity")
        revision = os.environ["CI_PREPARE_REVISION"]
        if OID.fullmatch(revision) is None:
            raise ConfigError("validated exact source revision is required")
        checkout = json.loads(os.environ["CI_PREPARE_WINDOWS_CHECKOUT"], object_pairs_hook=_object, parse_constant=_constant)
        submodules = json.loads(os.environ["CI_PREPARE_SUBMODULES_JSON"], object_pairs_hook=_object, parse_constant=_constant)
        if not isinstance(submodules, list) or any(not isinstance(path, str) for path in submodules):
            raise ConfigError("direct dependencies must be a JSON string array")
        protected = tuple(filter(None, (".github", ".gitmodules", os.environ["CI_PREPARE_SOLUTION"],
            os.environ["CI_PREPARE_GLOBAL_JSON"], os.environ.get("CI_PREPARE_MODULE_MANIFEST", ""), *submodules)))
        validate_windows_checkout(checkout, protected=protected)
        if checkout is None:
            raise ConfigError("protected checkout requires nonnull validated archive inputs")
        git.run("init", "--template=")
        git.run("remote", "add", "origin", "https://github.com/" + repository + ".git")
        fetch_source(git, revision)
        with tempfile.TemporaryDirectory(prefix="hexalith-protected-checkout-", dir=os.environ["RUNNER_TEMP"]) as temporary:
            entries = materialize(git, revision, checkout, Path(temporary), protected=protected)
            initialize_dependencies(git, submodules, entries)
        return 0
    except (ConfigError, OSError, UnicodeError, ValueError, KeyError) as error:
        print(ConfigError(str(error)), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

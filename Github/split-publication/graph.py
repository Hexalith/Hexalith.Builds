"""Structural AD-5 graph validation and exact-object reconstruction.

Semantic profile evaluation remains the FrontComposer canonical engine. This module
reconstructs the v1 edge set and raw Builds catalog hashes from Git objects.
"""

from __future__ import annotations

import subprocess
from pathlib import Path
from typing import Any, Protocol

from canonical import (
    ContractError,
    canonical_digest,
    expect_commit,
    expect_identity,
    expect_members,
    expect_path,
    expect_safe_integer,
    expect_sha256,
    expect_string,
    sha256_bytes,
)


GRAPH_SCHEMA = "hexalith.dependency-graph.v1"
BUILDS_IDENTITY = "github.com/hexalith/hexalith.builds"
CATALOG_PATH = "Props/Directory.Packages.props"
MAX_EDGES = 4096
MAX_LS_TREE_BYTES = 64 * 1024 * 1024
MAX_GITMODULES_BYTES = 1024 * 1024
MAX_CATALOG_BYTES = 4 * 1024 * 1024


class ObjectReader(Protocol):
    """Read-only Git object access keyed by normalized repository identity."""

    def ls_tree(self, identity: str, commit: str) -> list[tuple[str, str, str]]:
        """Return mode, object id, and path rows for one commit."""

    def blob(self, identity: str, commit: str, path: str, limit: int) -> bytes:
        """Return one blob after its size has been measured against a ceiling."""


def _normalize_remote(value: str, context: str) -> str:
    text = value.strip()
    if any(ord(character) < 32 or ord(character) == 127 or character == "%" for character in text):
        raise ContractError(f"{context} remote is not a closed GitHub identity")
    prefixes = (
        ("https://github.com/", "https://github.com/"),
        ("git@github.com:", "git@github.com:"),
        ("ssh://git@github.com/", "ssh://git@github.com/"),
    )
    matched = next((prefix for prefix, literal in prefixes if text.startswith(literal)), None)
    if matched is None or "@" in text[len(matched):] and matched.startswith("https"):
        raise ContractError(f"{context} remote is not a closed GitHub identity")
    remainder = text[len(matched):]
    if any(marker in remainder for marker in (":", "?", "#")):
        raise ContractError(f"{context} remote contains a port, query, or fragment")
    if remainder.endswith("/"):
        remainder = remainder[:-1]
    if remainder.endswith(".git"):
        remainder = remainder[:-4]
    if "/" not in remainder or remainder.endswith(".git") or remainder.startswith(".") or "/." in remainder:
        raise ContractError(f"{context} remote is not a closed GitHub identity")
    owner, repository = remainder.split("/", 1)
    if not owner or not repository or "." in (owner[:1], repository[:1]) or "/" in repository:
        raise ContractError(f"{context} remote is not a closed GitHub identity")
    return f"github.com/{owner.lower()}/{repository.lower()}"


def _parse_gitmodules(payload: bytes) -> dict[str, str]:
    if len(payload) > MAX_GITMODULES_BYTES:
        raise ContractError("committed .gitmodules exceeds the 1 MiB ceiling")
    try:
        text = payload.decode("utf-8")
    except UnicodeDecodeError as error:
        raise ContractError("committed .gitmodules is not UTF-8") from error
    mapping: dict[str, str] = {}
    section: str | None = None
    path = ""
    url = ""

    def finish() -> None:
        nonlocal path, url
        if section is None:
            return
        if not path or not url or path in mapping:
            raise ContractError("committed .gitmodules contains a missing or duplicate mapping")
        expect_path(path, ".gitmodules path", max_depth=16, max_bytes=240)
        mapping[path] = _normalize_remote(url, path)
        path = ""
        url = ""

    for raw_line in text.splitlines():
        line = raw_line.strip()
        if not line or line.startswith("#") or line.startswith(";"):
            continue
        if line.startswith("[submodule ") and line.endswith("]"):
            finish()
            section = line
            continue
        if section is None or "=" not in line:
            raise ContractError("committed .gitmodules is malformed")
        key, value = (part.strip() for part in line.split("=", 1))
        if key == "path":
            path = value
        elif key == "url":
            url = value
    finish()
    if len(set(mapping.values())) != len(mapping):
        raise ContractError("committed .gitmodules contains duplicate normalized identities")
    return mapping


def validate_graph(value: Any) -> dict[str, Any]:
    """Validate a closed v1 graph envelope and recompute its digest."""

    envelope = expect_members(value, {"schema", "root", "edge_count", "edges", "graph_digest"}, "dependency graph")
    if envelope["schema"] != GRAPH_SCHEMA:
        raise ContractError(f"dependency graph schema must be {GRAPH_SCHEMA}")
    root = expect_members(envelope["root"], {"repository", "commit"}, "dependency graph root")
    root_identity = expect_identity(root["repository"], "dependency graph root repository")
    root_commit = expect_commit(root["commit"], "dependency graph root commit")
    edges = envelope["edges"]
    if not isinstance(edges, list):
        raise ContractError("dependency graph edges must be an array")
    if expect_safe_integer(envelope["edge_count"], "dependency graph edge_count") != len(edges) or len(edges) > MAX_EDGES:
        raise ContractError("dependency graph edge_count is inconsistent or over the ceiling")
    normalized = []
    seen: set[tuple[str, str, str]] = set()
    for index, edge in enumerate(edges):
        repository = edge.get("repository") if isinstance(edge, dict) else None
        builds_edge = repository == BUILDS_IDENTITY
        members = {"owner_repository", "owner_commit", "path", "repository", "commit", "depth"}
        if builds_edge:
            members |= {"catalog_sha256", "catalog_contract_version"}
        row = expect_members(edge, members, f"dependency graph edge[{index}]")
        owner = expect_identity(row["owner_repository"], f"edge[{index}] owner")
        owner_commit = expect_commit(row["owner_commit"], f"edge[{index}] owner commit")
        path = expect_path(row["path"], f"edge[{index}] path", max_depth=16, max_bytes=240)
        identity = expect_identity(row["repository"], f"edge[{index}] repository")
        commit = expect_commit(row["commit"], f"edge[{index}] commit")
        depth = row["depth"]
        if isinstance(depth, bool) or depth not in (1, 2):
            raise ContractError(f"edge[{index}] depth must be integer 1 or 2")
        if depth == 1 and (owner != root_identity or owner_commit != root_commit):
            raise ContractError(f"edge[{index}] depth-1 owner does not match the graph root")
        key = (owner, owner_commit, path)
        if key in seen:
            raise ContractError(f"edge[{index}] duplicates another edge identity")
        seen.add(key)
        normalized_edge: dict[str, Any] = {
            "owner_repository": owner,
            "owner_commit": owner_commit,
            "path": path,
            "repository": identity,
            "commit": commit,
            "depth": depth,
        }
        if builds_edge:
            normalized_edge["catalog_sha256"] = expect_sha256(row["catalog_sha256"], f"edge[{index}] catalog")
            if row["catalog_contract_version"] is not None:
                version = expect_string(row["catalog_contract_version"], f"edge[{index}] catalog contract")
                if len(version) > 64 or not version[:1].isalnum():
                    raise ContractError(f"edge[{index}] catalog contract version is not a closed token")
            normalized_edge["catalog_contract_version"] = row["catalog_contract_version"]
        normalized.append(normalized_edge)
    sort_key = lambda edge: (
        edge["depth"],
        edge["owner_repository"],
        edge["owner_commit"],
        edge["path"],
        edge["repository"],
        edge["commit"],
    )
    if normalized != sorted(normalized, key=sort_key):
        raise ContractError("dependency graph edges are not in canonical order")
    digest_material = {
        "schema": GRAPH_SCHEMA,
        "root": {"repository": root_identity, "commit": root_commit},
        "edge_count": len(normalized),
        "edges": normalized,
    }
    digest = canonical_digest(digest_material)
    if expect_sha256(envelope["graph_digest"], "dependency graph digest") != digest:
        raise ContractError("dependency graph digest does not match its canonical bytes")
    return {**digest_material, "graph_digest": digest}


def _gitlinks(reader: ObjectReader, identity: str, commit: str) -> list[tuple[str, str]]:
    rows = reader.ls_tree(identity, commit)
    if sum(len(path.encode("utf-8")) + len(mode) + len(object_id) + 3 for mode, object_id, path in rows) > MAX_LS_TREE_BYTES:
        raise ContractError(f"{identity}@{commit} tree listing exceeds the 64 MiB ceiling")
    links = []
    for mode, object_id, path in rows:
        if mode != "160000":
            continue
        links.append((expect_path(path, f"{identity} gitlink", max_depth=16, max_bytes=240), expect_commit(object_id, path)))
    return links


def verify_live(reader: ObjectReader, sealed: dict[str, Any], trusted: set[str]) -> dict[str, Any]:
    """Reconstruct the v1 graph and require it to match the sealed envelope."""

    expected = validate_graph(sealed)
    actual = reconstruct_graph(reader, expected["root"]["repository"], expected["root"]["commit"], trusted)
    if actual != expected:
        raise ContractError("live dependency graph does not match the sealed CI handoff graph")
    return actual


def reconstruct_graph(reader: ObjectReader, root_identity: str, root_commit: str, trusted: set[str]) -> dict[str, Any]:
    """Reconstruct a v1 graph from exact Git objects and trusted identities."""

    if root_identity not in trusted:
        raise ContractError(f"{root_identity} is not an active-policy trusted identity")
    edges: list[dict[str, Any]] = []

    def add_edges(owner: str, owner_commit: str, depth: int) -> list[tuple[str, str]]:
        mapping: dict[str, str] = {}
        try:
            mapping = _parse_gitmodules(reader.blob(owner, owner_commit, ".gitmodules", MAX_GITMODULES_BYTES))
        except FileNotFoundError:
            mapping = {}
        discovered = []
        for path, commit in _gitlinks(reader, owner, owner_commit):
            if path not in mapping:
                raise ContractError(f"{owner}@{owner_commit} gitlink {path} has no .gitmodules mapping")
            identity = mapping[path]
            if identity not in trusted:
                raise ContractError(f"{owner}@{owner_commit} gitlink {path} resolves to untrusted {identity}")
            edge: dict[str, Any] = {
                "owner_repository": owner,
                "owner_commit": owner_commit,
                "path": path,
                "repository": identity,
                "commit": commit,
                "depth": depth,
            }
            if identity == BUILDS_IDENTITY:
                catalog = reader.blob(identity, commit, CATALOG_PATH, MAX_CATALOG_BYTES)
                edge["catalog_sha256"] = sha256_bytes(catalog)
                edge["catalog_contract_version"] = None
            edges.append(edge)
            discovered.append((identity, commit))
        mapped_paths = set(mapping)
        link_paths = {path for path, _commit in _gitlinks(reader, owner, owner_commit)}
        if mapped_paths != link_paths:
            raise ContractError(f"{owner}@{owner_commit} .gitmodules mappings do not match its gitlinks")
        return discovered

    depth1 = add_edges(root_identity, root_commit, 1)
    seen_owners: set[tuple[str, str]] = set()
    for identity, commit in depth1:
        if (identity, commit) in seen_owners:
            continue
        seen_owners.add((identity, commit))
        add_edges(identity, commit, 2)
    if len(edges) > MAX_EDGES:
        raise ContractError("reconstructed dependency graph exceeds the edge ceiling")
    edges.sort(key=lambda edge: (
        edge["depth"], edge["owner_repository"], edge["owner_commit"], edge["path"], edge["repository"], edge["commit"]
    ))
    material = {
        "schema": GRAPH_SCHEMA,
        "root": {"repository": root_identity, "commit": root_commit},
        "edge_count": len(edges),
        "edges": edges,
    }
    return {**material, "graph_digest": canonical_digest(material)}


class GitDirectoryReader:
    """Object reader over already-fetched bare or working repositories."""

    def __init__(self, repositories: dict[str, Path]) -> None:
        self.repositories = repositories

    def _git(self, identity: str, *arguments: str, check: bool = True) -> subprocess.CompletedProcess[bytes]:
        directory = self.repositories.get(identity)
        if directory is None:
            raise ContractError(f"{identity} has no fetched object store")
        return subprocess.run(
            ["git", "-C", str(directory), *arguments],
            check=check,
            capture_output=True,
        )

    def ls_tree(self, identity: str, commit: str) -> list[tuple[str, str, str]]:
        result = self._git(identity, "ls-tree", "-r", "-z", "--full-tree", expect_commit(commit, identity))
        rows = []
        for record in result.stdout.split(b"\0"):
            if not record:
                continue
            meta, _tab, path_bytes = record.partition(b"\t")
            mode, object_type, object_id = meta.decode("ascii").split(" ")
            del object_type
            rows.append((mode, object_id, path_bytes.decode("utf-8")))
        return rows

    def blob(self, identity: str, commit: str, path: str, limit: int) -> bytes:
        expect_path(path, path, max_depth=16, max_bytes=240)
        probe = self._git(identity, "cat-file", "-s", f"{expect_commit(commit, identity)}:{path}", check=False)
        if probe.returncode:
            raise FileNotFoundError(path)
        size = int(probe.stdout.strip())
        if size > limit:
            raise ContractError(f"{identity} blob {path} exceeds the {limit}-byte ceiling")
        return self._git(identity, "cat-file", "blob", f"{commit}:{path}").stdout

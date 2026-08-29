#!/usr/bin/env python3
"""Evaluate the bounded static closure of a governed Hexalith.Builds reusable workflow.

BUILD-REL-1 / GOV-1. The governed CI and Release paths must be able to prove, from the
exact reusable-workflow commit, which workflow and composite-action bytes actually
executed. This helper walks the literal ``uses:`` closure of one reusable workflow inside
an immutable Hexalith.Builds checkout, hashes every source it owns, and rejects any
reference that a later push could redefine.

The collector deliberately implements a closed YAML subset rather than importing a YAML
package: GitHub-hosted runners carry no guaranteed YAML dependency, and an ambiguous
parse must fail closed rather than silently drop a ``uses:`` edge.

Callers own their evidence logic. This helper emits provenance only; it never contacts a
network service, reads a secret, or decides whether a release may publish.
"""

import argparse
import hashlib
import json
import os
import re
import sys
from datetime import datetime, timezone
from pathlib import Path

PROVENANCE_SCHEMA = "hexalith.builds-governed-provenance.v1"
BUILDS_REPOSITORY = "Hexalith/Hexalith.Builds"
BUILDS_IDENTITY = "github.com/hexalith/hexalith.builds"
LOCAL_EXECUTION_PREFIX = "./.hexalith/builds-execution/"

# AD-13 ceilings, mirrored from the FrontComposer closure collector so a closure Builds
# reports can never exceed the bound the consumer is willing to validate.
MAX_CLOSURE_DEPTH = 16
MAX_CLOSURE_SOURCES = 256
MAX_SOURCE_BLOB_BYTES = 1_048_576
MAX_SOURCE_TOTAL_BYTES = 16_777_216

COMMIT_PATTERN = re.compile(r"^[0-9a-f]{40}$", re.ASCII)
SHA256_PATTERN = re.compile(r"^[0-9a-f]{64}$", re.ASCII)
IDENTITY_PATTERN = re.compile(r"^github\.com/[a-z0-9._-]+/[a-z0-9._-]+$", re.ASCII)
OWNER_REPOSITORY_PATTERN = re.compile(r"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$", re.ASCII)
WORKFLOW_REF_PATTERN = re.compile(
    r"^(?P<owner>[A-Za-z0-9._-]+)/(?P<repository>[A-Za-z0-9._-]+)"
    r"/(?P<path>\.github/workflows/[A-Za-z0-9._/-]+\.ya?ml)@(?P<ref>\S+)$",
    re.ASCII,
)
EXTERNAL_USES_PATTERN = re.compile(
    r"^(?P<owner>[A-Za-z0-9._-]+)/(?P<repository>[A-Za-z0-9._-]+)"
    r"(?P<path>/[^@\s]+)?@(?P<commit>[0-9a-f]{40})$",
    re.ASCII,
)
USES_LINE_PATTERN = re.compile(r"^\s*(?:-\s+)?uses\s*:(?P<value>.*)$", re.ASCII)
QUOTED_USES_LINE_PATTERN = re.compile(r"^\s*(?:-\s+)?['\"]uses['\"]\s*:", re.ASCII)
STAGES = ("ci", "release")

# Never-tracked build output. Everything else under a composite directory is hashed,
# including nested helper packages and JavaScript entrypoints, because deciding which
# nested file "really" executes would require interpreting arbitrary run commands.
UNTRACKED_HELPER_DIRECTORIES = frozenset({"__pycache__", ".git", "node_modules"})
UNTRACKED_HELPER_SUFFIXES = frozenset({".pyc", ".pyo"})


class ProvenanceError(Exception):
    """A deterministic, fail-closed governed-provenance failure."""


def canonical_bytes(value):
    """Serialize a value with the canonical form shared with the consuming contracts."""
    return json.dumps(
        value,
        ensure_ascii=True,
        allow_nan=False,
        sort_keys=True,
        separators=(",", ":"),
    ).encode("utf-8")


def canonical_digest(value):
    """Return the SHA-256 of the canonical serialization of a value."""
    return hashlib.sha256(canonical_bytes(value)).hexdigest()


def require_commit(value, context):
    """Require a strict lowercase 40-hex commit."""
    if not isinstance(value, str) or COMMIT_PATTERN.fullmatch(value) is None:
        raise ProvenanceError(f"{context} must be an exact lowercase 40-character commit SHA")
    return value


def require_sha256(value, context):
    """Require a strict lowercase 64-hex SHA-256."""
    if not isinstance(value, str) or SHA256_PATTERN.fullmatch(value) is None:
        raise ProvenanceError(f"{context} must be an exact lowercase 64-character SHA-256")
    return value


def require_identity(value, context):
    """Require a normalized lowercase ``github.com/owner/repository`` identity."""
    if not isinstance(value, str) or IDENTITY_PATTERN.fullmatch(value) is None:
        raise ProvenanceError(f"{context} must be a normalized github.com/owner/repository identity")
    return value


def normalize_identity(owner_repository, context):
    """Normalize an ``owner/repository`` pair to the canonical lowercase identity."""
    if not isinstance(owner_repository, str) or OWNER_REPOSITORY_PATTERN.fullmatch(owner_repository) is None:
        raise ProvenanceError(f"{context} must be an owner/repository pair")
    return require_identity(f"github.com/{owner_repository.lower()}", context)


def normalize_path(value, context):
    """Require a safe relative POSIX path."""
    if not isinstance(value, str) or not value or not value.isascii():
        raise ProvenanceError(f"{context} must be a non-empty ASCII relative path")
    if value.startswith("/") or "\\" in value:
        raise ProvenanceError(f"{context} must be a relative POSIX path: {value!r}")
    if any(ord(character) < 0x20 or ord(character) == 0x7F for character in value):
        raise ProvenanceError(f"{context} contains a control character: {value!r}")
    if any(segment in ("", ".", "..") for segment in value.split("/")):
        raise ProvenanceError(f"{context} contains an unsafe path segment: {value!r}")
    return value


def resolve_workspace_path(value, context):
    """Resolve a relative POSIX path and reject any escape from the current workspace."""
    relative = normalize_path(value, context)
    workspace = Path.cwd().resolve()
    destination = (workspace / relative).resolve()
    if not destination.is_relative_to(workspace):
        raise ProvenanceError(f"{context} escapes the workspace: {value!r}")
    return destination


def strip_yaml_comment(value):
    """Remove a trailing YAML comment from one scalar line, honoring quoting."""
    single = False
    double = False
    index = 0
    while index < len(value):
        character = value[index]
        if single:
            if character == "'":
                if index + 1 < len(value) and value[index + 1] == "'":
                    index += 2
                    continue
                single = False
        elif double:
            if character == "\\":
                index += 2
                continue
            if character == '"':
                double = False
        elif character == "'":
            single = True
        elif character == '"':
            double = True
        elif character == "#" and (index == 0 or value[index - 1].isspace()):
            return value[:index].rstrip()
        index += 1
    if single or double:
        raise ProvenanceError("unterminated quoted YAML scalar in a uses: entry")
    return value.rstrip()


def parse_uses_scalar(value, context):
    """Parse the scalar value of one ``uses:`` mapping entry."""
    scalar = strip_yaml_comment(value).strip()
    if not scalar:
        raise ProvenanceError(f"{context}: uses must carry a scalar value on the same line")
    if scalar.startswith("'"):
        if len(scalar) < 2 or not scalar.endswith("'"):
            raise ProvenanceError(f"{context}: malformed single-quoted uses scalar")
        return scalar[1:-1].replace("''", "'")
    if scalar.startswith('"'):
        try:
            parsed = json.loads(scalar)
        except json.JSONDecodeError as error:
            raise ProvenanceError(f"{context}: unsupported double-quoted uses scalar ({error.msg})") from error
        if not isinstance(parsed, str):
            raise ProvenanceError(f"{context}: uses value must be a string")
        return parsed
    if scalar[0] in "[{&*!|>" or any(character.isspace() for character in scalar):
        raise ProvenanceError(f"{context}: unsupported plain uses scalar {scalar!r}")
    return scalar


def scan_uses(text, context):
    """Return every literal ``uses:`` value in a workflow or action-metadata source."""
    literals = []
    for line_number, line in enumerate(text.splitlines(), 1):
        if QUOTED_USES_LINE_PATTERN.match(line):
            raise ProvenanceError(f"{context}:{line_number}: quoted uses keys are unsupported")
        match = USES_LINE_PATTERN.match(line)
        if match is None:
            continue
        literals.append((line_number, parse_uses_scalar(match.group("value"), f"{context}:{line_number}")))
    return literals


def action_using(text, context):
    """Return the ``runs.using`` value of a composite or JavaScript action."""
    values = [
        strip_yaml_comment(line.split(":", 1)[1]).strip().strip("'\"").lower()
        for line in text.splitlines()
        if re.match(r"^\s{2,}using\s*:", line, re.ASCII)
    ]
    if len(values) != 1:
        raise ProvenanceError(
            f"{context}: action metadata must declare exactly one runs.using value (found {len(values)})"
        )
    return values[0]


class ClosureCollector:
    """Collect the bounded static ``uses:`` closure of one reusable workflow."""

    def __init__(self, builds_root, commit):
        """Bind the collector to an immutable Hexalith.Builds checkout."""
        self.root = Path(builds_root).resolve()
        if not self.root.is_dir():
            raise ProvenanceError(f"Builds checkout {self.root} does not exist")
        self.commit = require_commit(commit, "builds-execution-sha")
        self.workflows = {}
        self.actions = {}
        self.external = {}
        self.helpers = {}
        self.total_bytes = 0
        self._active = []
        self._visited = set()

    def _resolve_inside_root(self, relative_path, context):
        candidate = (self.root / relative_path).resolve()
        if candidate != self.root and self.root not in candidate.parents:
            raise ProvenanceError(f"{context} escapes the approved Builds checkout: {relative_path!r}")
        return candidate

    def _read_source(self, relative_path, context):
        normalize_path(relative_path, context)
        path = self._resolve_inside_root(relative_path, context)
        if not path.is_file() or path.is_symlink():
            raise ProvenanceError(f"{context}: {relative_path} is not a regular file in the approved checkout")
        payload = path.read_bytes()
        if len(payload) > MAX_SOURCE_BLOB_BYTES:
            raise ProvenanceError(
                f"{context}: {relative_path} is {len(payload)} bytes, exceeding the "
                f"{MAX_SOURCE_BLOB_BYTES}-byte source ceiling"
            )
        self.total_bytes += len(payload)
        if self.total_bytes > MAX_SOURCE_TOTAL_BYTES:
            raise ProvenanceError(
                f"closure sources total {self.total_bytes} bytes, exceeding the "
                f"{MAX_SOURCE_TOTAL_BYTES}-byte closure ceiling"
            )
        # Helpers count toward the ceiling too. Counting only workflows and actions would
        # let a composite carry unlimited hashed helper bytes past a bound the consumer
        # agreed to validate.
        if len(self.workflows) + len(self.actions) + len(self.helpers) >= MAX_CLOSURE_SOURCES:
            raise ProvenanceError(f"closure exceeds the {MAX_CLOSURE_SOURCES}-source ceiling")
        return payload

    def _decode(self, payload, context):
        try:
            return payload.decode("utf-8-sig", "strict")
        except UnicodeDecodeError as error:
            raise ProvenanceError(f"{context}: source is not valid UTF-8 ({error})") from error

    def _record_helpers(self, directory, context):
        # Walk the whole composite subtree, not just its top level. A Python package, a
        # bundled JavaScript entrypoint, or a shell library one directory down executes
        # exactly like a top-level helper, and omitting it would leave executable bytes
        # outside the digest the consumer accepted.
        root = self._resolve_inside_root(directory, context)
        for entry in sorted(root.rglob("*"), key=lambda item: item.as_posix()):
            if entry.is_dir():
                continue
            relative_parts = entry.relative_to(root).parts
            if any(part in UNTRACKED_HELPER_DIRECTORIES for part in relative_parts[:-1]):
                continue
            if entry.suffix in UNTRACKED_HELPER_SUFFIXES:
                continue
            if len(relative_parts) == 1 and entry.name == "action.yml":
                continue
            relative = f"{directory}/{'/'.join(relative_parts)}"
            if relative in self.helpers:
                continue
            if entry.is_symlink() or not entry.is_file():
                raise ProvenanceError(
                    f"{context}: helper {relative} is not a regular file in the approved checkout"
                )
            payload = self._read_source(relative, f"{context} helper {'/'.join(relative_parts)}")
            self.helpers[relative] = {
                "repository": BUILDS_IDENTITY,
                "path": relative,
                "commit": self.commit,
                "blob_sha256": hashlib.sha256(payload).hexdigest(),
            }

    def _resolve_literal(self, literal, context):
        if "${{" in literal or "}}" in literal:
            raise ProvenanceError(f"{context}: a governed uses: reference must not be an expression: {literal!r}")
        if literal.lower().startswith("docker://"):
            raise ProvenanceError(f"{context}: Docker action references are not governable: {literal!r}")
        if literal.startswith("./"):
            if not literal.startswith(LOCAL_EXECUTION_PREFIX):
                raise ProvenanceError(
                    f"{context}: a governed local uses: reference must load from "
                    f"{LOCAL_EXECUTION_PREFIX}: {literal!r}"
                )
            directory = normalize_path(literal[len(LOCAL_EXECUTION_PREFIX):], f"{context} local uses path")
            return ("local", directory)
        match = EXTERNAL_USES_PATTERN.fullmatch(literal)
        if match is None:
            raise ProvenanceError(
                f"{context}: a governed uses: reference must be pinned to a literal lowercase "
                f"40-hex commit: {literal!r}"
            )
        raw_path = match.group("path")
        path = raw_path[1:] if raw_path else ""
        commit = match.group("commit")
        identity = normalize_identity(
            f"{match.group('owner')}/{match.group('repository')}",
            f"{context} external repository",
        )
        if path:
            normalize_path(path, f"{context} external path")
        # A Builds-owned action already pinned to the executing commit is the same bytes the
        # approved checkout holds, so record it as an exactly hashed local source instead of
        # an opaque coordinate.
        if identity == BUILDS_IDENTITY and commit == self.commit and path:
            return ("local", path)
        return ("external", (identity, path, commit))

    def _enter(self, key, depth):
        if depth > MAX_CLOSURE_DEPTH:
            raise ProvenanceError(f"closure depth {depth} exceeds the {MAX_CLOSURE_DEPTH}-level ceiling at {key}")
        if key in self._active:
            rendered = " -> ".join(self._active + [key])
            raise ProvenanceError(f"workflow/composite source cycle: {rendered}")
        if key in self._visited:
            return False
        self._active.append(key)
        return True

    def _leave(self, key):
        self._active.pop()
        self._visited.add(key)

    def _visit(self, literal, depth, context):
        kind, target = self._resolve_literal(literal, context)
        if kind == "external":
            identity, path, commit = target
            self.external[(identity, path, commit)] = {
                "repository": identity,
                "path": path,
                "commit": commit,
            }
            return
        self.visit_action(target, depth)

    def visit_workflow(self, workflow_path, depth=0):
        """Record one reusable workflow blob and recurse through its ``uses:`` closure."""
        normalize_path(workflow_path, "workflow path")
        if not workflow_path.startswith(".github/workflows/") or not workflow_path.endswith((".yml", ".yaml")):
            raise ProvenanceError(f"{workflow_path} is not a .github/workflows/*.yml reusable workflow")
        if not self._enter(workflow_path, depth):
            return
        try:
            payload = self._read_source(workflow_path, "reusable workflow")
            self.workflows[workflow_path] = {
                "repository": BUILDS_IDENTITY,
                "workflow_path": workflow_path,
                "commit": self.commit,
                "blob_sha256": hashlib.sha256(payload).hexdigest(),
            }
            text = self._decode(payload, workflow_path)
            for line_number, literal in scan_uses(text, workflow_path):
                self._visit(literal, depth + 1, f"{workflow_path}:{line_number}")
        finally:
            self._leave(workflow_path)

    def visit_action(self, directory, depth):
        """Record one local composite-action metadata blob and recurse through it."""
        metadata_path = f"{directory}/action.yml"
        if not self._enter(metadata_path, depth):
            return
        try:
            payload = self._read_source(metadata_path, "composite action")
            self.actions[metadata_path] = {
                "repository": BUILDS_IDENTITY,
                "path": metadata_path,
                "commit": self.commit,
                "blob_sha256": hashlib.sha256(payload).hexdigest(),
            }
            self._record_helpers(directory, metadata_path)
            text = self._decode(payload, metadata_path)
            using = action_using(text, metadata_path)
            literals = scan_uses(text, metadata_path)
            if using == "docker":
                raise ProvenanceError(f"{metadata_path}: Docker actions are not governable")
            if using == "composite":
                for line_number, literal in literals:
                    self._visit(literal, depth + 1, f"{metadata_path}:{line_number}")
            elif re.fullmatch(r"node[0-9]+", using, re.ASCII) is not None:
                if literals:
                    raise ProvenanceError(f"{metadata_path}: JavaScript action metadata declares uses: entries")
            else:
                raise ProvenanceError(f"{metadata_path}: unsupported runs.using value {using!r}")
        finally:
            self._leave(metadata_path)

    def projection(self, workflow_path):
        """Project the collected closure into its canonical, digest-bearing form."""
        reusable = self.workflows[workflow_path]
        actions = sorted(
            self.actions.values(),
            key=lambda source: (source["repository"], source["path"], source["commit"], source["blob_sha256"]),
        )
        helpers = sorted(
            self.helpers.values(),
            key=lambda source: (source["repository"], source["path"], source["commit"], source["blob_sha256"]),
        )
        external = sorted(
            self.external.values(),
            key=lambda source: (source["repository"], source["path"], source["commit"]),
        )
        material = {
            "reusable": reusable,
            "actions": actions,
            "helpers": helpers,
            "external_actions": external,
        }
        material["closure_digest"] = canonical_digest(material)
        return material


def validate_workflow_identity(arguments):
    """Prove the running job is defined by the exact approved Builds reusable workflow."""
    commit = require_commit(arguments.builds_execution_sha, "builds-execution-sha")
    if arguments.workflow_repository != BUILDS_REPOSITORY:
        raise ProvenanceError(
            f"job.workflow_repository is {arguments.workflow_repository!r}; the governed path "
            f"must be defined by {BUILDS_REPOSITORY}"
        )
    if arguments.workflow_sha != commit:
        raise ProvenanceError("job.workflow_sha does not match the approved builds-execution-sha")
    match = WORKFLOW_REF_PATTERN.fullmatch(arguments.workflow_ref)
    if match is None:
        raise ProvenanceError("job.workflow_ref is not an owner/repository/.github/workflows/<file>@<ref> value")
    resolved_repository = f"{match.group('owner')}/{match.group('repository')}"
    if resolved_repository != BUILDS_REPOSITORY:
        raise ProvenanceError("job.workflow_ref does not name the approved Hexalith.Builds repository")
    if match.group("path") != arguments.workflow_path:
        raise ProvenanceError(
            f"job.workflow_ref path {match.group('path')!r} does not match the declared governed "
            f"workflow {arguments.workflow_path!r}"
        )
    if arguments.workflow_file_path and arguments.workflow_file_path != arguments.workflow_path:
        raise ProvenanceError(
            f"job.workflow_file_path {arguments.workflow_file_path!r} does not match the declared "
            f"governed workflow {arguments.workflow_path!r}"
        )
    return commit


def dependency_policy_projection(arguments):
    """Validate and project the caller-declared active dependency-policy coordinate."""
    fields = (
        arguments.policy_repository,
        arguments.policy_path,
        arguments.policy_commit,
        arguments.policy_sha256,
    )
    if not any(fields):
        return None
    if not all(fields):
        raise ProvenanceError(
            "dependency-policy repository, path, commit, and sha256 must be declared together"
        )
    return {
        "repository": require_identity(arguments.policy_repository, "dependency-policy-repository"),
        "path": normalize_path(arguments.policy_path, "dependency-policy-path"),
        "commit": require_commit(arguments.policy_commit, "dependency-policy-commit"),
        "sha256": require_sha256(arguments.policy_sha256, "dependency-policy-sha256"),
    }


def build_provenance(arguments):
    """Build the complete governed-provenance document for one reusable-workflow run."""
    if arguments.stage not in STAGES:
        raise ProvenanceError(f"stage must be one of {list(STAGES)}, got {arguments.stage!r}")
    commit = validate_workflow_identity(arguments)
    candidate = require_commit(arguments.candidate, "candidate commit")
    expected_digest = None
    if arguments.expected_evaluator_digest:
        expected_digest = require_sha256(
            arguments.expected_evaluator_digest,
            "expected evaluator-authorization digest",
        )

    collector = ClosureCollector(arguments.builds_root, commit)
    collector.visit_workflow(arguments.workflow_path)
    closure = collector.projection(arguments.workflow_path)

    # Recording the expected digest without comparing it would make the field decorative:
    # the caller would believe it authorized a specific closure while any closure passed.
    if expected_digest is not None and closure["closure_digest"] != expected_digest:
        raise ProvenanceError(
            "the evaluated closure digest does not match the expected evaluator digest "
            f"(computed {closure['closure_digest']}, expected {expected_digest}); the "
            "authorized closure and the executing closure are not the same bytes"
        )

    provenance = {
        "schema": PROVENANCE_SCHEMA,
        "stage": arguments.stage,
        "generated_at_utc": datetime.now(timezone.utc).isoformat(),
        "candidate": candidate,
        "caller": {
            "repository": normalize_identity(arguments.caller_repository, "caller repository"),
            "workflow_ref": arguments.caller_workflow_ref,
            "run_id": arguments.run_id,
            "run_attempt": arguments.run_attempt,
            "event": arguments.event,
        },
        "closure": closure,
        "dependency_policy": dependency_policy_projection(arguments),
        "expected_evaluator_digest": expected_digest,
    }
    return provenance


def write_outputs(provenance, output_path):
    """Persist the provenance document and publish the caller-facing step outputs.

    The document has exactly one serialization: the canonical compact form produced by
    ``canonical_bytes``. The on-disk file, the ``provenance-json`` output, and the bytes
    ``provenance-sha256`` covers are all that same byte string, so a consumer can hash the
    artifact it received and compare it with the emitted digest without re-serializing.
    """
    destination = resolve_workspace_path(output_path, "output path")
    destination.parent.mkdir(parents=True, exist_ok=True)
    payload = canonical_bytes(provenance)
    destination.write_bytes(payload)

    closure = provenance["closure"]
    outputs = {
        "provenance-path": str(destination),
        "provenance-sha256": hashlib.sha256(payload).hexdigest(),
        "candidate": provenance["candidate"],
        "closure-digest": closure["closure_digest"],
        "reusable-repository": closure["reusable"]["repository"],
        "reusable-workflow-path": closure["reusable"]["workflow_path"],
        "reusable-commit": closure["reusable"]["commit"],
        "reusable-blob-sha256": closure["reusable"]["blob_sha256"],
        "actions-json": canonical_bytes(closure["actions"]).decode("utf-8"),
        "external-actions-json": canonical_bytes(closure["external_actions"]).decode("utf-8"),
        "provenance-json": payload.decode("utf-8"),
    }
    github_output = os.environ.get("GITHUB_OUTPUT")
    if github_output:
        with Path(github_output).open("a", encoding="utf-8") as stream:
            for name, value in outputs.items():
                stream.write(f"{name}={value}\n")
    return outputs


def build_parser():
    """Build the governed-provenance command-line parser."""
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--stage", required=True)
    parser.add_argument("--builds-root", required=True)
    parser.add_argument("--builds-execution-sha", required=True)
    parser.add_argument("--workflow-path", required=True)
    parser.add_argument("--workflow-repository", required=True)
    parser.add_argument("--workflow-sha", required=True)
    parser.add_argument("--workflow-ref", required=True)
    parser.add_argument("--workflow-file-path", default="")
    parser.add_argument("--caller-repository", required=True)
    parser.add_argument("--caller-workflow-ref", default="")
    parser.add_argument("--run-id", default="")
    parser.add_argument("--run-attempt", default="")
    parser.add_argument("--event", default="")
    parser.add_argument("--candidate", required=True)
    parser.add_argument("--policy-repository", default="")
    parser.add_argument("--policy-path", default="")
    parser.add_argument("--policy-commit", default="")
    parser.add_argument("--policy-sha256", default="")
    parser.add_argument("--expected-evaluator-digest", default="")
    parser.add_argument("--output", required=True)
    return parser


def main(argv=None):
    """Emit governed provenance, or fail closed with a support-safe diagnostic."""
    arguments = build_parser().parse_args(argv)
    try:
        provenance = build_provenance(arguments)
        outputs = write_outputs(provenance, arguments.output)
    except ProvenanceError as error:
        print(f"::error title=Governed provenance rejected::{error}", file=sys.stderr)
        return 1
    print(
        json.dumps(
            {
                "ok": True,
                "closure_digest": outputs["closure-digest"],
                "provenance_sha256": outputs["provenance-sha256"],
            },
            sort_keys=True,
        )
    )
    return 0


if __name__ == "__main__":
    sys.exit(main())

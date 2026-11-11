#!/usr/bin/env python3
"""Reduce release job results and write the sole non-publisher outcome, stdlib only."""

from __future__ import annotations

import json
import math
import os
from pathlib import Path
import re
import sys
import tempfile
from typing import Any

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import LANGUAGES


SCHEMA = "hexalith.release-outcome.v1"
SHA = re.compile(r"[0-9a-f]{40}")
VERSION = re.compile(r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)")
OUTCOMES = frozenset(("published", "ready", "nothing", "disabled", "failed"))


class OutcomeError(ValueError):
    """An outcome or prerequisite cannot safely authorize a release result."""


def validate_outcome(document: Any) -> dict[str, Any]:
    """Validate the closed v1 document without third-party runtime dependencies."""
    if not isinstance(document, dict):
        raise OutcomeError("release outcome must be an object")
    required = {"schema", "sha", "outcome"}
    allowed = required | {"version", "draftId", "lastTag"}
    if required - document.keys() or document.keys() - allowed:
        raise OutcomeError("release outcome has missing or unknown fields")
    if document["schema"] != SCHEMA:
        raise OutcomeError("unsupported release outcome schema")
    if not isinstance(document["sha"], str) or SHA.fullmatch(document["sha"]) is None:
        raise OutcomeError("release outcome sha must be a full lowercase commit SHA")
    if not isinstance(document["outcome"], str) or document["outcome"] not in OUTCOMES:
        raise OutcomeError("unsupported release outcome")
    if "version" in document and (not isinstance(document["version"], str)
                                  or VERSION.fullmatch(document["version"]) is None):
        raise OutcomeError("release outcome version must be unprefixed stable semver")
    if "draftId" in document:
        identity = document["draftId"]
        if (isinstance(identity, bool) or not isinstance(identity, (int, float)) or identity < 1
                or (isinstance(identity, float) and (not math.isfinite(identity) or not identity.is_integer()))):
            raise OutcomeError("release outcome draftId must be a positive integer")
    if "lastTag" in document:
        tag = document["lastTag"]
        if not isinstance(tag, str) or (tag != "" and (not tag.startswith("v")
                                                       or VERSION.fullmatch(tag[1:]) is None)):
            raise OutcomeError("release outcome lastTag must be a stable tag or empty string")
    if document["outcome"] == "ready" and {"version", "draftId", "lastTag"} - document.keys():
        raise OutcomeError("ready outcome requires version, draftId and lastTag")
    return document


def outcome_document(sha: str, outcome: str, version: str = "") -> dict[str, Any]:
    document = {"schema": SCHEMA, "sha": sha, "outcome": outcome}
    if version:
        document["version"] = version
    return validate_outcome(document)


def reduce_results(*, dispatch_sha: str, sha: str = "", version: str = "", enabled: str,
                   codeql: Any, nightly_hook: str, needs: Any, publish: str = "false",
                   noop_reason: str = "", mode: str = "create") -> dict[str, Any]:
    """Return a failure-first decision; only a validated draft can hand off writing.

    A successful no-version stage authorizes a no-op. A validated draft's
    complete artifact handoff transfers outcome ownership to the publisher.
    """
    selected = sha or dispatch_sha
    document = outcome_document(selected, "failed", version)

    def failed(message: str) -> dict[str, Any]:
        return {"document": document, "upload": "true", "publish": "false", "error": message}

    if not isinstance(needs, dict):
        return failed("release prerequisite results must be an object")
    # Failure and cancellation dominate switches, no-op decisions and handoffs.
    for name, job in needs.items():
        if not isinstance(job, dict) or job.get("result") not in {"success", "skipped", "failure", "cancelled"}:
            return failed(f"release prerequisite {name!r} has an unknown result")
        if job["result"] in {"failure", "cancelled"}:
            return failed(f"release prerequisite {name!r} {job['result']}")
    for name in ("resolve", "codeql", "nightly-hook"):
        if name not in needs:
            return failed(f"release prerequisite {name!r} is missing")
    if needs["resolve"]["result"] != "success":
        return failed("release resolve did not succeed")
    if not sha:
        return failed("release resolve did not select a SHA")
    if enabled not in {"true", "false"}:
        return failed("HEXALITH_RELEASE_PUBLISH_ENABLED must be exactly true or false")
    if (not isinstance(codeql, list) or any(not isinstance(item, str) or item not in LANGUAGES for item in codeql)
            or len(codeql) != len(set(codeql)) or nightly_hook not in {"true", "false"}):
        return failed("release nightly settings were not validated")
    if mode not in {"create", "complete"}:
        return failed("release mode was not resolved")
    if publish not in {"true", "false"}:
        return failed("release publish decision must be exactly true or false")
    if noop_reason not in {"", "no-version", "stale-draft-deleted"}:
        return failed("release no-op reason is unknown")
    intentional_skips = set()
    if not codeql:
        intentional_skips.add("codeql")
    if nightly_hook == "false":
        intentional_skips.add("nightly-hook")
    if enabled == "false":
        intentional_skips.update(("version", "build", "draft"))
    if mode == "complete":
        intentional_skips.update(("version", "build"))
    if noop_reason == "no-version":
        intentional_skips.update(("build", "draft"))
    for name, job in needs.items():
        if job["result"] == "skipped" and name not in intentional_skips:
            return failed(f"release prerequisite {name!r} was unexpectedly skipped")
    # A disabled job that ran is harmless; an enabled job that skipped is not.
    if publish == "true":
        required = ("draft",) if mode == "complete" else ("version", "build", "draft")
        draft_outputs = needs.get("draft", {}).get("outputs", {})
        if (enabled != "true" or noop_reason or not sha or not version
                or any(needs.get(name, {}).get("result") != "success" for name in required)
                or not isinstance(draft_outputs, dict) or draft_outputs.get("publish") != "true"
                or draft_outputs.get("sha") != sha or draft_outputs.get("version") != version):
            return failed("release publisher handoff lacks a validated successful draft")
        for name in ("draft-id", "artifact-id", "artifact-run"):
            value = draft_outputs.get(name)
            if not isinstance(value, str) or re.fullmatch(r"[1-9][0-9]*", value) is None:
                return failed("release publisher handoff has an invalid " + name)
        digest = draft_outputs.get("artifact-digest")
        last_tag = draft_outputs.get("last-tag")
        if (not isinstance(digest, str) or re.fullmatch(r"sha256:[0-9a-f]{64}", digest) is None
                or not isinstance(last_tag, str)
                or (last_tag != "" and (not last_tag.startswith("v")
                    or VERSION.fullmatch(last_tag[1:]) is None))):
            return failed("release publisher handoff has an invalid artifact digest or last tag")
        if mode == "create":
            build_outputs = needs["build"].get("outputs", {})
            version_outputs = needs["version"].get("outputs", {})
            if (not isinstance(build_outputs, dict) or not isinstance(version_outputs, dict)
                    or any(build_outputs.get(name) != draft_outputs[name]
                           for name in ("artifact-id", "artifact-digest"))
                    or version_outputs.get("version") != version
                    or version_outputs.get("last-tag") != last_tag
                    or version_outputs.get("no-version") != "false"):
                return failed("release publisher handoff differs from the version or build identity")
        return {"document": None, "upload": "false", "publish": "true", "error": ""}
    if enabled == "false":
        if noop_reason:
            return failed("disabled release cannot also claim a no-op decision")
        document["outcome"] = "disabled"
    elif noop_reason:
        owner = "version" if noop_reason == "no-version" else "draft"
        if needs.get(owner, {}).get("result") != "success":
            return failed(f"release no-op requires successful {owner}")
        if noop_reason == "stale-draft-deleted":
            draft_outputs = needs["draft"].get("outputs", {})
            if (mode != "complete" or not version or not isinstance(draft_outputs, dict)
                    or draft_outputs.get("noop-reason") != noop_reason
                    or draft_outputs.get("publish") != "false"
                    or draft_outputs.get("sha") != sha or draft_outputs.get("version") != version):
                return failed("release stale no-op lacks matching complete-mode draft evidence")
        elif mode != "create":
            return failed("release no-version no-op requires create mode")
        document["outcome"] = "nothing"
    else:
        return failed("release publication did not produce a handoff or validated no-op")
    return {"document": validate_outcome(document), "upload": "true", "publish": "false", "error": ""}


def write_document(directory: Path, document: dict[str, Any]) -> Path:
    """Atomically expose exactly outcome.json in the dedicated upload directory."""
    validate_outcome(document)
    directory.mkdir(parents=True, exist_ok=True)
    descriptor, name = tempfile.mkstemp(prefix=".outcome-", dir=directory)
    temporary = Path(name)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="\n") as stream:
            json.dump(document, stream, ensure_ascii=True, sort_keys=True)
            stream.write("\n")
        path = directory / "outcome.json"
        temporary.replace(path)
        return path
    finally:
        temporary.unlink(missing_ok=True)


def outputs(values: dict[str, str]) -> None:
    if any("\r" in value or "\n" in value for value in values.values()):
        raise OutcomeError("release output contains a line break")
    if os.environ.get("GITHUB_OUTPUT"):
        with Path(os.environ["GITHUB_OUTPUT"]).open("a", encoding="utf-8") as stream:
            for name, value in values.items():
                stream.write(f"{name}={value}\n")


def main() -> int:
    environment = os.environ
    try:
        if sys.version_info < (3, 12):
            raise OutcomeError("Python 3.12 or later is required")
        dispatch_sha = environment.get("RELEASE_DISPATCH_SHA", "")
        selected_sha = environment.get("RELEASE_SHA", "")
        version = environment.get("RELEASE_VERSION", "")
        try:
            decision = reduce_results(
                dispatch_sha=dispatch_sha, sha=selected_sha, version=version,
                enabled=environment.get("RELEASE_ENABLED", ""),
                # An absent job output is an empty string; failure-first
                # reduction must still name the failed prerequisite.
                codeql=json.loads(environment.get("RELEASE_CODEQL") or "null"),
                nightly_hook=environment.get("RELEASE_NIGHTLY_HOOK", ""),
                needs=json.loads(environment.get("RELEASE_NEEDS") or "null"),
                publish=environment.get("RELEASE_PUBLISH", "false"),
                noop_reason=environment.get("RELEASE_NOOP_REASON", ""),
                mode=environment.get("RELEASE_MODE", "create"))
        except (OutcomeError, ValueError, TypeError, RecursionError) as error:
            # Malformed metadata remains failed, using the last valid identity.
            known_sha = selected_sha if SHA.fullmatch(selected_sha) else dispatch_sha
            known_version = version if VERSION.fullmatch(version) else ""
            decision = {"document": outcome_document(known_sha, "failed", known_version),
                        "upload": "true", "publish": "false", "error": str(error)}
        path = ""
        if decision["upload"] == "true":
            directory = Path(environment.get("RELEASE_OUTPUT_DIRECTORY", "")
                             or str(Path(environment.get("RUNNER_TEMP", tempfile.gettempdir()))
                                    / "hexalith-release-outcome"))
            path = str(write_document(directory, decision["document"]).resolve())
        outputs({"upload": decision["upload"], "publish": decision["publish"], "path": path,
                 "outcome": decision["document"]["outcome"] if decision["document"] else ""})
        if decision["error"]:
            # JSON encoding prevents external names from becoming workflow commands.
            print("release outcome failed: " + json.dumps(decision["error"], ensure_ascii=True), file=sys.stderr)
            return 1
        print("release outcome: " + (decision["document"]["outcome"] if decision["document"] else "publisher handoff"))
        return 0
    except (OutcomeError, OSError) as error:
        print("release outcome could not be written: " + json.dumps(str(error), ensure_ascii=True), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

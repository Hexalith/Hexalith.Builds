"""Freeze event endpoints and lint complete messages from Git objects."""

from __future__ import annotations

import json
from pathlib import Path
import re

from expected_state import ExpectedStateError


SHA = re.compile(r"[0-9a-fA-F]{40}\Z")
ZERO = "0" * 40


def endpoint(git, value, label, *, zero=False):
    if not isinstance(value, str) or not SHA.fullmatch(value) or (value == ZERO and not zero):
        raise ExpectedStateError(label + ": expected a full nonzero commit SHA")
    value = value.lower()
    if value == ZERO:
        return value
    resolved = git.run("rev-parse", "--verify", "--end-of-options", value + "^{commit}").decode().strip()
    if resolved != value:
        raise ExpectedStateError(label + ": unavailable commit endpoint")
    return value


def select(git, *, event_name: str, event: dict, frozen_head: str, commit_range=None):
    """Return commit IDs and optional title; never substitute a checkout merge."""
    title = None
    if not isinstance(event, dict):
        raise ExpectedStateError("guard event payload must be an object")
    if commit_range is not None:
        if not isinstance(commit_range, (list, tuple)) or len(commit_range) != 2:
            raise ExpectedStateError("explicit commit range requires two endpoints")
        before = endpoint(git, commit_range[0], "range base", zero=True)
        after = endpoint(git, commit_range[1], "range head")
    elif event_name == "pull_request":
        pull = event.get("pull_request")
        if not isinstance(pull, dict) or not isinstance(pull.get("base"), dict) or not isinstance(pull.get("head"), dict):
            raise ExpectedStateError("pull_request: missing base/head context")
        before = endpoint(git, pull["base"].get("sha"), "PR base")
        after = endpoint(git, pull["head"].get("sha"), "PR head")
        title = pull.get("title")
        if not isinstance(title, str) or not title.strip():
            raise ExpectedStateError("pull_request: missing title")
    elif event_name == "push":
        before = endpoint(git, event.get("before"), "push before", zero=True)
        after = endpoint(git, event.get("after"), "push after")
    elif event_name == "workflow_dispatch":
        return [endpoint(git, frozen_head, "dispatch HEAD")], None
    else:
        raise ExpectedStateError("unsupported or missing guard event: " + str(event_name))
    revision = after if before == ZERO else before + ".." + after
    commits = git.run("rev-list", "--reverse", "--topo-order", revision, "--").decode("ascii").splitlines()
    if any(not SHA.fullmatch(commit) for commit in commits):
        raise ExpectedStateError("commit range returned invalid commit identity")
    return commits, title


def message(git, sha):
    raw = git.run("cat-file", "commit", sha)
    headers, separator, body = raw.partition(b"\n\n")
    if not separator:
        raise ExpectedStateError(sha + ": malformed commit object")
    encoding = "utf-8"
    for line in headers.splitlines():
        if line.startswith(b"encoding "):
            encoding = line[9:].decode("ascii")
    try:
        return body.decode(encoding)
    except (UnicodeError, LookupError) as error:
        raise ExpectedStateError(sha + ": undecodable commit message") from error


def lint(git, tools, *, event_name, event, frozen_head, commit_range=None):
    commits, title = select(git, event_name=event_name, event=event,
                           frozen_head=frozen_head, commit_range=commit_range)
    for sha in commits:
        tools.lint_message(message(git, sha), "commit " + sha)
    if title is not None:
        tools.lint_message(title, "pull request title")


def load_event(path):
    try:
        event = json.loads(Path(path).read_bytes())
    except (OSError, ValueError, UnicodeError) as error:
        raise ExpectedStateError("guard event payload is unavailable or invalid") from error
    if not isinstance(event, dict):
        raise ExpectedStateError("guard event payload must be an object")
    return event

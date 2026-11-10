#!/usr/bin/env python3
"""Compute create-mode versions from frozen Git objects and two pinned plugins."""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import secrets
import shutil
import subprocess
import sys
import tempfile

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import BUILDS_REPOSITORY
from release_state import Git, ReleaseError, SHA, STABLE, highest_reachable_stable_tag

TOOLS = Path(__file__).resolve().parents[1] / "release-version"
PARSER = {
    "headerPattern": r"^(\w+)(?:\((.*)\))?!?: (.*)$",
    "breakingHeaderPattern": r"^(\w+)(?:\((.*)\))?!: (.*)$",
    "headerCorrespondence": ["type", "scope", "subject"],
    "noteKeywords": ["BREAKING CHANGE", "BREAKING CHANGES"],
}


def release_rules(repository, selection, module_root=""):
    """Suppress plugin fallback rules and explicitly select the allowed types."""
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ReleaseError("invalid repository identity")
    if module_root and (repository != BUILDS_REPOSITORY or module_root != "Samples/Hexalith.Sample"):
        raise ReleaseError("module-root is a test-only Builds sample input")
    if selection not in ("module", "builds"):
        raise ReleaseError("release-rules must be module or builds")
    if selection == "builds" and (repository != BUILDS_REPOSITORY or module_root):
        raise ReleaseError("builds release rules require the actual Builds repository")
    rules = [{"release": False}]
    if selection == "builds":
        rules.extend([{"type": "!(docs|test|style)", "release": "patch"},
                      {"type": "!(docs|test|style)", "breaking": True, "release": "major"},
                      {"type": "feat", "release": "minor"}])
    else:
        for kind, increment in (("feat", "minor"), ("fix", "patch"), ("perf", "patch")):
            rules.extend([{"type": kind, "release": increment},
                          {"type": kind, "breaking": True, "release": "major"}])
    return rules


def commits_since(git, sha, last_tag):
    """Read complete messages without a checkout, including peeled annotated bases."""
    if not SHA.fullmatch(sha):
        raise ReleaseError("expected full source commit SHA")
    actual = git.run("rev-parse", "--verify", sha + "^{commit}").stdout.decode("ascii").strip()
    if actual != sha:
        raise ReleaseError("release SHA is not a commit")
    revision = sha
    if last_tag:
        if not STABLE.fullmatch(last_tag):
            raise ReleaseError("invalid release base tag")
        base = git.run("rev-parse", "--verify", last_tag + "^{commit}").stdout.decode("ascii").strip()
        revision = base + ".." + sha
    data = git.run("log", "-z", "--format=%H%x00%B", revision, "--").stdout.decode("utf-8")
    fields = data.split("\0")
    if fields[-1] != "" or (len(fields) - 1) % 2:
        raise ReleaseError("malformed frozen commit history")
    commits = []
    for index in range(0, len(fields) - 1, 2):
        identity, message = fields[index:index + 2]
        if not SHA.fullmatch(identity):
            raise ReleaseError("invalid commit in frozen history")
        commits.append({"hash": identity, "message": message})
    return commits


def next_version(last_tag, increment):
    if increment not in ("major", "minor", "patch"):
        raise ReleaseError("plugin returned an unsupported release increment")
    if not last_tag:
        return "1.0.0"
    match = STABLE.fullmatch(last_tag)
    if match is None:
        raise ReleaseError("invalid release base tag")
    major, minor, patch = map(int, match.groups())
    return {"major": f"{major + 1}.0.0", "minor": f"{major}.{minor + 1}.0",
            "patch": f"{major}.{minor}.{patch + 1}"}[increment]


def tool_environment(directory):
    """Exclude token, package-manager and JS configuration from the tool process."""
    directory = Path(directory)
    user_config = directory / "npm-user-config"
    user_config.write_text("", encoding="utf-8")
    environment = {key: value for key, value in os.environ.items()
                   if not key.upper().startswith(("GIT_", "GH_", "GITHUB_", "NPM_", "NODE_"))
                   and key.upper() not in ("NODE_OPTIONS", "NODE_PATH", "COREPACK_HOME")}
    environment.update(NPM_CONFIG_USERCONFIG=str(user_config), NPM_CONFIG_GLOBALCONFIG=os.devnull,
                       NPM_CONFIG_CACHE=str(directory / "npm-cache"), NPM_CONFIG_AUDIT="false",
                       NPM_CONFIG_FUND="false", NPM_CONFIG_REGISTRY="https://registry.npmjs.org/")
    return environment


class Plugins:
    """Install only the trusted locked package in a fresh disposable directory."""

    def __init__(self, directory):
        self.directory = Path(directory)
        self.environment = tool_environment(self.directory)
        for filename in ("package.json", "package-lock.json", "invoke_plugins.mjs"):
            shutil.copyfile(TOOLS / filename, self.directory / filename)
        # These plugins declare a semantic-release peer, but never call core.
        # Ignoring that peer avoids installing a lifecycle runner or npm plugin.
        self.run(["npm", "ci", "--ignore-scripts", "--legacy-peer-deps"])
        self.run(["npm", "audit", "signatures"])

    def run(self, arguments, payload=None):
        result = subprocess.run(arguments, cwd=self.directory, env=self.environment,
                                input=payload, text=True, capture_output=True, check=False)
        if result.returncode:
            # Do not repeat commit bodies or third-party/config-controlled output.
            raise ReleaseError(f"trusted release tool failed: {arguments[0]} {arguments[1]}")
        return result.stdout

    def __call__(self, operation, config, context):
        output = self.run(["node", str(self.directory / "invoke_plugins.mjs")],
                          json.dumps({"operation": operation, "config": config, "context": context}))
        try:
            return json.loads(output)
        except ValueError as error:
            raise ReleaseError("release plugin returned invalid JSON") from error


def compute_version(git, plugins, *, repository, sha, selection="module", module_root=""):
    config = {"parserOpts": PARSER, "releaseRules": release_rules(repository, selection, module_root)}
    last_tag = highest_reachable_stable_tag(git, sha)
    commits = commits_since(git, sha, last_tag)
    context = {"commits": commits, "options": {"repositoryUrl": "https://github.com/" + repository},
               "lastRelease": {"gitTag": last_tag}}
    increment = plugins("analyze", config, context)
    if increment is None:
        print("nothing to release", flush=True)
        return {"version": "", "notes": "", "last-tag": last_tag, "no-version": "true"}
    version = next_version(last_tag, increment)
    context["nextRelease"] = {"version": version, "gitTag": "v" + version, "gitHead": sha}
    notes = plugins("notes", {"parserOpts": PARSER}, context)
    if not isinstance(notes, str):
        raise ReleaseError("release notes plugin returned non-text notes")
    return {"version": version, "notes": notes, "last-tag": last_tag, "no-version": "false"}


def write_outputs(values):
    output = os.environ.get("GITHUB_OUTPUT")
    if not output:
        raise ReleaseError("GITHUB_OUTPUT is required")
    with Path(output).open("a", encoding="utf-8") as stream:
        for key, value in values.items():
            # An unpredictable delimiter prevents commit-controlled output injection.
            delimiter = "hexalith_" + secrets.token_hex(32)
            while delimiter in value:
                delimiter = "hexalith_" + secrets.token_hex(32)
            stream.write(f"{key}<<{delimiter}\n{value}\n{delimiter}\n")


def main():
    argparse.ArgumentParser(description=__doc__).parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ReleaseError("Python 3.12 or later is required")
        repository = os.environ.get("GITHUB_REPOSITORY", "")
        selection = os.environ.get("RELEASE_RULES", "module")
        module_root = os.environ.get("RELEASE_MODULE_ROOT", "")
        # Guard before any network or npm work.
        release_rules(repository, selection, module_root)
        sha = os.environ.get("RELEASE_SHA", "")
        if not SHA.fullmatch(sha):
            raise ReleaseError("expected full source commit SHA")
        with tempfile.TemporaryDirectory(prefix="hexalith-release-version-") as directory:
            git_directory, plugin_directory = Path(directory) / "git", Path(directory) / "tools"
            git_directory.mkdir()
            plugin_directory.mkdir()
            git = Git.fetch(git_directory, repository)
            result = compute_version(git, Plugins(plugin_directory), repository=repository, sha=sha,
                                     selection=selection, module_root=module_root)
            write_outputs(result)
        return 0
    except (ReleaseError, OSError, UnicodeError) as error:
        print(error, file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

"""Disposable, integrity-locked tools; no module configuration is loaded."""

from __future__ import annotations

import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys

from expected_state import ExpectedStateError


SOURCE = Path(__file__).resolve().parents[1] / "guard"
TIMEOUT = 180


def tool_environment(directory: Path) -> dict[str, str]:
    """Whitelist platform settings; exclude tokens and npm/Node/Git injection."""
    environment = {key: value for key, value in os.environ.items()
                   if key in {"PATH", "SystemRoot", "WINDIR", "TMPDIR", "TMP", "TEMP",
                              "LANG", "LC_ALL", "LC_CTYPE", "SSL_CERT_FILE", "SSL_CERT_DIR"}}
    home = directory / "home"
    home.mkdir()
    config = directory / "npm-user-config"
    config.write_text("", encoding="utf-8")
    environment.update(HOME=str(home), USERPROFILE=str(home), XDG_CONFIG_HOME=str(home),
        NPM_CONFIG_USERCONFIG=str(config), NPM_CONFIG_GLOBALCONFIG=os.devnull,
        NPM_CONFIG_CACHE=str(directory / "npm-cache"), NPM_CONFIG_AUDIT="false",
        NPM_CONFIG_FUND="false", NPM_CONFIG_REGISTRY="https://registry.npmjs.org/",
        NPM_CONFIG_IGNORE_SCRIPTS="true", GIT_CONFIG_NOSYSTEM="1", GIT_CONFIG_GLOBAL=os.devnull,
        GIT_TERMINAL_PROMPT="0", HEXALITH_GUARD_TOOLS=str(directory),
        HEXALITH_GUARD_PYTHON=sys.executable, NO_COLOR="1")
    return environment


class Tools:
    """Install once after ownership comparison and share parser/commitlint."""

    def __init__(self, directory: str | Path):
        self.directory = Path(directory)
        try:
            self.environment = tool_environment(self.directory)
            for filename in ("package.json", "package-lock.json"):
                shutil.copyfile(SOURCE / filename, self.directory / filename)
        except (OSError, UnicodeError) as error:
            raise ExpectedStateError("trusted guard tool preparation failed") from error
        self._run(["npm", "ci", "--ignore-scripts"], "npm locked installation")
        self._run(["npm", "audit", "signatures"], "npm signature verification")

    def _process(self, arguments, operation, payload=None):
        try:
            return subprocess.run(arguments, cwd=self.directory, env=self.environment,
                input=payload, text=True, encoding="utf-8", errors="strict",
                capture_output=True, check=False, timeout=TIMEOUT)
        except (OSError, UnicodeError, subprocess.TimeoutExpired) as error:
            raise ExpectedStateError("trusted guard tool failed: " + operation) from error

    def _run(self, arguments, operation, payload=None):
        result = self._process(arguments, operation, payload)
        if result.returncode:
            raise ExpectedStateError("trusted guard tool failed: " + operation)
        return result.stdout

    def parse_yaml(self, text: str):
        output = self._run(["node", str(self.directory / "node_modules/js-yaml/bin/js-yaml.js")],
                           "YAML parser", "# guard YAML input\n" + text)
        try:
            document = json.loads(output)
        except ValueError as error:
            raise ExpectedStateError("trusted guard YAML parser returned invalid or multiple documents") from error
        if not isinstance(document, dict):
            raise ExpectedStateError("custom action YAML requires one mapping document")
        return document

    def lint_message(self, message: str, identity: str) -> None:
        result = self._process(["node", str(self.directory / "node_modules/@commitlint/cli/cli.js"),
            "--config", str(SOURCE / "commitlint.config.mjs"), "--color=false"], "commitlint", message)
        if result.returncode:
            rules = sorted(set(re.findall(r"\[([a-z][a-z0-9-]+)\]", result.stdout + result.stderr)))
            raise ExpectedStateError(identity + ": commitlint " + (", ".join(rules) if rules else "tool failed"))

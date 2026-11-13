"""Lint committed workspace-root custom action metadata as data only.

YAML parsing belongs to the pinned external parser; policy decisions stay here.
The deny scan intentionally recognizes literal text, without following scripts
or attempting to interpret a shell program.
"""

from __future__ import annotations

import re
import shlex

from expected_state import ExpectedStateError


REGULAR = {"100644", "100755"}
PREFIX = ".github/actions/"
REMOTE = re.compile(r"([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)((?:/[^/@\s]+)*)@([0-9a-fA-F]{40})\Z")


def _quoted(pattern: str) -> str:
    return f"(?:{pattern}|\"{pattern}\"|'{pattern}')"


def _executable(basename: str) -> str:
    name = "(?:" + basename + r")(?:\.exe)?"
    return (r'''(?:(?:[^\s"';&|<>]*[/\\])?''' + name
            + r'''|"(?:[^"\r\n]*[/\\])?''' + name + '"'
            + r'''|'(?:[^'\r\n]*[/\\])?''' + name + "')")


def _primitive(executable: str, *verbs: str) -> str:
    # Recognize literal global options, including quoted operands. This remains
    # a conservative text match, without interpreting shell variables or scripts.
    token = r'''(?:(?>"[^"\r\n]*"|'[^'\r\n]*'|[^\s;&|<>"']+))+'''
    option = (r'''(?:--?[^=\s;&|<>"']+(?:=''' + token + r''')?'''
              + r'''|"--?[^"\r\n]*"|'--?[^'\r\n]*')''')
    def before(verb):
        operand = r"(?!(?:--?|" + _quoted(verb) + r"(?=\s|$|[;&|<>])))" + token
        options = r"(?:[ \t]+(?>" + option + r"(?:[ \t]+" + operand + r")?))*"
        return options + r"[ \t]+" + _quoted(verb)
    return (r"(?<![\w.-])" + _executable(executable)
            + "".join(before(verb) for verb in verbs) + r"(?=\s|$|[;&|<>])")


# Match only the invocation prefix: consuming its arguments would hide a second
# literal dotnet command on the same line. Paths are text, never resolved/run.
COMMAND = re.compile(_primitive("dotnet", "(?:build|test)"), re.IGNORECASE)
REDIRECTION = re.compile(r"(?:&?>{1,2}|<{1,2}|[<>]&)$")
# These options consume the following token, so a logger/settings/output file
# ending in .csproj cannot masquerade as an explicit project argument.
VALUE_OPTIONS = {
    "--configuration", "-c", "--framework", "-f", "--runtime", "-r", "--arch", "-a",
    "--os", "--output", "-o", "--verbosity", "-v", "--logger", "-l", "--settings", "-s",
    "--filter", "--results-directory", "--diag", "-d", "--test-adapter-path", "--environment",
    "-e", "--collect", "--artifacts-path", "--tl", "--property", "-p", "--blame-hang-timeout",
    "--stop-on-fail",
    "--blame-hang-dump-type", "--blame-crash-dump-type",
}
DENY = (
    ("actions/checkout", r"(?<![\w.-])actions/checkout\b"),
    ("actions/setup-dotnet", r"(?<![\w.-])actions/setup-dotnet\b"),
    ("actions/cache", r"(?<![\w.-])actions/cache\b"),
    ("dapr init", _primitive("dapr", "init")),
    ("nuget push", _primitive("nuget", "push")),
    ("registry login", _primitive("docker|podman|helm|oras|crane|skopeo", "login")
                       + "|" + _primitive("helm", "registry", "login")
                       + "|" + _primitive("crane", "auth", "login")
                       + "|" + _primitive("az", "acr", "login")
                       + r"|(?<![\w.-])(?:docker/login-action|azure/docker-login|aws-actions/amazon-ecr-login)\b"),
    ("semantic-release", r"(?<![\w.-])semantic-release\b"),
)


def _parts(path: str) -> bool:
    return bool(path) and all(part not in {"", ".", ".."} for part in path.split("/"))


def _reference(value: object, snapshot) -> str | None:
    if (not isinstance(value, str) or not value or "${{" in value or "\\" in value
            or any(ord(char) < 32 or ord(char) == 127 for char in value)):
        return "step uses must be a literal local action or a full-SHA third-party action"
    # Identify Builds before checking its pin so a tag or malformed pin receives
    # the same explicit prohibition as an otherwise valid full-SHA reference.
    if re.match(r"hexalith/hexalith\.builds(?:/|@|$)", value, re.IGNORECASE):
        return "custom actions cannot use Hexalith/Hexalith.Builds"
    if value.startswith("./"):
        path = value[2:]
        if (not path.startswith(PREFIX) or not _parts(path[len(PREFIX):])
                or any(char.isspace() or ord(char) < 32 or ord(char) == 127 for char in path)):
            return "local uses must be normalized ./.github/actions/<directory>"
        if not any((item := snapshot.items.get(path + "/" + filename)) is not None
                   and item.mode in REGULAR for filename in ("action.yml", "action.yaml")):
            return "local uses target has no regular committed action metadata: " + value
        return None
    match = REMOTE.fullmatch(value)
    if match is None or not _parts(match[1] + "/" + match[2] + match[3]):
        return "third-party uses must be owner/repo[/path]@<40-hex-SHA>"
    return None


def _whole_map_expression(value: object) -> bool:
    """Recognize one expression token; never evaluate its result or contents."""
    if not isinstance(value, str) or not (text := value.strip()).startswith("${{"):
        return False
    quoted = False
    index = 3
    while index < len(text):
        if text[index] == "'":
            if quoted and text[index:index + 2] == "''":
                index += 2
                continue
            quoted = not quoted
        elif not quoted and text[index:index + 2] == "}}":
            return index == len(text) - 2 and bool(text[3:index].strip())
        index += 1
    return False


def _structure(document: object, snapshot) -> list[str]:
    if not isinstance(document, dict):
        return ["action must be a single mapping document"]
    runs = document.get("runs")
    if not isinstance(runs, dict) or runs.get("using") != "composite":
        return ["runs.using must be composite"]
    steps = runs.get("steps")
    if not isinstance(steps, list) or not steps:
        return ["composite runs.steps must be a nonempty sequence"]
    findings = []
    for position, step in enumerate(steps, 1):
        label = f"step {position}"
        if not isinstance(step, dict):
            findings.append(label + " must be a mapping")
            continue
        for field in ("env", "with"):
            if field in step and not isinstance(step[field], dict) and not _whole_map_expression(step[field]):
                findings.append(label + " " + field + " must be a mapping")
            elif isinstance(step.get(field), dict) and any(isinstance(value, (dict, list)) for value in step[field].values()):
                findings.append(label + " " + field + " entries must be scalar values")
        if ("uses" in step) == ("run" in step):
            findings.append(label + " must declare exactly one of uses or run")
        elif "uses" in step:
            finding = _reference(step["uses"], snapshot)
            if finding:
                findings.append(label + ": " + finding)
        elif not isinstance(step["run"], str) or not step["run"].strip():
            findings.append(label + " run must be nonempty text")
        elif not isinstance(step.get("shell"), str) or not step["shell"].strip():
            findings.append(label + " run requires a shell")
    return findings


def _explicit_project(arguments: str) -> bool:
    try:
        lexer = shlex.shlex(arguments, posix=True, punctuation_chars=";&|<>")
        lexer.whitespace_split = True
        lexer.commenters = "#"
        tokens = list(lexer)
    except ValueError:
        return False
    positional = []
    project = None
    index = 0
    while index < len(tokens):
        token = tokens[index]
        lower = token.lower()
        if token == "--" or token and all(char in ";&|" for char in token):
            break
        if (token.isdigit() or token == "*") and index + 1 < len(tokens) and REDIRECTION.fullmatch(tokens[index + 1]):
            index += 1
            token = tokens[index]
        if REDIRECTION.fullmatch(token):
            index += 2  # Redirection and its file/descriptor operand are not project arguments.
            if index > len(tokens):
                return False
            continue
        if lower == "--project":
            index += 1
            if index >= len(tokens) or project is not None:
                return False
            project = tokens[index]
        elif lower.startswith("--project="):
            if project is not None:
                return False
            project = token.split("=", 1)[1]
        elif lower in VALUE_OPTIONS:
            index += 1
            if index >= len(tokens):
                return False
        elif token.startswith("-") or re.match(r"/[A-Za-z][A-Za-z-]*(?::|$)", token):
            pass
        else:
            positional.append(token)
        index += 1
    selected = [project] if project is not None else positional
    return (len(selected) == 1 and bool(selected[0]) and selected[0].lower().endswith(".csproj")
            and (project is None or not positional))


def _strings(value):
    if isinstance(value, str):
        yield value
    elif isinstance(value, dict):
        for key, child in value.items():
            yield from _strings(key)
            yield from _strings(child)
    elif isinstance(value, list):
        for child in value:
            yield from _strings(child)


def _deny(text: str, document: object) -> list[str]:
    # Recognize ordinary shell and PowerShell line continuations only. There is
    # deliberately no expansion of variables, functions, aliases or scripts.
    scalars = [re.sub(r"(?:\\|`)[ \t]*\r?\n", " ", scalar) for scalar in _strings(document)]
    metadata = text + "\n" + "\n".join(scalars)
    findings = ["deny-list: " + name for name, pattern in DENY if re.search(pattern, metadata, re.IGNORECASE)]
    # Inspect resolved scalar text for commands so YAML's own surrounding quote
    # characters and flow delimiters never become shell arguments. References
    # and deny-listed primitive names above still scan the full metadata text.
    commands = scalars + [match[1] for match in re.finditer(r"(?m)#([^\r\n]*)", text)]
    if any(not _explicit_project(re.split(r"[\r\n]", scalar[match.end():], maxsplit=1)[0])
           for scalar in commands for match in COMMAND.finditer(scalar)):
        findings.append("deny-list: solution-wide dotnet build/test requires an explicit .csproj")
    return findings


def lint(snapshot, tools) -> None:
    """Inspect every metadata blob in the pinned full workspace snapshot."""
    findings = []
    for path, item in sorted(snapshot.items.items()):
        if not path.startswith(PREFIX) or path.rsplit("/", 1)[-1] not in {"action.yml", "action.yaml"}:
            continue
        if item.mode not in REGULAR:
            findings.append(path + ": action-metadata must be a regular committed blob")
            continue
        try:
            text = snapshot.read(path).decode("utf-8-sig")
        except UnicodeError:
            findings.append(path + ": action-yaml must be valid UTF-8")
            continue
        try:
            document = tools.parse_yaml(text)
        except ExpectedStateError as error:
            findings.append(path + ": action-yaml parser failed")
            findings.extend(path + ": " + diagnostic for diagnostic in error.diagnostics)
            continue
        findings.extend(path + ": action-structure: " + finding for finding in _structure(document, snapshot))
        findings.extend(path + ": " + finding for finding in _deny(text, document))
    if findings:
        raise ExpectedStateError(*findings)

"""Evaluate the GitHub Actions expression subset used by release metadata.

Release tests use this to execute the real workflow and composite mappings
against controlled contexts. Unsupported syntax fails loudly so a mapping can
never silently evaluate to an empty value; no test asserts YAML source text.
"""
from __future__ import annotations

import json
import math
from pathlib import Path
import re
import subprocess

import yaml

TOKEN = re.compile(r"\s*(?:(?P<string>'(?:[^']|'')*')|(?P<number>\d+(?:\.\d+)?)"
                   r"|(?P<operator>==|!=|&&|\|\||[!(),.])|(?P<name>[A-Za-z_][A-Za-z0-9_-]*))")
TEMPLATE = re.compile(r"\$\{\{(.*?)\}\}", re.DOTALL)
STATUS = re.compile(r"\b(?:always|cancelled|success|failure)\s*\(")


def truthy(value):
    if isinstance(value, bool):
        return value
    if isinstance(value, (int, float)):
        return value != 0 and not math.isnan(value)
    return value not in (None, "")


def render(value):
    if value is None:
        return ""
    if isinstance(value, bool):
        return str(value).lower()
    if isinstance(value, float) and value.is_integer():
        return str(int(value))
    if isinstance(value, (dict, list)):
        return json.dumps(value)
    return str(value)


def number(value):
    if value is None:
        return 0
    if isinstance(value, bool):
        return int(value)
    if isinstance(value, (int, float)):
        return value
    if isinstance(value, str):
        try:
            return float(value.strip()) if value.strip() else 0
        except ValueError:
            return math.nan
    return math.nan


def equal(left, right):
    if isinstance(left, str) and isinstance(right, str):
        return left.casefold() == right.casefold()
    if type(left) is type(right):
        return left == right
    return number(left) == number(right)


class Evaluator:
    """Loose-typed GitHub semantics: && and || return operands, strings compare case-insensitively."""

    def __init__(self, context, *, status=None):
        self.context = context
        status = status or {}
        self.functions = {
            "always": lambda: True,
            "cancelled": lambda: status.get("cancelled", False),
            "success": lambda: status.get("success", True),
            "failure": lambda: status.get("failure", False),
            "toJSON": lambda value: json.dumps(value, indent=2),
            "format": self.format,
        }

    @staticmethod
    def format(template, *values):
        result = str(template).replace("{{", "\0").replace("}}", "\1")
        for index, value in enumerate(values):
            result = result.replace("{" + str(index) + "}", render(value))
        if re.search(r"\{\d+\}", result):
            raise AssertionError("format argument missing")
        return result.replace("\0", "{").replace("\1", "}")

    def evaluate(self, text):
        self.tokens, position = [], 0
        while position < len(text):
            if not text[position:].strip():
                break
            match = TOKEN.match(text, position)
            if not match or match.end() == position:
                raise AssertionError(f"unsupported expression syntax: {text!r}")
            self.tokens.append((match.lastgroup, match[match.lastgroup]))
            position = match.end()
        self.index = 0
        value = self.disjunction()
        if self.index != len(self.tokens):
            raise AssertionError(f"unsupported expression syntax: {text!r}")
        return value

    def peek(self, value):
        return self.index < len(self.tokens) and self.tokens[self.index] == ("operator", value)

    def take(self, kind=None, value=None):
        if self.index >= len(self.tokens):
            raise AssertionError("unexpected end of expression")
        token = self.tokens[self.index]
        if (kind and token[0] != kind) or (value and token[1] != value):
            raise AssertionError(f"unexpected expression token {token[1]!r}")
        self.index += 1
        return token

    def disjunction(self):
        left = self.conjunction()
        while self.peek("||"):
            self.take()
            right = self.conjunction()
            left = left if truthy(left) else right
        return left

    def conjunction(self):
        left = self.equality()
        while self.peek("&&"):
            self.take()
            right = self.equality()
            left = right if truthy(left) else left
        return left

    def equality(self):
        left = self.unary()
        while self.peek("==") or self.peek("!="):
            operator = self.take()[1]
            right = self.unary()
            left = equal(left, right) == (operator == "==")
        return left

    def unary(self):
        if self.peek("!"):
            self.take()
            return not truthy(self.unary())
        return self.primary()

    def primary(self):
        kind, value = self.take()
        if kind == "string":
            return value[1:-1].replace("''", "'")
        if kind == "number":
            return float(value)
        if kind == "operator" and value == "(":
            result = self.disjunction()
            self.take("operator", ")")
            return result
        if kind != "name":
            raise AssertionError(f"unexpected expression token {value!r}")
        if value in ("true", "false"):
            return value == "true"
        if value == "null":
            return None
        if self.peek("("):
            self.take()
            arguments = []
            while not self.peek(")"):
                arguments.append(self.disjunction())
                if not self.peek(")"):
                    self.take("operator", ",")
            self.take("operator", ")")
            if value not in self.functions:
                raise AssertionError(f"unsupported expression function {value}")
            return self.functions[value](*arguments)
        if value not in self.context:
            raise AssertionError(f"unknown expression context {value}")
        result = self.context[value]
        while self.peek("."):
            self.take()
            name = self.take("name")[1]
            result = result.get(name) if isinstance(result, dict) else None
        return result

    def template(self, text):
        # YAML scalars such as false or 90 reach actions as their string forms.
        if not isinstance(text, str):
            return render(text)
        return TEMPLATE.sub(lambda match: render(self.evaluate(match[1])), text)

    def condition(self, text):
        text = str(text).strip()
        whole = TEMPLATE.fullmatch(text)
        expression = whole[1] if whole else text
        value = truthy(self.evaluate(expression))
        # Actions prefixes success() when no status function is present.
        return value if STATUS.search(expression) else value and self.functions["success"]()


def parse_outputs(text):
    """Read GITHUB_OUTPUT `name=value` and `name<<delimiter` records as Actions does."""
    values, lines, index = {}, text.split("\n"), 0
    while index < len(lines):
        line = lines[index]
        index += 1
        if not line:
            continue
        if "<<" in line and ("=" not in line or line.index("<<") < line.index("=")):
            name, delimiter = line.split("<<", 1)
            end = lines.index(delimiter, index)
            values[name] = "\n".join(lines[index:end])
            index = end + 1
        else:
            name, value = line.split("=", 1)
            values[name] = value
    return values


def run_composite(action, inputs, environment, *, cwd, runner_temp, external=None):
    """Run composite steps with their real input, env, if and output mappings.

    External `uses:` steps are delegated to `external(reference, with_values)`,
    which returns that step's outputs. Run steps append to the caller's
    GITHUB_OUTPUT, and only each step's own records become its outputs.
    Always-run steps still execute after failures. Returns (composite outputs
    or None when any step failed, processes).
    """
    output_file = environment.get("GITHUB_OUTPUT")
    document = yaml.safe_load((action / "action.yml").read_text())
    declared = document.get("inputs", {})
    unknown = set(inputs) - set(declared)
    if unknown:
        raise AssertionError(f"undeclared composite inputs: {sorted(unknown)}")
    values = {name: str(entry.get("default", "")) for name, entry in declared.items()}
    values.update(inputs)
    context = {"inputs": values, "runner": {"temp": str(runner_temp)}, "steps": {},
               "github": {"token": environment.get("GITHUB_TOKEN", "")}}
    status = {"success": True, "failure": False}
    evaluator = Evaluator(context, status=status)
    processes = []
    for step in document["runs"]["steps"]:
        if "if" in step and not evaluator.condition(step["if"]):
            if "id" in step:
                context["steps"][step["id"]] = {"outputs": {}, "outcome": "skipped"}
            continue
        if "if" not in step and not status["success"]:
            if "id" in step:
                context["steps"][step["id"]] = {"outputs": {}, "outcome": "skipped"}
            continue
        outputs = {}
        failed = False
        if "uses" in step:
            arguments = {name: evaluator.template(value) for name, value in step.get("with", {}).items()}
            try:
                outputs = external(step["uses"], arguments) if external else {}
            except subprocess.CalledProcessError:
                failed = True
        else:
            env = dict(environment, GITHUB_ACTION_PATH=str(action))
            env.update({name: evaluator.template(value) for name, value in step.get("env", {}).items()})
            before = len(Path(output_file).read_text(encoding="utf-8")) if output_file and Path(output_file).exists() else 0
            result = subprocess.run(["bash", "--noprofile", "--norc", "-e", "-o", "pipefail", "-c", step["run"]],
                                    cwd=cwd, env=env, text=True, capture_output=True, check=False)
            processes.append(result)
            failed = bool(result.returncode)
            if output_file and Path(output_file).exists():
                outputs = parse_outputs(Path(output_file).read_text(encoding="utf-8")[before:])
        if "id" in step:
            context["steps"][step["id"]] = {"outputs": outputs, "outcome": "failure" if failed else "success"}
        if failed:
            status.update(success=False, failure=True)
    return (None if status["failure"] else
            {name: evaluator.template(entry["value"]) for name, entry in document.get("outputs", {}).items()},
            processes)

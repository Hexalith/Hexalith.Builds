"""Git snapshot comparison and index-only application of perimeter v1.

No module files are opened. Callers supply trusted repository/snapshot handles,
the rendered prefix, and the Builds repository used to peel exact release tags.
Selection is a set of zero-based entry indexes, so restore policy stays outside
this engine. There are no implicit ownership paths or network operations.
"""

from __future__ import annotations

from dataclasses import dataclass
import importlib.util
import json
import os
from pathlib import Path
import re
import string
import subprocess
import tempfile
from typing import Any, Iterable


# Absolute sibling loading works under python -I without accepting a module's
# cwd/PYTHONPATH or requiring a third-party package installation.
_SPEC = importlib.util.spec_from_file_location("_perimeter_config", Path(__file__).with_name("perimeter_config.py"))
config = importlib.util.module_from_spec(_SPEC)
_SPEC.loader.exec_module(config)


class PerimeterError(config.ConfigError):
    """A Git/source/ownership failure; application leaves the selected index intact."""


@dataclass(frozen=True, order=True)
class Drift:
    """One deterministic difference in an owned path."""

    path: str
    kind: str
    reason: str


@dataclass(frozen=True)
class Item:
    mode: str
    oid: str


@dataclass(frozen=True)
class Content:
    mode: str
    data: bytes | None = None
    oid: str | None = None
    partial: bool = False


class Git:
    """A trusted Git repository, optionally using a caller-selected index.

    An explicit ``index_file`` overrides GIT_INDEX_FILE; otherwise that variable
    is preserved. Paths in Git commands are literal, and input/output is binary.
    """

    def __init__(self, repository: str | Path, *, index_file: str | Path | None = None):
        self.root = Path(repository).resolve()
        self.environment = os.environ.copy()
        for key in ("GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_PREFIX"):
            self.environment.pop(key, None)
        self.environment["GIT_LFS_SKIP_SMUDGE"] = "1"
        self.environment["GIT_LITERAL_PATHSPECS"] = "1"
        if index_file is not None:
            self.environment["GIT_INDEX_FILE"] = str(Path(index_file).resolve())

    def run(self, *arguments: str, data: bytes | None = None,
            environment: dict[str, str] | None = None, check: bool = True) -> bytes:
        result = subprocess.run(["git", "-c", "core.hooksPath=/dev/null", "-c", "submodule.recurse=false",
                                 "-c", "index.sparse=false",
                                 "-C", str(self.root), *arguments], input=data, capture_output=True,
                                env=self.environment if environment is None else environment, check=False)
        if check and result.returncode:
            message = result.stderr.decode("utf-8", "backslashreplace").strip()
            raise PerimeterError(f"git {arguments[0]}: {message or 'command failed'}")
        return result.stdout if check or not result.returncode else b""

    def blob(self, oid: str) -> bytes:
        return self.run("cat-file", "blob", oid)

    def snapshot(self, ref: str = "HEAD") -> Snapshot:
        return Snapshot(self, ref)

    def index_path(self) -> Path:
        value = self.environment.get("GIT_INDEX_FILE")
        if value is None:
            value = os.fsdecode(self.run("rev-parse", "--git-path", "index").rstrip(b"\n"))
        path = Path(value)
        return path if path.is_absolute() else self.root / path


class Snapshot:
    """Pinned enumeration of a HEAD/tree or fully expanded stage-zero index."""

    def __init__(self, repository: Git, ref: str = "HEAD"):
        self.git = repository
        self.ref = ref
        self.items: dict[str, Item] = {}
        if ref == "index":
            def staged_records(visibility: str) -> bytes:
                return repository.run("diff", "--cached", "--raw", "--no-abbrev", "-z", "--no-ext-diff",
                                      "--no-textconv", "--no-renames", visibility)

            if staged_records("--ita-visible-in-index") != staged_records("--ita-invisible-in-index"):
                raise PerimeterError("intent-to-add index entries are not a perimeter snapshot")
            records = repository.run("ls-files", "--stage", "-z")
        else:
            # Pin moving refs before enumeration and later blob access.
            self.ref = repository.run("rev-parse", "--verify", f"{ref}^{{tree}}").decode().strip()
            records = repository.run("ls-tree", "-r", "-z", "--full-tree", self.ref)
        for record in records.split(b"\0"):
            if not record:
                continue
            header, raw_path = record.split(b"\t", 1)
            mode, middle, last = header.decode("ascii").split()
            if ref == "index":
                if last != "0":
                    raise PerimeterError("unmerged index is not a perimeter snapshot")
                oid = middle
            else:
                oid = last
            if mode not in ("100644", "100755", "120000", "160000"):
                raise PerimeterError(f"unsupported index/tree mode {mode}")
            path = os.fsdecode(raw_path)
            if path in self.items:
                raise PerimeterError(f"duplicate index path {path}")
            self.items[path] = Item(mode, oid)
        self._blobs: dict[str, bytes] = {}

    def read(self, path: str) -> bytes | None:
        item = self.items.get(path)
        if item is None or item.mode == "160000":
            return None
        if item.oid not in self._blobs:
            self._blobs[item.oid] = self.git.blob(item.oid)
        return self._blobs[item.oid]


def wildmatch(pattern: str, path: str) -> bool:
    """Root-relative Git wildmatch, including zero-directory **/ and classes.

    Match path components separately so ordinary stars cannot cross slashes.
    Bracket classes support both Git negations and its POSIX character classes.
    Only a complete ** path component crosses directories.
    """
    patterns, parts = pattern.split("/"), path.split("/")
    positions = {0}
    for index, pattern_part in enumerate(patterns):
        if pattern_part == "**":
            # A trailing /** means descendants, not the directory itself.
            if index == len(patterns) - 1:
                return any(position < len(parts) for position in positions)
            positions = set(range(min(positions), len(parts) + 1))
        else:
            positions = {position + 1 for position in positions
                         if position < len(parts) and _segment_match(pattern_part, parts[position])}
        if not positions:
            return False
    return len(parts) in positions


def _segment_match(pattern: str, value: str) -> bool:
    classes = {"alnum": "A-Za-z0-9", "alpha": "A-Za-z", "blank": " \\t", "cntrl": "\\x00-\\x1f\\x7f",
               "digit": "0-9", "graph": "\\x21-\\x7e", "lower": "a-z", "print": "\\x20-\\x7e",
               "punct": re.escape(string.punctuation), "space": " \\t\\r\\n\\v\\f", "upper": "A-Z", "xdigit": "A-Fa-f0-9"}
    expression, index = [], 0
    while index < len(pattern):
        char = pattern[index]
        if char in "*?":
            expression.append(b".*" if char == "*" else b".")
        elif char == "[":
            start = index + 1
            negated = start < len(pattern) and pattern[start] in "!^"
            if negated:
                start += 1
            end = start
            while end < len(pattern):
                if (pattern.startswith("[:", end)
                        and (class_end := pattern.find("]", end + 2)) > end + 2
                        and pattern[class_end - 1] == ":"):
                    end = class_end + 1
                elif pattern[end] == "]" and end != start:
                    break
                elif end + 2 < len(pattern) and pattern[end + 1] == "-" and pattern[end + 2] != "]":
                    # Consume the endpoint literally, even when it is '[';
                    # it can no longer start a POSIX class on the next step.
                    end += 3
                else:
                    end += 1
            if end == len(pattern):
                return False
            else:
                raw, chunks, offset = pattern[start:end].encode("utf-8"), [], 0
                while offset < len(raw):
                    if raw.startswith(b"[:", offset) and raw.find(b":]", offset + 2) != -1:
                        stop = raw.index(b":]", offset + 2)
                        name = raw[offset + 2:stop].decode("ascii", "replace")
                        if name not in classes:
                            return False
                        chunks.append(classes[name].encode())
                        offset = stop + 2
                        continue
                    current = raw[offset:offset + 1]
                    chunks.append(re.escape(current))
                    if offset + 2 < len(raw) and raw[offset + 1:offset + 2] == b"-":
                        endpoint = raw[offset + 2:offset + 3]
                        # Git first matches the starting literal, even when
                        # the subsequent range descends and matches nothing.
                        if current <= endpoint:
                            chunks.append(re.escape(current) + b"-" + re.escape(endpoint))
                        offset += 3
                    else:
                        offset += 1
                expression.append(b"[" + (b"^" if negated else b"") + b"".join(chunks) + b"]")
                index = end
        else:
            expression.append(re.escape(char.encode("utf-8")))
        index += 1
    try:
        # Git wildmatch counts path bytes, including for '?' and bracket
        # classes. Keep Unicode literals as UTF-8 sequences, while matching
        # operators against the original filesystem/Git byte representation.
        value_bytes = os.fsencode(value)
        positions = {0}
        for atom in expression:
            if atom == b".*":
                positions = set(range(min(positions), len(value_bytes) + 1))
            else:
                matcher = re.compile(atom, re.DOTALL)
                positions = {match.end() for position in positions
                             if (match := matcher.match(value_bytes, position)) is not None}
            if not positions:
                return False
        return len(value_bytes) in positions
    except re.error:
        return False


def _selector_matches(pattern: str, key: str) -> bool:
    # Only * is a selector wildcard. '/' is ordinary key data, not a separator.
    return re.fullmatch(".*".join(re.escape(part) for part in pattern.split("*")), key, re.DOTALL) is not None


def _prefix(left: str, right: str) -> bool:
    return right.startswith(left + "/")


def _ownership(entries: list[dict[str, Any]]) -> None:
    """Reject incompatible ownership independently of caller selection."""
    whole = {"whole", "whole-dir", "gitlink"}
    for index, entry in enumerate(entries):
        for other in entries[:index]:
            if "path" not in entry or "path" not in other:
                glob, target = (entry, other) if entry["kind"] == "absent-glob" else (other, entry)
                if "path" in target and wildmatch(glob["pattern"], target["path"]):
                    raise PerimeterError(f"conflicting ownership at {target['path']}")
                continue
            path, previous = entry["path"], other["path"]
            if path != previous:
                if _prefix(path, previous) or _prefix(previous, path):
                    raise PerimeterError(f"file/directory ownership overlap: {path}, {previous}")
                continue
            a, b = entry["kind"], other["kind"]
            conflict = a in whole or b in whole
            if a == b == "gitmodules-section":
                conflict = entry["submodulePath"] == other["submodulePath"]
            elif a == b == "absent-line" or a == b == "absent-json":
                conflict = False
            elif a in {"json-superset", "absent-json"} and b in {"json-superset", "absent-json"}:
                first = ([config.pointer_parts(entry["pointer"])] if a == "json-superset" else entry["selectors"])
                second = ([config.pointer_parts(other["pointer"])] if b == "json-superset" else other["selectors"])
                for left in first:
                    for right in second:
                        # Superset pointers are literal; absent selectors may wildcard.
                        overlaps = all((x == y if a == b else
                                        _selector_matches(x, y) if a == "absent-json" else _selector_matches(y, x))
                                       for x, y in zip(left, right))
                        conflict |= overlaps
            else:
                conflict = True
            if conflict:
                raise PerimeterError(f"conflicting ownership at {path}")


@dataclass(frozen=True)
class _JsonNumber:
    token: str


def _json(data: bytes | None) -> Any:
    if data is None:
        raise ValueError("missing JSON")
    try:
        return json.loads(data.decode("utf-8"), object_pairs_hook=config._object, parse_constant=config._constant,
                          parse_int=_JsonNumber, parse_float=_JsonNumber)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise config.ConfigError(f"partial JSON: {error}") from error


def _json_bytes(value: Any) -> bytes:
    def encode(node: Any, depth: int = 0) -> str:
        if isinstance(node, _JsonNumber):
            return node.token
        if isinstance(node, (dict, list)):
            opening, closing = ("{", "}") if isinstance(node, dict) else ("[", "]")
            if not node:
                return opening + closing
            children = ([json.dumps(key, ensure_ascii=True) + ": " + encode(child, depth + 1)
                         for key, child in node.items()] if isinstance(node, dict)
                        else [encode(child, depth + 1) for child in node])
            return opening + "\n" + ",\n".join("  " * (depth + 1) + child for child in children) + "\n" + "  " * depth + closing
        return json.dumps(node, ensure_ascii=True, allow_nan=False)

    try:
        return (encode(value) + "\n").encode()
    except RecursionError as error:
        raise PerimeterError("JSON nesting exceeds the repair limit") from error


def _superset(data: bytes | None, entry: dict) -> tuple[bool, bytes]:
    parts = config.pointer_parts(entry["pointer"])
    try:
        document = _json(data)
        parsed = True
    except ValueError:
        document = {} if parts else []
        parsed = False

    def repair(node: Any, offset: int) -> tuple[Any, bool]:
        if offset == len(parts):
            valid = isinstance(node, list) and all(isinstance(value, str) for value in node)
            if not valid:
                node = []
            missing = [value for value in entry["values"] if value not in node]
            node.extend(missing)
            return node, valid and not missing
        part = parts[offset]
        if isinstance(node, list):
            if (re.fullmatch(r"0|[1-9][0-9]*", part) is None
                    or len(part) > len(str(len(node))) or int(part) >= len(node)):
                raise PerimeterError(f"{entry['path']}: pointer cannot repair an absent array position")
            index = int(part)
            node[index], valid = repair(node[index], offset + 1)
        else:
            if not isinstance(node, dict):
                node = {}
            node[part], valid = repair(node.get(part), offset + 1)
        return node, valid

    repaired, valid = repair(document, 0)
    # A missing source with an empty required array must still create the file.
    if parsed and valid:
        return True, data
    return False, _json_bytes(repaired)


def _absent_json(data: bytes | None, entry: dict) -> tuple[bool, bytes]:
    if data is None:
        return True, b""
    try:
        document = _json(data)
    except ValueError:
        return False, b"{}\n"
    changed = False

    def remove(value: Any, selector: list[str]) -> None:
        nonlocal changed
        if not isinstance(value, dict):
            return
        for key in list(value):
            if _selector_matches(selector[0], key):
                if len(selector) == 1:
                    del value[key]
                    changed = True
                else:
                    remove(value[key], selector[1:])

    for selector in entry["selectors"]:
        remove(document, selector)
    return not changed, _json_bytes(document)


def _text(data: bytes | None) -> str:
    if data is None:
        raise ValueError("missing text")
    return data.decode("utf-8")


def _marker(data: bytes | None, entry: dict) -> tuple[bool, bytes]:
    body = entry["text"].replace("\r\n", "\n")
    block = config.BEGIN + "\n" + body + ("" if body.endswith("\n") or not body else "\n") + config.END
    try:
        text = _text(data)
    except (ValueError, UnicodeError):
        return False, (block + "\n").encode()
    begin, end = text.count(config.BEGIN), text.count(config.END)
    if begin == end == 0:
        return False, (text + ("" if not text or text.endswith("\n") else "\n") + block + "\n").encode()
    if begin != 1 or end != 1 or text.index(config.BEGIN) >= text.index(config.END):
        return False, (block + "\n").encode()
    start, stop = text.index(config.BEGIN), text.index(config.END) + len(config.END)
    return text[start:stop].replace("\r\n", "\n") == block, (text[:start] + block + text[stop:]).encode()


def _lines(text: str) -> list[str]:
    """Physical Git/shell lines; only LF terminates a record."""
    return re.findall(r"[^\n]*\n|[^\n]+$", text)


def _line(data: bytes | None, entry: dict) -> tuple[bool, bytes]:
    if data is None:
        return True, b""
    try:
        text = _text(data)
        if "\0" in text:
            raise ValueError("NUL in line data")
    except (ValueError, UnicodeError):
        return False, b""
    pattern = re.compile(entry["pattern"])
    lines = _lines(text)
    retained = [line for line in lines if pattern.search(line.replace("\r\n", "\n").removesuffix("\n")) is None]
    return len(retained) == len(lines), "".join(retained).encode()


def _git_config(git: Git, text: str) -> list[tuple[str, str]]:
    raw = git.run("config", "--no-includes", "--null", "--file", "-", "--list", data=text.encode())
    return [tuple(record.decode().split("\n", 1)) if b"\n" in record else (record.decode(), "")
            for record in raw.split(b"\0") if record]


_HEADER = re.compile(r'^[ \t\r]*\[[A-Za-z0-9.-]+(?:[ \t\r]+"(?:[^"\\\n]|\\.)*")?[ \t\r]*\]', re.MULTILINE)
_KEY = re.compile(r"^\s*([A-Za-z][A-Za-z0-9-]*)\s*(?:=|$)")


def _continuation(line: str, quoted: bool) -> tuple[bool, bool]:
    """Recognize Git value continuations, excluding backslashes in comments."""
    value = line.removesuffix("\r\n").removesuffix("\n")
    escaped = False
    for char in value:
        if escaped:
            escaped = False
        elif char == "\\":
            escaped = True
        elif char == '"':
            quoted = not quoted
        elif char in "#;" and not quoted:
            return False, False
    return escaped, quoted if escaped else False


def _headers(text: str) -> list[re.Match]:
    result = []
    offset, continued, quoted = 0, False, False
    for line in _lines(text):
        if not continued:
            match = _HEADER.match(text, offset)
            if match:
                result.append(match)
        continued, quoted = _continuation(line, quoted)
        offset += len(line)
    return result


def _finish_record(text: str) -> str:
    """Terminate a valid final Git continuation without changing its value."""
    continued, quoted = False, False
    for line in _lines(text):
        continued, quoted = _continuation(line, quoted)
    return text + ("" if not text or text.endswith("\n") else "\n") + ("\n" if continued else "")


def _gitmodules(data: bytes | None, entry: dict, git: Git) -> tuple[bool, bytes]:
    def section(name: str, extra: str = "") -> str:
        escaped = name.replace("\\", "\\\\").replace('"', '\\"')
        url = entry["url"].replace("\\", "\\\\").replace('"', '\\"')
        path = entry["submodulePath"].replace('"', '\\"')
        return f'[submodule "{escaped}"]\n\tpath = "{path}"\n\turl = "{url}"\n' + extra

    try:
        text = _text(data)
        bom = "\ufeff" if text.startswith("\ufeff") else ""
        text = text.removeprefix(bom) if bom else text
        if "\0" in text:
            raise ValueError("NUL in Git configuration")
        _git_config(git, text)  # Git, rather than an INI approximation, validates syntax.
        headers = _headers(text)
        chunks = []
        for index, header in enumerate(headers):
            end = headers[index + 1].start() if index + 1 < len(headers) else len(text)
            chunk = text[header.start():end]
            properties = _git_config(git, chunk)
            # A section without keys still has a name: use a sentinel query.
            header_text = header.group()
            identity = _git_config(git, header_text + "\nperimeter-identity = yes\n")[0][0]
            name = identity[:-len(".perimeter-identity")]
            chunks.append((chunk, properties, name))
    except (ValueError, UnicodeError, PerimeterError):
        return False, section(entry["submodulePath"]).encode()
    matches = [index for index, (_, properties, _) in enumerate(chunks)
               if any(key.startswith("submodule.") and key.endswith(".path") and value == entry["submodulePath"]
                      for key, value in properties)]
    if not matches:
        default_name = "submodule." + entry["submodulePath"]
        defaults = [index for index, (_, _, name) in enumerate(chunks) if name == default_name]
        if any(key == default_name + ".path" and value != entry["submodulePath"]
               for index in defaults for key, value in chunks[index][1]):
            raise PerimeterError(f"{entry['path']}: default submodule section name belongs to unrelated state")
        if defaults:
            matches = [defaults[0]]
        else:
            return False, (bom + _finish_record(text) + section(entry["submodulePath"])).encode()
    first = matches[0]
    chunk, properties, name = chunks[first]
    matching_names = {chunks[index][2] for index in matches}
    # Repeated headers with the same subsection name are one logical Git
    # section. A fragment without path may still override its owned URL or
    # branch; a different path under that name is ambiguous foreign state.
    for _, fields, section_name in chunks:
        if section_name in matching_names and any(key == section_name + ".path" and value != entry["submodulePath"]
                                                 for key, value in fields):
            raise PerimeterError(f"{entry['path']}: matching submodule section name belongs to unrelated state")
    properties = [field for _, fields, section_name in chunks if section_name == name for field in fields]
    expected = {name + ".path": [entry["submodulePath"]], name + ".url": [entry["url"]]}
    values = {key: [value for field, value in properties if field == key] for key in expected}
    forbidden = any(key in (name + ".branch", name + ".update") for key, _ in properties)
    if len(matches) == 1 and values == expected and not forbidden:
        return True, data
    # Keep all foreign keys, comments and values, including continuations. Strip
    # only path/url/branch/update logical records from the retained section.
    header_length = headers[first].end() - headers[first].start()
    def strip_owned(chunk: str, header_length: int) -> str:
        retained = []
        skipping = False
        continuation, quoted = False, False
        for line in _lines(chunk[header_length:]):
            match = _KEY.match(line) if not continuation else None
            if not continuation:
                skipping = bool(match and match[1].lower() in {"path", "url", "branch", "update"})
            if not skipping:
                retained.append(line)
            continuation, quoted = _continuation(line, quoted)
        return chunk[:header_length] + "".join(retained)

    retained_text = strip_owned(chunk, header_length)
    # The existing header/name stays byte-for-byte; new values are appended.
    additions = section(entry["submodulePath"]).split("\n", 1)[1]
    retained_text = _finish_record(retained_text) + additions
    result = text[:headers[0].start()] if headers else text
    for index, (original, _, section_name) in enumerate(chunks):
        if index == first:
            result += retained_text
        elif index in matches or section_name in matching_names - {name}:
            continue
        elif section_name == name:
            result += strip_owned(original, headers[index].end() - headers[index].start())
        else:
            result += original
    _git_config(git, result)
    return False, (bom + result).encode()


def _partial(data: bytes | None, entry: dict, git: Git) -> tuple[bool, bytes]:
    return {"json-superset": _superset, "absent-json": _absent_json,
            "marker-block": _marker, "absent-line": _line}.get(entry["kind"],
                lambda value, rule: _gitmodules(value, rule, git))(data, entry)


class _Plan:
    def __init__(self, document: dict, actual: Snapshot, expected: Snapshot,
                 rendered_root: str, builds: Git, selected: Iterable[int] | None):
        config.validate_config(document)
        entries = document["entries"]
        _ownership(entries)
        if rendered_root and not config.repository_path(rendered_root):
            raise PerimeterError("unsafe rendered-root prefix")
        prefix = rendered_root + "/" if rendered_root else ""
        absent_patterns = [entry["pattern"] for entry in entries if entry["kind"] == "absent-glob"]
        for entry in entries:
            if entry["kind"] == "whole-dir":
                for source in expected.items:
                    if _prefix(prefix + entry["path"], source):
                        target = source[len(prefix):]
                        if any(wildmatch(pattern, target) for pattern in absent_patterns):
                            raise PerimeterError(f"conflicting ownership at {target}")
        indexes = list(range(len(entries))) if selected is None else list(selected)
        if any(type(index) is not int or not 0 <= index < len(entries) for index in indexes):
            raise PerimeterError("selected entry indexes must exist in the perimeter")
        if len(set(indexes)) != len(indexes):
            raise PerimeterError("duplicate selected entry index")
        self.selected = indexes
        self.drift: list[Drift] = []
        self.changes: dict[str, Content | None] = {}
        self.actual = actual
        rules = [entries[index] for index in sorted(indexes)]

        def full_ownership(path: str) -> bool:
            return any((rule["kind"] in {"whole", "gitlink"} and rule["path"] == path)
                       or (rule["kind"] == "whole-dir" and (rule["path"] == path or _prefix(rule["path"], path)))
                       or (rule["kind"] == "absent-glob" and wildmatch(rule["pattern"], path)) for rule in rules)

        def set_change(path: str, value: Content | None) -> None:
            if path in self.changes and (self.changes[path] is None) != (value is None):
                raise PerimeterError(f"conflicting ownership at {path}")
            self.changes[path] = value

        for entry in rules:
            kind = entry["kind"]
            if kind == "absent-glob":
                for path in sorted(actual.items):
                    if wildmatch(entry["pattern"], path):
                        self.drift.append(Drift(path, kind, "forbidden"))
                        set_change(path, None)
                continue
            path = entry["path"]
            if kind in {"whole", "whole-dir"}:
                prefix = rendered_root + "/" if rendered_root else ""
                sources = {name[len(prefix):]: item for name, item in expected.items.items()
                           if name == prefix + path} if kind == "whole" else {
                               name[len(prefix):]: item for name, item in expected.items.items()
                               if _prefix(prefix + path, name)}
                if not sources:
                    raise PerimeterError(f"missing expected source: {prefix}{path}")
                for target, item in sorted(sources.items()):
                    if item.mode == "160000":
                        raise PerimeterError(f"expected whole source is not a blob: {prefix}{target}")
                    data = expected.read(prefix + target)
                    current = actual.items.get(target)
                    reason = ("missing" if current is None else "mode" if current.mode != item.mode
                              else "content" if actual.read(target) != data else "")
                    if reason:
                        self.drift.append(Drift(target, kind, reason))
                        set_change(target, Content(item.mode, data))
                if kind == "whole-dir":
                    for target in sorted(actual.items):
                        if (target == path or _prefix(path, target)) and target not in sources:
                            self.drift.append(Drift(target, kind, "unexpected"))
                            set_change(target, None)
                continue
            if kind == "gitlink":
                oid = builds.run("rev-parse", "--verify", f"refs/tags/{entry['buildsTag']}^{{}}").decode().strip()
                if builds.run("cat-file", "-t", oid).strip() != b"commit":
                    raise PerimeterError(f"{entry['buildsTag']}: expected tag must resolve to a commit")
                if actual.items.get(path) != Item("160000", oid):
                    self.drift.append(Drift(path, kind, "gitlink"))
                    set_change(path, Content("160000", oid=oid))
                continue
            item = actual.items.get(path)
            regular = item is not None and item.mode in {"100644", "100755"}
            # Absence and a malformed non-regular target are distinct: the
            # latter must recreate owned state ({} for absence-only JSON).
            original = actual.read(path) if regular else b"\xff" if item is not None else None
            compliant, repaired = _partial(original, entry, actual.git)
            if not regular and item is not None:
                compliant = False
            if not compliant:
                self.drift.append(Drift(path, kind, "missing" if item is None else "partial"))
                current = self.changes.get(path)
                if current is not None:
                    _, repaired = _partial(current.data, entry, actual.git)
                set_change(path, Content(item.mode if regular else "100644", repaired, partial=True))

        # Validate every final addition against selected ownership before writes.
        for path, content in list(self.changes.items()):
            if content is None:
                continue
            for other in actual.items:
                if _prefix(path, other) or _prefix(other, path):
                    if not full_ownership(other):
                        raise PerimeterError(f"repair of {path} would remove unselected path {other}")
                    if other not in self.changes:
                        self.changes[other] = None
            for other, value in self.changes.items():
                if value is not None and (_prefix(path, other) or _prefix(other, path)):
                    raise PerimeterError(f"file/directory conflict in repair: {path}, {other}")
        self.drift.sort()


def compare(document: dict, actual: Snapshot, *, expected: Snapshot,
            rendered_root: str, builds: Git, selected: Iterable[int] | None = None) -> list[Drift]:
    """Compare selected entries against HEAD/tree or index without changing files."""
    return _Plan(document, actual, expected, rendered_root, builds, selected).drift


def _attribute_tree(repository: Git, snapshot: Snapshot) -> str:
    """Build the attribute source directly from Git objects, without refresh.

    Some Git write-tree/index-refresh paths consult worktree conversion filters.
    mktree consumes our already enumerated object identities instead. Only
    .gitattributes blobs are needed for tree-based attribute resolution.
    """
    tree: dict = {}
    for path, item in snapshot.items.items():
        parts = path.split("/")
        if parts[-1] != ".gitattributes" or item.mode not in {"100644", "100755"}:
            continue
        node = tree
        for part in parts[:-1]:
            node = node.setdefault(part, {})
        node[parts[-1]] = item

    def write(node: dict) -> str:
        records = []
        for name, child in sorted(node.items()):
            if isinstance(child, dict):
                mode, kind, oid = "040000", "tree", write(child)
            else:
                mode, kind, oid = child.mode, "blob", child.oid
            records.append(f"{mode} {kind} {oid}\t".encode() + os.fsencode(name) + b"\0")
        return repository.run("mktree", "-z", data=b"".join(records)).decode().strip()

    return write(tree)


def apply(document: dict, repository: Git, *, expected: Snapshot,
          rendered_root: str, builds: Git, selected: Iterable[int] | None = None) -> list[Drift]:
    """Preflight all selected repairs, then atomically replace only their index data.

    A private candidate index makes command/filter errors atomic too. The real
    index lock and byte identity check protect unrelated concurrent staging.
    Hash attributes come from the pre-repair selected index tree, never dirty
    working-tree .gitattributes. No worktree path is written or removed.
    """
    index = repository.index_path()
    before = index.read_bytes() if index.exists() else None
    plan = _Plan(document, repository.snapshot("index"), expected, rendered_root, builds, selected)
    if not plan.changes:
        return plan.drift
    lock = Path(str(index) + ".lock")
    descriptor = os.open(lock, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    candidate: Path | None = None
    try:
        os.close(descriptor)
        if (index.read_bytes() if index.exists() else None) != before:
            raise PerimeterError("selected index changed during perimeter preflight")
        descriptor, name = tempfile.mkstemp(prefix="perimeter-index-", dir=index.parent)
        candidate = Path(name)
        with os.fdopen(descriptor, "wb") as stream:
            if before is not None:
                stream.write(before)
                os.fchmod(stream.fileno(), index.stat().st_mode & 0o777)
        if before is None:
            candidate.unlink()
        environment = repository.environment | {"GIT_INDEX_FILE": str(candidate)}
        tree = _attribute_tree(repository, plan.actual)
        environment["GIT_ATTR_SOURCE"] = tree
        removals = [path for path, content in sorted(plan.changes.items()) if content is None]
        if removals:
            repository.run("rm", "--cached", "--sparse", "--force", "--quiet",
                           "--pathspec-from-file=-", "--pathspec-file-nul",
                           data=b"".join(os.fsencode(path) + b"\0" for path in removals), environment=environment)
        for path, content in sorted(plan.changes.items()):
            if content is None:
                continue
            oid = content.oid
            if oid is None:
                conversion = "--no-filters" if content.mode == "120000" else f"--path={path}"
                oid = repository.run("hash-object", "-w", conversion, "--stdin", data=content.data,
                                     environment=environment).decode().strip()
                hashed = repository.blob(oid)
                if hashed != content.data and (not content.partial or
                        hashed.replace(b"\r\n", b"\n") != content.data.replace(b"\r\n", b"\n")):
                    raise PerimeterError(f"{path}: selected index attributes prevent a compliant repair without changing proposed content")
            repository.run("update-index", "--add", "--cacheinfo", content.mode, oid, path,
                           environment=environment)
        # Clean filters can transform even stdin. Refuse a candidate whose
        # resulting blobs no longer satisfy the selected ownership contract.
        candidate_git = Git(repository.root, index_file=candidate)
        remaining = _Plan(document, candidate_git.snapshot("index"), expected,
                          rendered_root, builds, plan.selected).drift
        if remaining:
            raise PerimeterError("selected index attributes prevent a compliant perimeter repair")
        os.replace(candidate, index)
        candidate = None
    finally:
        if candidate is not None:
            candidate.unlink(missing_ok=True)
            Path(str(candidate) + ".lock").unlink(missing_ok=True)
        lock.unlink(missing_ok=True)
    return plan.drift

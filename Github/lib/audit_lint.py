"""Reject literal module-owned audit waivers without evaluating MSBuild.

All bytes come from pinned Git objects. XML comments are ignored; ordinary
non-XML and binary files are left alone. Response files intentionally use a
conservative text scan, including text inside comments.
"""

from __future__ import annotations

import codecs
import re
import xml.etree.ElementTree as ET
from xml.parsers import expat

from expected_state import ExpectedStateError


REGULAR = {"100644", "100755"}
BLOCKING = {"nu1900", "nu1903", "nu1904", "nu1905"}
FORBIDDEN = {
    "nugetauditsuppress": "NuGetAuditSuppress",
    "msbuildwarningsasmessages": "MSBuildWarningsAsMessages",
    "auditsources": "auditSources",
    "testingplatformcommandlinearguments": "TestingPlatformCommandLineArguments",
    "vstesttestcasefilter": "VSTestTestCaseFilter",
}
NUGET_SECTIONS = {"auditsources", "packagesources", "packagesourcemapping", "trustedsigners"}
PROJECT_SUFFIXES = (".csproj", ".fsproj", ".vbproj", ".proj", ".props", ".targets")


def _local(name: str) -> str:
    return name.rsplit("}", 1)[-1].rsplit(":", 1)[-1].lower()


def _tokens(value: str) -> set[str]:
    return set(re.split(r"[,;\s]+", value.strip().lower())) - {""}


def _declaration(name: str, value: str) -> str | None:
    name = _local(name)
    if name in FORBIDDEN:
        return "audit-lint: forbidden " + FORBIDDEN[name] + " declaration"
    if name == "nowarn":
        codes = sorted(_tokens(value) & BLOCKING)
        if codes:
            return "audit-lint: NoWarn suppresses blocking " + ", ".join(code.upper() for code in codes)
    if name == "treataslocalproperty" and any(token.startswith("nugetaudit") for token in _tokens(value)):
        return "audit-lint: TreatAsLocalProperty names a NuGetAudit-prefixed property"
    return None


def _utf32_encoding(data: bytes) -> str | None:
    if data.startswith((codecs.BOM_UTF32_LE, codecs.BOM_UTF32_BE)):
        return "utf-32"
    if data.startswith(b"<\x00\x00\x00"):
        return "utf-32-le"
    if data.startswith(b"\x00\x00\x00<"):
        return "utf-32-be"
    return None


def _xml_probe(data: bytes) -> tuple[str, set[str]]:
    """Observe root/section names even when a later XML token is malformed."""
    root = ""
    sections = set()
    parser = expat.ParserCreate(namespace_separator="}")

    def start(name, _attributes):
        nonlocal root
        local = _local(name)
        if not root:
            root = local
        sections.add(local)

    parser.StartElementHandler = start
    # Never resolve a DTD or an external entity. Expat does not fetch external
    # entities by default; this handler also makes that decision explicit.
    parser.ExternalEntityRefHandler = lambda *_arguments: 0
    try:
        parser.Parse(data, True)
    except (expat.ExpatError, ValueError, LookupError):
        pass
    if not root:
        # Expat emits StartElement only after the complete start tag. A
        # malformed attribute or invalid encoding declaration must not hide
        # a recognizable Project root. This lexical probe only classifies a
        # failed parse; it never accepts or evaluates the malformed content.
        if utf32 := _utf32_encoding(data):
            encoding = utf32
        elif data.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)):
            encoding = "utf-16"
        elif data.startswith(b"<\x00"):
            encoding = "utf-16-le"
        elif data.startswith(b"\x00<"):
            encoding = "utf-16-be"
        else:
            encoding = "utf-8-sig"
        text = data.decode(encoding, errors="replace")
        # Advance through the prolog without repeatedly copying its suffix.
        # Atomic subset tokens also prevent exponential retries on a missing ']'.
        doctype = r'''<!DOCTYPE\b(?:[^>"'\[]|"[^"]*"|'[^']*'|\[(?><!--.*?-->|[^\]"']|"[^"]*"|'[^']*')*\])*>'''
        prolog = re.compile(r"\s*(?:<\?.*?\?>|<!--.*?-->|" + doctype + ")", re.DOTALL)
        offset = 0
        while match := prolog.match(text, offset):
            offset = match.end()
        text = text[offset:]
        match = re.match(r"\A\s*<(?:[A-Za-z_][\w.-]*:)?([A-Za-z_][\w.-]*)(?=[\s/>]|$)", text)
        if match:
            root = match[1].lower()
            sections.update(name.lower() for name in re.findall(
                r"<(?:[A-Za-z_][\w.-]*:)?(auditSources|packageSources|packageSourceMapping|trustedSigners)(?=[\s/>]|$)",
                text, re.IGNORECASE))
    return root, sections


def _xml(data: bytes, *, canonical: bool) -> list[str]:
    try:
        # Expat supports XML's UTF-8/16 byte inputs directly. UTF-32 BOMs
        # identify a safe explicit decoder before the same stdlib XML parser.
        encoding = _utf32_encoding(data)
        source = data.decode(encoding) if encoding else data
        root = ET.fromstring(source)
    except (ET.ParseError, ValueError, LookupError, UnicodeError):
        name, sections = _xml_probe(data)
        if canonical or name == "project" or name == "configuration" and sections & NUGET_SECTIONS:
            return ["audit-lint: malformed or undecodable project/NuGet XML"]
        return []
    name = _local(root.tag)
    sections = {_local(element.tag) for element in root.iter() if isinstance(element.tag, str)}
    if not (canonical or name == "project" or name == "configuration" and sections & NUGET_SECTIONS):
        return []
    findings = set()
    for element in root.iter():
        if not isinstance(element.tag, str):
            continue
        value = "".join(element.itertext()) if _local(element.tag) in {"nowarn", "treataslocalproperty"} else ""
        finding = _declaration(element.tag, value)
        if finding:
            findings.add(finding)
        for key, value in element.attrib.items():
            finding = _declaration(key, value)
            if finding:
                findings.add(finding)
    return sorted(findings)


def _response(data: bytes) -> list[str]:
    try:
        if data.startswith((codecs.BOM_UTF32_LE, codecs.BOM_UTF32_BE)):
            text = data.decode("utf-32")
        elif data.startswith((codecs.BOM_UTF16_LE, codecs.BOM_UTF16_BE)):
            text = data.decode("utf-16")
        else:
            text = data.decode("utf-8-sig")
        if "\x00" in text:
            raise UnicodeError("NUL is not response text")
    except UnicodeError:
        return ["audit-lint: response file must have a valid text encoding"]
    findings = set()
    for name, display in FORBIDDEN.items():
        if re.search(r"(?<![\w.-])" + name + r"(?![\w.-])", text, re.IGNORECASE):
            findings.add("audit-lint: forbidden " + display + " declaration")
    # Quotes can surround a whole /p: assignment or only its value. Warning and
    # local-property lists use comma, semicolon or whitespace in either form.
    for match in re.finditer(r'''(?<![\w.-])[-/]?(["']?)(NoWarn|TreatAsLocalProperty)\1\s*([=:>])\s*''',
                             text, re.IGNORECASE):
        value = text[match.end():]
        if match[3] == ">":  # Preserve XML-like declaration scanning in response text.
            value = value.split("<", 1)[0]
        value = re.split(r"\s+[\"']?[-/]\w", value, maxsplit=1)[0]
        value = value.replace('"', "").replace("'", "")
        finding = _declaration(match[2], value)
        if finding:
            findings.add(finding)
    return sorted(findings)


def lint(snapshot) -> None:
    """Inspect only the supplied module/sample snapshot, never its workspace."""
    findings = []
    for path, item in sorted(snapshot.items.items()):
        lower = path.lower()
        response = lower.endswith(".rsp")
        canonical = lower.rsplit("/", 1)[-1] == "nuget.config"
        if item.mode not in REGULAR:
            if response or canonical or lower.endswith(PROJECT_SUFFIXES):
                findings.append(path + ": audit-lint selected input must be a regular committed blob")
            continue
        data = snapshot.read(path)
        rules = _response(data) if response else _xml(data, canonical=canonical)
        findings.extend(path + ": " + rule for rule in rules)
    if findings:
        raise ExpectedStateError(*findings)

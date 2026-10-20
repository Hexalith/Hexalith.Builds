"""Retain metadata from an actual outer xUnit TRX without test argument values.

This evidence-only transformation preserves native test IDs, method identities,
outcomes, timing and counters. It does not generate or execute tests.
"""

import hashlib
import json
from pathlib import Path
import xml.etree.ElementTree as ET


ROOT = Path(__file__).resolve().parent
REPORT = ROOT / "native-source-regressions.trx"
BINDING = ROOT / "native-regressions.binding.json"


def local_name(element):
    return element.tag.rsplit("}", 1)[-1]


def semantics(document):
    return {
        "results": [
            dict(element.attrib)
            | {"testName": element.attrib.get("testName", "").split("(", 1)[0], "computerName": ""}
            for element in document.iter()
            if local_name(element) == "UnitTestResult"
        ],
        "methods": [
            {key: value for key, value in element.attrib.items() if key != "codeBase"}
            for element in document.iter()
            if local_name(element) == "TestMethod"
        ],
        "counters": [dict(element.attrib) for element in document.iter() if local_name(element) == "Counters"],
        "summary": [element.attrib.get("outcome") for element in document.iter() if local_name(element) == "ResultSummary"],
        "definitions": [element.attrib.get("id") for element in document.iter() if local_name(element) == "UnitTest"],
    }


binding = json.loads(BINDING.read_text())
if "redaction" in binding:
    raise SystemExit("Redaction already bound; preserve the recorded original report hash.")
original = REPORT.read_bytes()
document = ET.fromstring(original)
before = semantics(document)
removed_arguments = 0
removed_attributes = 0
removed_elements = 0
for element in document.iter():
    for key in list(element.attrib):
        if key in {"runUser", "computerName", "runDeploymentRoot"}:
            del element.attrib[key]
            removed_attributes += 1
        elif key in {"codeBase", "storage"}:
            element.attrib[key] = element.attrib[key].replace("\\", "/").rsplit("/", 1)[-1]
        elif key in {"name", "testName"} and "(" in element.attrib[key]:
            element.attrib[key] = element.attrib[key].split("(", 1)[0]
            removed_arguments += 1
    for child in list(element):
        if local_name(child) in {"Output", "RunInfos", "ResultFiles", "CollectorDataEntries"}:
            element.remove(child)
            removed_elements += 1
document.attrib["name"] = "native-source-regressions"
assert semantics(document) == before, "Native IDs, methods, outcomes, timings or counters changed"
ET.register_namespace("", document.tag.split("}", 1)[0].lstrip("{"))
retained = ET.tostring(document, encoding="utf-8", xml_declaration=True) + b"\n"
assert semantics(ET.fromstring(retained)) == before
REPORT.write_bytes(retained)
binding["reportSha256"] = hashlib.sha256(retained).hexdigest()
binding["redaction"] = {
    "originalReportSha256": hashlib.sha256(original).hexdigest(),
    "retainedReportSha256": binding["reportSha256"],
    "policy": "Remove display-name argument text, machine/user/path metadata and captured output; preserve native IDs, method identities, outcomes, timings and counters.",
    "rawReportRetained": False,
    "nativeSemanticsUnchanged": True,
    "argumentAttributesRedacted": removed_arguments,
    "identityAttributesRemoved": removed_attributes,
    "outputElementsRemoved": removed_elements,
    "script": "redact-native-report.py",
}
BINDING.write_text(json.dumps(binding, indent=2) + "\n")
print(json.dumps(binding["redaction"], indent=2))

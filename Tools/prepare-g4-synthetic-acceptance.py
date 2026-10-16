#!/usr/bin/env python3
"""Refresh only the synthetic P0 acceptance corpus from the evaluated catalog.

Real acceptance evidence and the legacy v1 module-manifest pin are out of scope.
Byte edits preserve malformed/duplicate-key controls and deliberate bad hashes.
"""

import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess
import tempfile


PIN_NAMES = ("eventStoreVersion", "daprRuntimeVersion", "daprSdkVersion", "frontComposerVersion")
HASH = re.compile(rb"(?<![0-9A-Fa-f])[0-9A-Fa-f]{64}(?![0-9A-Fa-f])")


def digest(body):
    return hashlib.sha256(body).hexdigest()


def prepare(original, catalog):
    """Return a deterministic byte map; retain off-pin mutations and bad hashes."""
    baseline = json.loads(original["positive/p0-acceptance.json"])["platformPins"]
    mismatch = json.loads(original["negative/pin-mismatch.json"])["platformPins"]
    if mismatch["daprRuntimeVersion"] == catalog["daprRuntimeVersion"]:
        raise ValueError("The Dapr pin-mismatch control must remain off catalog; review it explicitly.")
    updated = dict(original)
    for name, body in original.items():
        if name.endswith(".expected.json"):
            continue
        for pin in PIN_NAMES:
            pattern = re.compile(rb'("' + pin.encode() + rb'"\s*:\s*")' + re.escape(baseline[pin].encode()) + rb'(")')
            body = pattern.sub(lambda m: m[1] + catalog[pin].encode() + m[2], body)
        updated[name] = body

    # Each pass propagates hashes one edge through the artifact dependency graph.
    # Start from the original bytes every time, so a deliberate mismatch cannot
    # accidentally become valid by repeatedly replacing a newly generated hash.
    pinned = dict(updated)
    for _ in range(len(original) + 1):
        replacements = {}
        for name, body in updated.items():
            old, new = digest(original[name]), digest(body)
            if old in replacements and replacements[old] != new:
                raise ValueError("Ambiguous original artifact digest")
            replacements[old] = new

        def rebind(match):
            old = match[0].decode()
            new = replacements.get(old.lower(), old)
            return (new.upper() if old.isupper() else new).encode()

        result = {name: body if name.endswith(".expected.json") else HASH.sub(rebind, body)
                  for name, body in pinned.items()}
        if result == updated:
            validate(result, catalog)
            return result
        updated = result
    raise ValueError("Fixture hash graph did not converge; review cyclic bindings")


def validate(prepared, catalog):
    """Reject partial refreshes or unexpected drift rather than certify a fixed point."""
    prefix = "test/fixtures/evidence/acceptance/"
    pairs = {"evidencePath": "evidenceSha256", "artifactPath": "artifactSha256",
             "manifestPath": "manifestSha256", "fixturePath": "fixtureHash"}
    allowed_bad_hash = {("negative/manifest-hash-mismatch.json", "manifestSha256"),
                        ("negative/tampered-evidence-hash.json", "evidenceSha256")}

    def bindings(value, name):
        if isinstance(value, dict):
            for path_key, hash_key in pairs.items():
                if path_key == "manifestPath" and "manifestHash" in value:
                    hash_key = "manifestHash"
                path, expected = value.get(path_key), value.get(hash_key)
                if isinstance(path, str) and path.startswith(prefix) and path.endswith(".json"):
                    if path[len(prefix):] not in prepared:
                        raise ValueError(f"Missing referenced synthetic artifact: {path}")
                    if (name, hash_key) in allowed_bad_hash and expected == "0" * 64:
                        continue
                    if not isinstance(expected, str) or digest(prepared[path[len(prefix):]]) != expected.lower():
                        raise ValueError(f"Unexpected binding drift in {name}: {hash_key}; review partial preparation")
            for child in value.values():
                bindings(child, name)
        elif isinstance(value, list):
            for child in value:
                bindings(child, name)

    for name in prepared:
        if name.startswith(("positive/", "negative/")):
            partner = name.replace(".expected.json", ".json") if name.endswith(".expected.json") else name[:-5] + ".expected.json"
            if partner not in prepared:
                raise ValueError(f"Missing fixture/expectation partner for {name}")

    for name, body in prepared.items():
        document = json.loads(body)
        if isinstance(document, dict):
            pins = document.get("platformPins", document.get("topology", {}).get("platform"))
            if pins is not None:
                for pin in PIN_NAMES:
                    if name == "negative/pin-mismatch.json" and pin == "daprRuntimeVersion":
                        continue
                    if pins.get(pin) != catalog[pin]:
                        raise ValueError(f"Unexpected pin drift in {name}: {pin}; review partial preparation")
        bindings(document, name)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--write", action="store_true", help="Apply the computed fixture edits; otherwise check only")
    args = parser.parse_args()
    repository = Path(__file__).resolve().parents[1]
    corpus = repository / "test/fixtures/evidence/acceptance"
    with tempfile.TemporaryDirectory(prefix="hexalith-synthetic-acceptance-") as scratch:
        catalog_path = Path(scratch) / "catalog.json"
        subprocess.run(["pwsh", "-NoProfile", "-File", str(repository / "Tools/write-platform-version-catalog.ps1"),
                        "-OutputPath", str(catalog_path)], cwd=repository, check=True)
        catalog = json.loads(catalog_path.read_bytes())
    original = {path.relative_to(corpus).as_posix(): path.read_bytes()
                for path in sorted(corpus.rglob("*.json"))}
    tracked = subprocess.check_output(["git", "ls-files", "-z", "--", "test/fixtures/evidence/acceptance"], cwd=repository).decode().split("\0")
    for name in tracked:
        if name and not (repository / name).is_file():
            raise ValueError(f"Missing tracked synthetic corpus file: {name}")
    updated = prepare(original, catalog)
    changed = [name for name in original if original[name] != updated[name]]
    for name in changed:
        if args.write:
            (corpus / name).write_bytes(updated[name])
        print(("Updated " if args.write else "Stale ") + name)
    print(json.dumps({"synthetic": True, "pins": {name: catalog[name] for name in PIN_NAMES},
                      "changedFiles": len(changed), "write": args.write}, sort_keys=True))
    return 0 if args.write or not changed else 1


if __name__ == "__main__":
    raise SystemExit(main())

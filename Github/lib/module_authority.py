#!/usr/bin/env python3
"""Authenticate an accepted managed authority using the existing fixed transport.

This file and its perimeter dependencies are embedded in the producer assembly.
The managed CLI executes those package-owned bytes, never module-authored Python.
Candidate qualification has a separate entry point and cannot pass this verifier.
"""

from __future__ import annotations

import argparse
import hashlib
import json
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_config import ConfigError, _read_json
import expected_state


def digest_document(value: dict) -> str:
    """Hash a checked structured authority document deterministically."""
    return hashlib.sha256(json.dumps(value, sort_keys=True, separators=(",", ":"), ensure_ascii=True).encode()).hexdigest()


def verify(authority: dict, *, transport=None) -> None:
    """Require current accepted delivered state; a receipt alone grants nothing."""
    if (not isinstance(authority, dict) or authority.get("schema") != "hexalith.managed-build.v1"
            or authority.get("kind") != "accepted-delivery" or authority.get("qualification") is not None):
        raise ConfigError("production preparation accepts only accepted-delivery authority")
    comparison = expected_state.compare_repository(expected_state.Git(authority["moduleRoot"]),
        repository=authority["repository"], ref=authority["moduleHead"], delivered_sha=authority["deliveredSha"], transport=transport)
    if comparison is None:
        raise ConfigError("managed delivery authority is unavailable for this repository")
    comparison.require_clean()
    configuration = comparison.actual.read(".github/hexalith-ci.json")
    if (comparison.delivered_sha != authority["deliveredSha"]
            or hashlib.sha256(configuration).hexdigest() != authority["configurationDigest"]
            or digest_document(comparison.document) != authority["perimeterDigest"]):
        raise ConfigError("accepted delivery revision, configuration or perimeter changed")


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--authority", required=True, type=Path)
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise ConfigError("managed authority verification requires Python 3.12 or later")
        verify(_read_json(args.authority))
        return 0
    except (ConfigError, expected_state.ExpectedStateError, OSError, ValueError, KeyError) as error:
        print(ConfigError(str(error)), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())

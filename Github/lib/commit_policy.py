#!/usr/bin/env python3
"""The guard's raw-message GitHub workflow skip policy (stdlib only)."""

import re
import sys


SKIP_MARKER = re.compile(r"\[(?:skip[ \t]+ci|ci[ \t]+skip|no[ \t]+ci|skip[ \t]+actions|actions[ \t]+skip)\]", re.IGNORECASE)
SKIP_TRAILER = re.compile(r"^[ \t]*skip-checks[ \t]*:", re.IGNORECASE | re.MULTILINE)


def allows(message: str) -> bool:
    """Check the complete raw message, including merge text."""
    return not (SKIP_MARKER.search(message) or SKIP_TRAILER.search(message))


if __name__ == "__main__":
    print("true" if allows(sys.stdin.read()) else "false")

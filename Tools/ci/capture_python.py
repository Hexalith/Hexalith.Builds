#!/usr/bin/env python3
"""Preserve the runner interpreter before setup-python changes PATH."""

import os
from pathlib import Path
import sys


if __name__ == "__main__":
    interpreter = Path(sys.executable).resolve()
    with Path(os.environ["GITHUB_ENV"]).open("a", encoding="utf-8") as output:
        output.write(f"RUNNER_PYTHON={interpreter}\n")
    print(f"Runner Python: {interpreter} ({sys.version.split()[0]})")

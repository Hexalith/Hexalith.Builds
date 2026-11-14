#!/usr/bin/env python3
"""Check a committed module with the shared CI guard, build and unit stages."""

from pathlib import Path
import sys


# The command must not leave untracked bytecode in its Builds checkout.
sys.dont_write_bytecode = True
sys.path.insert(0, str(Path(__file__).resolve().parent / "Github/lib"))
from local_check import main


if __name__ == "__main__":
    raise SystemExit(main())

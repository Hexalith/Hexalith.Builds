#!/usr/bin/env python3
"""Prepare Linux custom jobs through the single shared loader and build APIs."""

from __future__ import annotations

import argparse
import os
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "lib"))
from ci_config import ConfigError
from ci_pipeline import (PipelineError, classified_projects, initialize_submodules,
                         prepare_config, restore_and_build, write_outputs, write_stage_outputs)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--phase", choices=("prepare", "build"), required=True)
    args = parser.parse_args()
    try:
        if sys.version_info < (3, 12):
            raise PipelineError("Python 3.12 or later is required")
        repository = os.environ.get("CI_REPOSITORY", "")
        module_root = os.environ.get("CI_MODULE_ROOT", "")
        prepared = prepare_config(Path(os.environ.get("GITHUB_WORKSPACE", ".")),
                                  repository=repository, module_root=module_root)
        if args.phase == "prepare":
            initialize_submodules(prepared)
            write_outputs(prepared)
        else:
            restore_and_build(prepared)
            classified = classified_projects(prepared, repository=repository, module_root=module_root)
            write_stage_outputs(prepared, classified)
        return 0
    except ConfigError as error:
        print(error, file=sys.stderr)
        return getattr(error, "returncode", 1)


if __name__ == "__main__":
    raise SystemExit(main())

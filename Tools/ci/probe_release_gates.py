#!/usr/bin/env python3
"""Temporary Builds-only hosted observation; removed after evidence is retained."""
from __future__ import annotations
import argparse
import json
import os
from pathlib import Path
import subprocess
import sys


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--hook', action='store_true', required=True)
    parser.parse_args()
    if os.environ.get('GITHUB_REPOSITORY') != 'Hexalith/Hexalith.Builds':
        raise ValueError('release gate probe is Builds-only')
    root = Path(os.environ['GITHUB_WORKSPACE'])
    actual = subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip()
    expected = os.environ['GITHUB_SHA']
    if actual != expected:
        raise ValueError('nightly hook did not observe the dispatch source')
    libraries = list((root / 'Samples/Hexalith.Sample/src').glob('**/bin/Release/net*/Hexalith.Sample*.dll'))
    if not libraries:
        raise ValueError('nightly hook did not receive a Release-built sample')
    event = json.loads(Path(os.environ['GITHUB_EVENT_PATH']).read_text())
    print(json.dumps({'probe': 'nightly', 'sha': actual, 'release_dlls': len(libraries)}, sort_keys=True), flush=True)
    if str(event.get('inputs', {}).get('fail-nightly', 'false')).lower() == 'true':
        print('intentional nightly probe failure after successful preparation', file=sys.stderr)
        return 23
    return 0


if __name__ == '__main__':
    raise SystemExit(main())

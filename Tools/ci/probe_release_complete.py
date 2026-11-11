#!/usr/bin/env python3
"""Observe a complete-mode reusable handoff without invoking any publisher."""
import json
import os
from pathlib import Path

EXPECTED = {
    'sha': '75e1d4a3809b30684e67c7e9b2c8043720b49c53',
    'mode': 'complete',
    'publish': 'true',
    'version': '4.47.1',
    'last-tag': 'v4.47.0',
    'draft-id': '441591623',
    'artifact-id': '13850760915',
    'artifact-digest': 'sha256:18db4ed7b07ffa3c5fd4496afce65dd50888705687d42695260e7573a7456d30',
    'artifact-run': '43010609603',
}
actual = json.loads(os.environ['RELEASE_HANDOFF'])
observed = {key: actual.get(key, '') for key in EXPECTED}
record = {'expected': EXPECTED, 'actual': observed,
          'release_result': os.environ['RELEASE_RESULT'],
          'dispatch_revision': os.environ['GITHUB_SHA'],
          'run_id': os.environ['GITHUB_RUN_ID'], 'publisher_present': False}
path = Path(os.environ['RUNNER_TEMP']) / 'complete-handoff.json'
path.write_text(json.dumps(record, indent=2) + '\n')
if record['release_result'] != 'success' or observed != EXPECTED:
    raise SystemExit('complete-mode original handoff did not match the retained identities')
print('Authenticated original handoff matched; probe has no publisher job.')

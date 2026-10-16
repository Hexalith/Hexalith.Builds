#!/usr/bin/env python3
"""Retain exact argv, UTC interval, exit code and output hash for a local check."""
import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess

p = argparse.ArgumentParser(description=__doc__)
p.add_argument('--name', required=True)
p.add_argument('--cwd', type=Path, default=Path.cwd())
p.add_argument('command', nargs=argparse.REMAINDER)
a = p.parse_args()
command = a.command[1:] if a.command[0] == '--' else a.command
root = Path(__file__).resolve().parent
log = root / (a.name + '.log')
started = datetime.now(timezone.utc).isoformat()
with log.open('xb') as output:
    result = subprocess.run(command, cwd=a.cwd, stdout=output, stderr=subprocess.STDOUT)
record = {'command': command, 'cwd': str(a.cwd.resolve()), 'startedAtUtc': started,
          'finishedAtUtc': datetime.now(timezone.utc).isoformat(), 'exitCode': result.returncode,
          'log': str(log.relative_to(root)), 'sha256': hashlib.sha256(log.read_bytes()).hexdigest()}
(root / (a.name + '.command.json')).write_text(json.dumps(record, indent=2)+'\n')
print(json.dumps(record))
raise SystemExit(result.returncode)

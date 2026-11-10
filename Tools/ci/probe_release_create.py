#!/usr/bin/env python3
"""Collect and clean only the identified disposable story 3-3 hosted probe."""
import argparse
import hashlib
import io
import json
from pathlib import Path
import re
import subprocess
import zipfile

REPOSITORY = 'Hexalith/Hexalith.Builds'
ROOT = '/repos/' + REPOSITORY

def api(endpoint, *, method='GET', binary=False):
    result = subprocess.run(['gh', 'api', '--method', method, ROOT + endpoint],
                            capture_output=True, check=True)
    return result.stdout if binary else (json.loads(result.stdout) if result.stdout else None)

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('run', type=int)
    parser.add_argument('--directory', type=Path, required=True)
    parser.add_argument('--cleanup-draft', action='store_true')
    args = parser.parse_args()
    args.directory.mkdir(parents=True, exist_ok=True)
    run = api(f'/actions/runs/{args.run}')
    if run['path'] != '.github/workflows/release-create-probe.yml' or run['event'] != 'workflow_dispatch':
        raise ValueError('not the identified probe workflow')
    jobs = api(f'/actions/runs/{args.run}/jobs?per_page=100')['jobs']
    artifacts = api(f'/actions/runs/{args.run}/artifacts?per_page=100')['artifacts']
    report = {key: run[key] for key in ('id','run_attempt','head_sha','event','path','status','conclusion','html_url')}
    report['jobs'] = [{key: job.get(key) for key in ('name','conclusion','started_at','completed_at','steps')}
                      for job in jobs]
    report['artifacts'] = [{key: item.get(key) for key in ('id','name','digest','expired','created_at','expires_at','workflow_run')}
                           for item in artifacts]
    handoff = None
    for artifact in artifacts:
        if artifact['name'] not in ('probe-handoff','release','hexalith-release-outcome'):
            continue
        payload = api(f"/actions/artifacts/{artifact['id']}/zip", binary=True)
        digest = 'sha256:' + hashlib.sha256(payload).hexdigest()
        if digest != artifact['digest']:
            raise ValueError('downloaded artifact digest mismatch')
        (args.directory / (artifact['name'] + '.zip')).write_bytes(payload)
        with zipfile.ZipFile(io.BytesIO(payload)) as archive:
            if artifact['name'] == 'probe-handoff':
                handoff = json.loads(archive.read('handoff.json'))
                report['handoff'] = handoff
            elif artifact['name'] == 'release':
                report['manifest'] = json.loads(archive.read('release-manifest.json'))
                report['release_files'] = archive.namelist()
            else:
                report['outcome'] = json.loads(archive.read('outcome.json'))
    if handoff and handoff['outputs'].get('publish') == 'true':
        outputs = handoff['outputs']
        draft_id = outputs['draft-id']
        if not re.fullmatch(r'[1-9][0-9]*', draft_id):
            raise ValueError('invalid probe draft identity')
        draft = api('/releases/' + draft_id)
        expected = {'hexalith-release': run['head_sha'], 'hexalith-release-run': str(args.run),
                    'hexalith-release-artifact': outputs['artifact-id'] + ' ' + outputs['artifact-digest'],
                    'hexalith-release-packages': ','.join(sorted(x['id'] for x in report['manifest']['packages'])),
                    'hexalith-release-containers': ','.join(sorted(x['name'] for x in report['manifest']['containers']))}
        actual = dict(re.findall(r'^(hexalith-release(?:-[a-z]+)?):[ \t]*([^\r\n]*)$', draft['body'], re.MULTILINE))
        if (actual != expected or draft['draft'] is not True or draft['author']['login'] != 'github-actions[bot]'
                or draft['target_commitish'] != run['head_sha'] or draft['tag_name'] != 'v' + outputs['version']):
            raise ValueError('probe draft does not match the exact run/artifact/inventory')
        report['draft'] = {key: draft[key] for key in ('id','draft','tag_name','target_commitish','body','created_at')}
        report['draft']['author'] = draft['author']['login']
        if args.cleanup_draft:
            api('/releases/' + draft_id, method='DELETE')
            report['identified_probe_draft_deleted'] = True
    (args.directory / 'report.json').write_text(json.dumps(report, indent=2) + '\n')
    print(json.dumps({key: report[key] for key in ('id','run_attempt','head_sha','conclusion')}))

if __name__ == '__main__':
    main()

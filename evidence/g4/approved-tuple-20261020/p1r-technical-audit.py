"""Audit completed P1R receipts through the frozen owning APIs; preserve owner gates."""
from pathlib import Path
import hashlib
import json
import shutil
import subprocess
import sys
import uuid

WORKSPACE = Path('/home/administrator/projects/hexalith/p0-approved-qualification-20261020')
VERIFIER = WORKSPACE / 'references/Hexalith.EventStore'
SOURCE = Path('/home/administrator/projects/hexalith/p0-runtime-3.126.0-20261020')
EXECUTION = VERIFIER / '.p0-qualification/p1r-31260-execution-attempt2'
OBSERVATION = WORKSPACE / '.p0-qualification/p1r-31260-observation-attempt4'
OUT = WORKSPACE / '.p0-qualification/p1r-31260-technical-audit-attempt1'
sys.path.insert(0, str(VERIFIER / 'tools'))
import p1r_qualification as p
import p1r_published_qualification as q

OUT.mkdir(parents=True, exist_ok=False)
(OUT / 'receipts').mkdir()
self_path = Path(__file__).resolve()
self_hash = p.digest(p.regular(self_path))
result = {'artifactKind': 'independent technical audit using frozen owning P1R validators',
    'started_utc': p.stamp(), 'finished_utc': None, 'invocation': uuid.uuid4().hex,
    'script': {'path': str(self_path), 'sha256': self_hash}, 'auditCompleted': False,
    'technicalEvidenceValid': False, 'qualified': False, 'p1r_usable': False,
    'ownerDispositionRequired': True, 'errors': [], 'packages': {}, 'receipts': [], 'commands': []}

def save():
    p.write_json(OUT / 'audit.json', result)

def command(label, argv):
    row = {'purpose': label, 'argv': list(map(str, argv)), 'started_utc': p.stamp()}
    completed = subprocess.run(row['argv'], cwd=OUT, capture_output=True, text=True, timeout=600)
    row.update(exit_code=completed.returncode, stdout=completed.stdout, stderr=completed.stderr,
               finished_utc=p.stamp())
    result['commands'].append(row)
    save()
    return row

save()
try:
    selection, raw, scope = q.load_document(OBSERVATION / 'owner-inputs.json', q.validate_inputs)
    p.require(scope == 'owner-selected' and selection['candidate']['version'] == '3.126.0', 'selection differs')
    result['inputs_sha256'] = p.digest(raw)
    binding = p.source_binding(VERIFIER, SOURCE)
    p.require(binding['main']['head'] == '47def45665312410b73fa12cb047c6bbaa431cf2', 'verifier revision differs')
    p.require(p.git(WORKSPACE / 'references/Hexalith.Builds', 'rev-parse', 'HEAD').decode().strip()
              == 'e40ed9c7110d56a2f38c9291627728aea2f205cb', 'workspace Builds revision differs')
    q.validate_candidate_source(binding, selection)
    execution = q.parse_json(p.regular(EXECUTION / 'execution.json'))
    source_bytes = p.regular(EXECUTION / 'executor-source.json')
    p.require(execution['finished_utc'] and execution['errors'] == []
              and execution['inputs_sha256'] == p.digest(raw), 'execution incomplete, failed or substituted')
    p.require(q.parse_json(source_bytes) == binding
              and p.digest(source_bytes) == execution['executor_source_sha256'], 'execution source closure differs')
    p.write_json(OUT / 'source-binding.json', binding)
    shutil.copyfile(SOURCE / 'global.json', OUT / 'global.json')
    context = {'inputs': {'value': selection, 'scope': scope, 'sha256': p.digest(raw)},
               'source_binding': binding, 'verified': {}}
    for role in ['candidate', *('comparison-' + version for version in q.COMPARISON_VERSIONS)]:
        p.check_current_source(binding)
        row, verified = q.execute_package_lane(OUT, result['invocation'], role,
            EXECUTION / 'consumers' / role, context['inputs'], q.default_verifier)
        result['packages'][role] = row
        p.require(verified is not None, 'package proof nonpassing: ' + role)
        q.validate_package_receipt(OUT, result['invocation'], role, row['receipts'][0],
            context['inputs'], *q.validate_times({'started_utc': result['started_utc'], 'finished_utc': p.stamp()}))
        context['verified'][role] = verified
        save()
    inventory = q.executed_case_inventory(selection)
    lane_paths, measured_lanes = [], set()
    counts = {'attempted': 0, 'passed': 0, 'failed': 0}
    case_count = 0
    for name in execution['receipts']:
        path = Path(name)
        p.require(path.parent == EXECUTION / 'receipts', 'receipt escaped original execution')
        data = p.regular(path)
        receipt = q.parse_json(data)
        row = {'path': str(path), 'sha256': p.digest(data), 'lane': receipt.get('lane', receipt.get('role')),
               'assertions': receipt['assertions'], 'validated': False}
        try:
            group, lane, outcome = q.receipt_outcome(receipt, context)
            row.update(validated=True, group=group, lane=lane, outcome=outcome)
        except Exception as error:
            row['refusal'] = str(error)
            result['errors'].append({'stage': 'receipt', 'path': str(path), 'reason': str(error)})
        result['receipts'].append(row)
        lane = receipt.get('lane')
        if lane in inventory:
            p.require(lane not in measured_lanes, 'duplicate lane')
            p.require([case['id'] for case in receipt['cases']] == list(inventory[lane]), 'case inventory differs')
            measured_lanes.add(lane)
            lane_paths.append(path)
            case_count += len(receipt['cases'])
            for key in counts:
                counts[key] += receipt['assertions'][key]
        save()
    p.require(measured_lanes == set(inventory) and case_count == sum(map(len, inventory.values())),
              'scenario/addition/case accounting incomplete')
    result.update(caseCount=case_count, laneCount=len(measured_lanes), assertions=counts,
                  allMeasuredChecksPassed=counts['failed'] == 0)
    result['process_controls'] = q.runtime.new_process_section()
    q.runtime.run_process_controls(result['process_controls'], OUT, result['invocation'], binding['python']['path'],
        lambda: p.check_current_source(binding), save)
    retained_controls, control_count = q.runtime.validate_process_controls(OUT, result['process_controls'],
        result['invocation'], binding['python']['path'],
        *q.validate_times({'started_utc': result['started_utc'], 'finished_utc': p.stamp()}))
    result['processControlAssertions'] = control_count
    result['processControlFiles'] = sorted(retained_controls)
    cli = VERIFIER / 'tools/p1r-published-qualification.py'
    table = OUT / 'successor-support-boundary.json'
    row = command('measure-boundary', ['python3', cli, 'measure-boundary', '--inputs', OBSERVATION / 'owner-inputs.json',
        '--out', table, *[arg for path in lane_paths for arg in ('--receipt', path)]])
    p.require(row['exit_code'] == 0, 'support measurement failed')
    argv = ['python3', cli, 'prepare', '--out', OUT / 'strict-packet-pending-disposition', '--source-root', SOURCE,
        '--inputs', OBSERVATION / 'owner-inputs.json', '--decisions', OBSERVATION / 'owner-decisions.json',
        '--support-boundary', table, '--candidate-evidence', EXECUTION / 'consumers/candidate', '--process-controls']
    for version in q.COMPARISON_VERSIONS:
        argv += ['--comparison-evidence', EXECUTION / ('consumers/comparison-' + version)]
    for path in execution['receipts']:
        argv += ['--receipt', path]
    row = command('strict-prepare-pending-owner-disposition', argv)
    p.require(row['exit_code'] == 2 and 'successor support boundary lacks measured owner disposition'
              in row['stderr'] + row['stdout'], 'unexpected strict packet disposition')
    p.check_current_source(binding)
    p.require(p.digest(p.regular(self_path)) == self_hash, 'audit procedure changed')
    result.update(auditCompleted=True, technicalEvidenceValid=not result['errors'], sourceCheckedAfterAllCommands=True)
except Exception as error:
    result['errors'].append({'stage': 'audit', 'type': type(error).__name__, 'reason': str(error)})
finally:
    result['finished_utc'] = p.stamp()
    save()
print(json.dumps({key: result.get(key) for key in ['auditCompleted', 'technicalEvidenceValid', 'laneCount', 'caseCount',
    'assertions', 'allMeasuredChecksPassed', 'qualified', 'p1r_usable', 'ownerDispositionRequired', 'errors']}), flush=True)
raise SystemExit(0 if result['technicalEvidenceValid'] else 2)

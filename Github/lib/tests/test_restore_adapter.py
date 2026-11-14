"""Execute restore job gates, native env mappings and the real composite."""
from __future__ import annotations

import json
import os
from pathlib import Path
import subprocess
import sys
import unittest

import yaml

from actions_expressions import Evaluator, run_composite
import test_restore as fixtures

REAL_GIT = fixtures.REAL_GIT

ROOT = Path(__file__).resolve().parents[3]


class RestoreAdapterTests(unittest.TestCase):
    def setUp(self):
        self.fixture = fixtures.RestoreTests()
        self.fixture.setUp()
        self.addCleanup(self.fixture.doCleanups)
        self.root = self.fixture.root
        self.bin = self.root / 'adapter-bin'
        self.bin.mkdir()
        self.ledger = self.root / 'git-operations.jsonl'
        self.dispatches = self.root / 'dispatches.jsonl'
        self.fixture.module.write('AGENTS.md', 'restore this independently\n')
        self.parent = self.fixture.module.commit()
        self.context = {
            'github': {'repository': 'Example/Fixture', 'event_name': 'push', 'ref': 'refs/heads/main',
                       'token': 'secret-marker'},
            'inputs': {'module-root': '', 'expected-source': ''},
            'needs': {'ci': {'result': 'failure', 'outputs': {'drift': 'true',
                'expected-sha': 'untrusted', 'module-root': '/untrusted', 'tag': 'v99.0.0',
                'repair-plan': 'untrusted'}}},
            'job': {'workflow_ref': self.fixture.context.workflow_ref,
                    'workflow_sha': self.fixture.builds_sha,
                    'workflow_repository': 'Hexalith/Hexalith.Builds'},
        }
        self.status = {'success': False, 'failure': True, 'cancelled': False}
        git = self.bin / 'git'
        git.write_text(f'#!{sys.executable}\n' + f'''
import base64, json, os, subprocess, sys
args=sys.argv[1:]
for key in ('GH_TOKEN','GITHUB_TOKEN','RESTORE_TOKEN','PYTHONPATH','PYTHONHOME','GIT_CONFIG_PARAMETERS'):
    assert key not in os.environ, key
assert not any('secret-marker' in arg for arg in args)
operation=next((arg for arg in args if arg in ('init','clone','fetch','push','commit-tree')), '')
credential='GIT_CONFIG_COUNT' in os.environ
if credential:
    assert operation=='push', args
    assert os.environ['GIT_CONFIG_COUNT']=='1'
    assert os.environ['GIT_CONFIG_KEY_0']=='http.https://github.com/.extraheader'
    expected='AUTHORIZATION: basic '+base64.b64encode(b'x-access-token:secret-marker').decode()
    assert os.environ['GIT_CONFIG_VALUE_0']==expected
with open({str(self.ledger)!r}, 'a') as stream:
    stream.write(json.dumps({{'args':args,'credential':credential}})+'\\n')
remotes={{'https://github.com/Example/Fixture.git':{self.fixture.module.root.as_uri()!r},
    'https://github.com/Hexalith/Hexalith.CiCd.git':{self.fixture.delivered.root.as_uri()!r},
    'https://github.com/Hexalith/Hexalith.Builds.git':{self.fixture.builds.root.as_uri()!r}}}
args=[remotes.get(arg,arg).replace('protocol.file.allow=never','protocol.file.allow=always') for arg in args]
raise SystemExit(subprocess.call([{REAL_GIT!r},*args]))
''')
        git.chmod(0o755)
        python = self.bin / 'python3'
        python.write_text(f'#!{sys.executable}\n' + f'''
import importlib.util, json, pathlib, sys
assert sys.argv[1]=='-I'
source=pathlib.Path(sys.argv[2]).resolve()
assert source==pathlib.Path({str(ROOT / 'Github/lib/restore.py')!r})
spec=importlib.util.spec_from_file_location('controlled_restore',source)
module=importlib.util.module_from_spec(spec)
sys.modules[spec.name]=module
spec.loader.exec_module(module)
class Dispatch:
    def send(self,repository,token):
        assert token=='secret-marker'
        with open({str(self.dispatches)!r},'a') as stream:
            stream.write(json.dumps({{'repository':repository,'ref':'main'}})+'\\n')
module.Dispatch=Dispatch
raise SystemExit(module.main())
''')
        python.chmod(0o755)
        self.environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'],
            GITHUB_TOKEN='secret-marker', GH_TOKEN='secret-marker', PYTHONPATH='/poison',
            GIT_CONFIG_COUNT='1', GIT_CONFIG_KEY_0='url.ext::poison.insteadOf',
            GIT_CONFIG_VALUE_0='https://github.com/', GIT_CONFIG_PARAMETERS='poison')
        self.workflow = yaml.safe_load((ROOT / '.github/workflows/ci.yml').read_text())

    def operations(self):
        return [json.loads(line) for line in self.ledger.read_text().splitlines()] if self.ledger.exists() else []

    def invoke(self):
        job = self.workflow['jobs']['restore']
        evaluator = Evaluator(self.context, status=self.status)
        if not evaluator.condition(job['if']):
            return False, []
        steps = job['steps']
        # External actions in the writer would execute here and fail loudly.
        self.assertEqual(1, len(steps))
        step = steps[0]
        self.assertEqual('$/Github/restore', step['uses'])
        environment = self.environment | {name: evaluator.template(value) for name, value in step['env'].items()}
        outputs, processes = run_composite(ROOT / step['uses'][2:], {}, environment,
            cwd=self.root, runner_temp=self.root)
        return True, processes

    def test_failed_ci_runs_real_writer_from_native_identity_with_only_push_authentication(self):
        enabled, processes = self.invoke()
        self.assertTrue(enabled)
        self.assertEqual(1, len(processes))
        self.assertEqual(0, processes[0].returncode, processes[0].stdout + processes[0].stderr)
        current = self.fixture.module.command('rev-parse', 'main')
        self.assertNotEqual(self.parent, current)
        self.assertEqual(self.parent, self.fixture.module.command('show', '-s', '--format=%P', 'main'))
        operations = self.operations()
        authenticated = [record for record in operations if record['credential']]
        self.assertEqual(1, len(authenticated))
        self.assertIn('push', authenticated[0]['args'])
        self.assertEqual(1, sum('commit-tree' in record['args'] for record in operations))
        self.assertEqual([{'repository':'Example/Fixture', 'ref':'main'}],
                         [json.loads(line) for line in self.dispatches.read_text().splitlines()])
        self.assertNotIn('secret-marker', processes[0].stdout + processes[0].stderr)
        # A successful restore cannot turn the original failed job green.
        self.assertEqual('failure', self.context['needs']['ci']['result'])

    def test_job_conditions_skip_every_forbidden_invocation_after_failure(self):
        cases = [('github', 'event_name', 'pull_request'), ('github', 'event_name', 'workflow_dispatch'),
                 ('github', 'ref', 'refs/heads/topic'), ('github', 'repository', 'Hexalith/Hexalith.Builds'),
                 ('inputs', 'module-root', 'Samples/Hexalith.Sample')]
        for section, key, value in cases:
            old = self.context[section][key]
            self.context[section][key] = value
            with self.subTest(section=section, key=key, value=value):
                enabled, processes = self.invoke()
                self.assertFalse(enabled)
                self.assertEqual([], processes)
            self.context[section][key] = old
        for flag in ('', 'false', None):
            self.context['needs']['ci']['outputs']['drift'] = flag
            self.assertFalse(self.invoke()[0])
        self.context['needs']['ci']['outputs']['drift'] = 'true'
        self.status['cancelled'] = True
        self.assertFalse(self.invoke()[0])
        self.assertEqual([], self.operations())
        self.assertFalse(self.dispatches.exists())
        self.assertEqual(self.parent, self.fixture.module.command('rev-parse', 'main'))

    def test_forged_native_source_identity_fails_inside_real_composite_before_network(self):
        self.context['job']['workflow_repository'] = 'Example/Untrusted'
        enabled, processes = self.invoke()
        self.assertTrue(enabled)
        self.assertNotEqual(0, processes[0].returncode)
        self.assertIn('workflow identity', processes[0].stderr)
        self.assertEqual([], self.operations())
        self.assertFalse(self.dispatches.exists())

    def test_permission_union_keeps_ci_hooks_and_windows_read_only(self):
        caller = yaml.safe_load((ROOT / '.github/workflows/builds-ci.yml').read_text())
        required = self.workflow['jobs']['restore']['permissions']
        self.assertEqual({'contents':'write','actions':'write'}, required)
        self.assertEqual({'contents':'read'}, self.workflow['jobs']['ci']['permissions'])
        for name in ('dotnet', 'sample'):
            permissions = caller['jobs'][name]['permissions']
            self.assertTrue(all(permissions.get(scope)==access for scope,access in required.items()))
        self.assertEqual({'contents':'read'}, caller['jobs']['sample-windows']['permissions'])
        # Execute Builds root and sample gates with failed CI to prove the union
        # never enables either self-restore path.
        self.context['github']['repository'] = 'Hexalith/Hexalith.Builds'
        self.assertFalse(self.invoke()[0])
        self.context['inputs']['module-root'] = 'Samples/Hexalith.Sample'
        self.assertFalse(self.invoke()[0])


if __name__ == '__main__':
    unittest.main()

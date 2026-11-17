"""Effect-free transport controls for the exact maintenance publication guard."""
from copy import deepcopy
import importlib.util
import io
import json
import os
from pathlib import Path
import tempfile
import types
import unittest
from unittest.mock import patch
from contextlib import redirect_stderr

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('maintenance_guard', HERE / 'verify-maintenance-release-source.py')
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)
SOURCE = 'a' * 40
REPO = guard.REPOSITORY
REF = 'refs/heads/4.44.x'
ENV = {'REPOSITORY': REPO, 'DISPATCH_SHA': SOURCE, 'DISPATCH_REF': REF,
       'GITHUB_EVENT_NAME': 'workflow_dispatch', 'GITHUB_WORKFLOW_REF': f'{REPO}/.github/workflows/build-release.yml@{REF}',
       'GITHUB_WORKFLOW_SHA': SOURCE, 'GITHUB_SHA': SOURCE}
RUN = {'id': 123, 'run_number': 17, 'run_attempt': 2, 'workflow_id': 89, 'name': 'CI',
       'path': guard.WORKFLOW, 'repository': {'full_name': REPO}, 'head_repository': {'full_name': REPO},
       'head_sha': SOURCE, 'head_branch': '4.44.x', 'event': 'push', 'pull_requests': [],
       'status': 'completed', 'conclusion': 'success'}
LIST = f'repos/{REPO}/actions/workflows/ci.yml/runs?head_sha={SOURCE}&event=push&branch=4.44.x&per_page=100'
RUN_PATH = f'repos/{REPO}/actions/runs/123'
HEAD = f'repos/{REPO}/git/ref/heads/4.44.x'
REG = f'repos/{REPO}/actions/workflows/ci.yml'
JOBS = RUN_PATH + '/attempts/2/jobs?per_page=100'


def fixtures():
    names = ['Build and test', '.NET tests', 'Python contract tests', 'Package catalog contracts',
             'Build and package contracts', 'CodeQL / analyze', 'Dependency review']
    jobs = [{'id': index + 1, 'name': name, 'run_id': 123, 'head_sha': SOURCE, 'status': 'completed',
             'conclusion': 'skipped' if name.startswith('Dependency review') else 'success'} for index, name in enumerate(names)]
    return {HEAD: {'ref': REF, 'url': f'https://api.github.com/repos/{REPO}/git/{REF}',
                   'object': {'type': 'commit', 'sha': SOURCE, 'url': f'https://api.github.com/repos/{REPO}/git/commits/{SOURCE}'}},
            REG: {'id': 89, 'path': guard.WORKFLOW, 'name': 'CI', 'state': 'active'},
            LIST: {'total_count': 1, 'workflow_runs': [deepcopy(RUN)]}, RUN_PATH: deepcopy(RUN),
            JOBS: {'total_count': 7, 'jobs': jobs}}


class MaintenanceSourceTests(unittest.TestCase):
    def execute(self, data=None, environment=None, ancestor=True, race=None):
        data = fixtures() if data is None else data
        counts = {}
        def api(path):
            counts[path] = counts.get(path, 0) + 1
            value = deepcopy(data[path])
            if race:
                race(path, counts[path], value)
            return value
        receipt = {}
        result = guard.verify(api, lambda base, source: ancestor and base == guard.BASE and source == SOURCE,
                              ENV | (environment or {}), receipt)
        return result, receipt

    def test_exact_normal_maintenance_source_and_attempt_are_retained(self):
        result, receipt = self.execute(environment={'EXPECTED_CI_RUN_ID': '123', 'EXPECTED_CI_RUN_ATTEMPT': '2'})
        self.assertEqual(result, (123, 2))
        self.assertEqual(receipt['status'], 'verified')
        self.assertEqual(len(receipt['jobs']), 7)

    def test_foreign_missing_or_changed_executing_context_refuses(self):
        for name, value in [('REPOSITORY', 'Other/Repo'), ('DISPATCH_REF', 'refs/heads/main'),
                            ('DISPATCH_SHA', 'A' * 40), ('GITHUB_EVENT_NAME', 'push'),
                            ('GITHUB_WORKFLOW_REF', f'{REPO}/.github/workflows/build-release.yml@refs/heads/main'),
                            ('GITHUB_WORKFLOW_SHA', 'b' * 40), ('GITHUB_SHA', 'b' * 40)]:
            with self.subTest(name=name), self.assertRaises(guard.Refusal):
                self.execute(environment={name: value})

    def test_unrelated_source_ancestry_refuses(self):
        with self.assertRaises(guard.Refusal):
            self.execute(ancestor=False)

    def test_exact_live_ref_and_workflow_are_required(self):
        for path, key, value in [(HEAD, 'ref', 'refs/heads/other'), (HEAD, 'url', 'https://api.github.com/repos/Other/Repo/git/' + REF),
                                 (REG, 'path', '.github/workflows/other.yml'), (REG, 'state', 'disabled_manually'), (REG, 'id', 0), (REG, 'name', 'Other CI')]:
            data = fixtures(); data[path][key] = value
            with self.subTest(path=path, key=key), self.assertRaises(guard.Refusal):
                self.execute(data)

    def test_latest_failed_or_running_attempt_never_falls_back_to_older_success(self):
        for conclusion, status in [('failure', 'completed'), (None, 'in_progress')]:
            data = fixtures(); newer = deepcopy(RUN); newer.update(id=124, run_number=18, conclusion=conclusion, status=status)
            data[LIST] = {'total_count': 2, 'workflow_runs': [deepcopy(RUN), newer]}
            data[f'repos/{REPO}/actions/runs/124'] = newer
            with self.subTest(status=status), self.assertRaises(guard.Refusal):
                self.execute(data)

    def test_missing_truncated_or_ambiguous_run_discovery_refuses(self):
        for value in [{'total_count': 0, 'workflow_runs': []}, {'total_count': 2, 'workflow_runs': [RUN]},
                      {'total_count': 101, 'workflow_runs': [RUN]}, {'total_count': 2, 'workflow_runs': [RUN, RUN]}]:
            data = fixtures(); data[LIST] = deepcopy(value)
            with self.subTest(value=value), self.assertRaises(guard.Refusal):
                self.execute(data)

    def test_foreign_or_incomplete_run_identity_refuses(self):
        for key, value in [('name', 'Other workflow'), ('workflow_id', 7), ('path', '.github/workflows/other.yml'),
                           ('head_branch', 'main'), ('head_sha', 'b' * 40), ('run_attempt', 0),
                           ('event', 'pull_request'), ('pull_requests', [{}]), ('repository', {'full_name': 'Other/Repo'})]:
            data = fixtures(); data[RUN_PATH][key] = value
            with self.subTest(key=key), self.assertRaises(guard.Refusal):
                self.execute(data)

    def test_every_product_or_security_job_is_required(self):
        for missing in range(7):
            data = fixtures(); del data[JOBS]['jobs'][missing]; data[JOBS]['total_count'] -= 1
            with self.subTest(missing=missing), self.assertRaises(guard.Refusal):
                self.execute(data)

    def test_failed_skipped_or_foreign_product_job_refuses(self):
        for key, value in [('conclusion', 'failure'), ('conclusion', 'skipped'), ('run_id', 99),
                           ('head_sha', 'b' * 40), ('status', 'in_progress')]:
            data = fixtures(); data[JOBS]['jobs'][0][key] = value
            with self.subTest(key=key, value=value), self.assertRaises(guard.Refusal):
                self.execute(data)

    def test_expected_attempt_cannot_be_missing_or_replaced(self):
        for environment in [{'EXPECTED_CI_RUN_ID': '123'}, {'EXPECTED_CI_RUN_ID': '122', 'EXPECTED_CI_RUN_ATTEMPT': '2'},
                            {'EXPECTED_CI_RUN_ID': '123', 'EXPECTED_CI_RUN_ATTEMPT': '1'}]:
            with self.subTest(environment=environment), self.assertRaises(guard.Refusal):
                self.execute(environment=environment)

    def test_late_head_attempt_or_run_discovery_change_refuses(self):
        mutations = [(HEAD, lambda value: value['object'].update(sha='b' * 40)),
                     (RUN_PATH, lambda value: value.update(run_attempt=3)),
                     (LIST, lambda value: value.update(total_count=2))]
        for target, mutate in mutations:
            def race(path, count, value):
                if path == target and count == 2: mutate(value)
            with self.subTest(target=target), self.assertRaises(guard.Refusal):
                self.execute(race=race)

    def test_real_cli_boundary_writes_output_and_immutable_receipt(self):
        data = fixtures()
        def command(arguments, **kwargs):
            if arguments[:2] == ['gh', 'api']:
                return types.SimpleNamespace(returncode=0, stdout=json.dumps(data[arguments[2]]))
            self.assertEqual(arguments, ['git', 'merge-base', '--is-ancestor', guard.BASE, SOURCE])
            return types.SimpleNamespace(returncode=0, stdout='')
        with tempfile.TemporaryDirectory() as temporary:
            output = Path(temporary) / 'outputs'
            with patch.dict(os.environ, ENV | {'RUNNER_TEMP': temporary, 'GITHUB_OUTPUT': str(output)}, clear=True), \
                 patch.object(guard.subprocess, 'run', side_effect=command), redirect_stderr(io.StringIO()):
                self.assertEqual(guard.main(), 0)
            self.assertEqual(output.read_text(), 'ci-run-id=123\nci-run-attempt=2\n')
            receipts = list(Path(temporary).glob('g4-maintenance-source-*.json'))
            self.assertEqual(len(receipts), 1)
            self.assertEqual(json.loads(receipts[0].read_text())['ciRunAttempt'], 2)


if __name__ == '__main__':
    unittest.main(verbosity=2)

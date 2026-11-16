"""Own the exact local CLI path and clean only authenticated private resources."""
from __future__ import annotations
import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import ci_dapr_scope as SCOPE


class DaprScopeTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="dapr-scope-tests-")
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.state = {}
        self.volumes = {}
        self.calls = []
        self.fail_init = False
        self.fail_volume_create = False
        self.root.joinpath('docker').write_text('')
        self.root.joinpath('docker').chmod(0o755)
        self.owner = None

    def docker(self, arguments, root):
        self.calls.append(arguments)
        args = arguments[1:]
        if args == ['ps', '-aq']:
            return '\n'.join(self.state)
        if args[0] == 'inspect':
            return json.dumps([self.state[key] for key in args[1:]])
        if args[:2] == ['volume', 'create']:
            self.owner = args[3].split('=', 1)[1]
            self.volumes[args[-1]] = {'Name': args[-1], 'Labels': {SCOPE.LABEL: self.owner}}
            if self.fail_volume_create:
                raise SCOPE.PipelineError('controlled create-then-client-failure')
            return args[-1]
        if args[:2] == ['volume', 'ls']:
            return '\n'.join(self.volumes)
        if args[:2] == ['volume', 'inspect']:
            return json.dumps([self.volumes[args[-1]]])
        if args[:2] == ['volume', 'rm']:
            del self.volumes[args[-1]]
            return args[-1]
        if args[:2] == ['rm', '-f']:
            del self.state[args[-1]]
            return args[-1]
        if args[0] == '--version':
            return 'CLI version: 1.19.1\nRuntime version: 1.19.1'
        if 'init' in args:
            self.assertIn('--runtime-path', args)
            runtime = Path(args[args.index('--runtime-path') + 1])
            self.assertTrue(runtime.is_relative_to(self.root))
            self.assertIn('--scheduler-volume', args)
            for index, name in enumerate(SCOPE.NAMES):
                if self.fail_init and index == 2:
                    raise SCOPE.PipelineError('controlled partial init failure')
                key = str(index) * 64
                self.state[key] = {'Id': key, 'Name': '/' + name, 'Config': {'Labels': {SCOPE.LABEL: self.owner}}}
            return ''
        raise AssertionError(arguments)

    def scope(self):
        return SCOPE.isolated_dapr(self.root, self.root)

    def controls(self):
        return (mock.patch.object(SCOPE.shutil, 'which', return_value=str(self.root / 'docker')),
                mock.patch.object(SCOPE, 'command', side_effect=self.docker),
                mock.patch.object(SCOPE, 'available_ports'),
                mock.patch.object(SCOPE, 'download_dapr_cli', return_value=(self.root / 'verified-cli', '1.19.1')))

    def test_exact_cli_private_runtime_and_path_lifetime(self):
        before = dict(os.environ)
        self.state['foreign'] = {'Id': 'foreign', 'Name': '/unrelated-product', 'Config': {'Labels': {}}}
        with self.controls()[0], self.controls()[1], self.controls()[2], self.controls()[3]:
            with self.scope() as result:
                self.assertTrue(Path(os.environ['PATH'].split(os.pathsep)[0]).is_relative_to(self.root))
                self.assertEqual(str(self.root / 'verified-cli'), result['cli'])
                self.assertEqual(4, len(result['containers']))
                shim = Path(os.environ['PATH'].split(os.pathsep)[0], 'dapr').read_text()
                self.assertIn(repr(str(self.root / 'verified-cli')), shim)
                self.assertIn('--runtime-path', shim)
                self.assertEqual(before.get('HOME'), os.environ.get('HOME'))
                self.state['concurrent'] = {'Id': 'concurrent', 'Name': '/appeared-after-preflight', 'Config': {'Labels': None}}
        self.assertEqual(before, dict(os.environ))
        self.assertEqual({'foreign', 'concurrent'}, set(self.state))
        self.assertFalse(self.volumes)
        self.assertFalse(list(self.root.glob('dapr-qualification-*')))

    def test_partial_initialization_and_product_failure_clean_owned_ids(self):
        for fail_init in (True, False):
            self.fail_init = fail_init
            with self.controls()[0], self.controls()[1], self.controls()[2], self.controls()[3]:
                with self.assertRaisesRegex((RuntimeError, SCOPE.PipelineError), 'controlled'):
                    with self.scope():
                        raise RuntimeError('controlled product failure')
            self.assertFalse(self.state)
            self.assertFalse(self.volumes)
            self.assertFalse(list(self.root.glob('dapr-qualification-*')))

    def test_existing_user_control_plane_refuses_before_install_or_mutation(self):
        self.state['user'] = {'Id': 'user', 'Name': '/dapr_placement', 'Config': {'Labels': {}}}
        with self.controls()[0], self.controls()[1], self.controls()[2] as ports, self.controls()[3] as install:
            with self.assertRaisesRegex(SCOPE.PipelineError, 'existing user'):
                with self.scope():
                    self.fail('must refuse')
            ports.assert_not_called()
            install.assert_not_called()
        self.assertEqual(['user'], list(self.state))
        self.assertFalse(any(args[1] in ('rm', 'volume') for args in self.calls))

    def test_created_volume_is_cleaned_after_client_failure(self):
        self.fail_volume_create = True
        with self.controls()[0], self.controls()[1], self.controls()[2], self.controls()[3] as install:
            with self.assertRaisesRegex(SCOPE.PipelineError, 'create-then-client-failure'):
                with self.scope():
                    self.fail('must refuse')
            install.assert_not_called()
        self.assertFalse(self.volumes)
        self.assertFalse(self.state)
        self.assertFalse(list(self.root.glob('dapr-qualification-*')))

    def test_replaced_volume_refuses_deletion_and_reports_cleanup_failure(self):
        with self.controls()[0], self.controls()[1], self.controls()[2], self.controls()[3]:
            with self.assertRaisesRegex(SCOPE.PipelineError, 'volume ownership changed'):
                with self.scope() as result:
                    self.volumes[result['schedulerVolume']]['Labels'] = {}
        self.assertEqual(1, len(self.volumes))
        self.assertFalse(self.state)

    def test_child_shim_executes_exact_binary_and_forbids_runtime_replacement(self):
        target = self.root / 'verified'
        target.write_text(f'#!{sys.executable}\nimport json,sys\nprint(json.dumps(sys.argv))\n')
        target.chmod(0o755)
        SCOPE.write_shims(self.root / 'bin', str(self.root / 'docker'), target, self.root / 'runtime', 'owned')
        result = subprocess.run([str(self.root / 'bin/dapr'), 'run', '--app-id', 'test'], check=True, text=True, capture_output=True)
        self.assertEqual([str(target), '--runtime-path', str(self.root / 'runtime'), 'run', '--app-id', 'test'], json.loads(result.stdout))
        for args in (['--runtime-path', '/foreign'], ['--runtime-path=/foreign'], ['init'], ['uninstall']):
            result = subprocess.run([str(self.root / 'bin/dapr'), *args], text=True, capture_output=True)
            self.assertNotEqual(0, result.returncode)

    def test_docker_shim_labels_only_its_control_plane_and_rejects_foreign_start(self):
        docker = self.root / 'docker-real'
        docker.write_text(f'#!{sys.executable}\nimport json,sys\nprint(json.dumps(sys.argv[1:]))\n')
        docker.chmod(0o755)
        SCOPE.write_shims(self.root / 'bin', str(docker), self.root / 'verified', self.root / 'runtime', 'owned')
        result = subprocess.run([str(self.root / 'bin/docker'), 'run', '--name', 'dapr_scheduler', 'image'], check=True, text=True, capture_output=True)
        args = json.loads(result.stdout)
        self.assertEqual(['run', '--label', SCOPE.LABEL + '=owned', '--name', 'dapr_scheduler', 'image'], args)
        result = subprocess.run([str(self.root / 'bin/docker'), 'run', '--name', 'unrelated', 'image'], check=True, text=True, capture_output=True)
        self.assertEqual(['run', '--name', 'unrelated', 'image'], json.loads(result.stdout))
        docker.write_text(f'#!{sys.executable}\nimport json\nprint(json.dumps([{{"Config": {{"Labels": {{}}}}}}]))\n')
        result = subprocess.run([str(self.root / 'bin/docker'), 'start', 'dapr_redis'], text=True, capture_output=True)
        self.assertNotEqual(0, result.returncode)
        self.assertIn('foreign Dapr', result.stderr)


if __name__ == '__main__':
    unittest.main()

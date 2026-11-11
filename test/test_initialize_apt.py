"""Exercise APT recovery with fake commands and private filesystem fixtures."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
SCRIPT = ROOT / 'Github/initialize-apt/initialize-apt.sh'
ORIGINAL = (b'http://azure.archive.ubuntu.com/ubuntu/\tpriority:1\n'
            b'https://archive.ubuntu.com/ubuntu/\tpriority:2\n'
            b'https://security.ubuntu.com/ubuntu/\tpriority:3\n')
FALLBACK = b'https://mirrors.edge.kernel.org/ubuntu/\tpriority:0\n'
FAKE = r'''#!/usr/bin/env python3
import json, os, pathlib, shutil, sys
name = pathlib.Path(sys.argv[0]).name
args = sys.argv[1:]
event = {'command': name, 'arguments': args}
if name == 'apt-get':
    event['mirrors'] = pathlib.Path(os.environ['FAKE_MIRRORS']).read_text()
with open(os.environ['FAKE_LOG'], 'a') as out:
    out.write(json.dumps(event) + '\n')
if name == 'timeout':
    while args[0].startswith('--'):
        args.pop(0)
    args.pop(0)
    os.execvp(args[0], args)
elif name == 'sudo':
    os.execvp(args[0], args)
elif name == 'getent':
    if args[-1] == os.environ.get('FAKE_HEALTHY_HOST'):
        print('127.0.0.1 STREAM fixture')
    else:
        sys.exit(2)
elif name == 'curl':
    if os.environ.get('FAKE_CURL_FAIL') == '1':
        sys.exit(22)
    pathlib.Path(args[args.index('--output') + 1]).write_text('fixture InRelease')
elif name == 'gpgv':
    sys.exit(1 if os.environ.get('FAKE_BAD_SIGNATURE') == '1' else 0)
elif name == 'install':
    shutil.copyfile(args[-2], args[-1])
elif name == 'apt-get':
    sys.exit(100 if os.environ.get('FAKE_APT_FAIL') == '1' else 0)
else:
    raise AssertionError(name)
'''


class InitializeAptTests(unittest.TestCase):
    def setUp(self):
        temporary = tempfile.TemporaryDirectory(prefix='apt-recovery-test-')
        self.addCleanup(temporary.cleanup)
        self.root = Path(temporary.name)
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        for name in ('timeout', 'sudo', 'getent', 'curl', 'gpgv', 'install', 'apt-get'):
            path = self.bin / name
            path.write_text(FAKE)
            path.chmod(0o755)
        self.os_release = self.root / 'os-release'
        self.os_release.write_text('ID=ubuntu\nVERSION_CODENAME=noble\n')
        self.mirrors = self.root / 'apt-mirrors.txt'
        self.mirrors.write_bytes(ORIGINAL)
        self.keyring = self.root / 'ubuntu-archive-keyring.gpg'
        self.keyring.write_bytes(b'installed fixture archive keyring')
        self.log = self.root / 'commands.jsonl'
        self.environment = os.environ | {'PATH': str(self.bin) + os.pathsep + os.environ['PATH'],
                                        'FAKE_LOG': str(self.log), 'FAKE_MIRRORS': str(self.mirrors)}

    def run_recovery(self, **settings):
        result = subprocess.run(['bash', '-c', 'source "$1"; _initialize_apt "$2" "$3" "$4"',
                                 'apt-fixture', str(SCRIPT), str(self.os_release),
                                 str(self.mirrors), str(self.keyring)],
                                env=self.environment | settings, capture_output=True, text=True, timeout=10)
        events = [json.loads(row) for row in self.log.read_text().splitlines()] if self.log.exists() else []
        return result, events

    def test_healthy_dns_does_not_mutate_or_run_package_commands(self):
        before = self.mirrors.stat()
        result, events = self.run_recovery(FAKE_HEALTHY_HOST='archive.ubuntu.com')
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mirrors.read_bytes(), ORIGINAL)
        self.assertEqual(self.mirrors.stat().st_mtime_ns, before.st_mtime_ns)
        self.assertTrue(events)
        self.assertTrue(all(event['command'] in ('timeout', 'getent') for event in events))

    def test_fallback_verifies_before_prepending_priority_zero_and_strict_update(self):
        result, events = self.run_recovery()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mirrors.read_bytes(), FALLBACK + ORIGINAL)
        commands = [event['command'] for event in events]
        self.assertLess(commands.index('curl'), commands.index('gpgv'))
        self.assertLess(commands.index('gpgv'), commands.index('install'))
        self.assertLess(commands.index('install'), commands.index('apt-get'))
        curl = next(event['arguments'] for event in events if event['command'] == 'curl')
        self.assertEqual(curl[curl.index('--proto') + 1], '=https')
        self.assertEqual(curl[curl.index('--proto-redir') + 1], '=https')
        self.assertIn('https://mirrors.edge.kernel.org/ubuntu/dists/noble/InRelease', curl)
        verify = next(event['arguments'] for event in events if event['command'] == 'gpgv')
        self.assertEqual(verify[:2], ['--keyring', str(self.keyring)])
        update = next(event for event in events if event['command'] == 'apt-get')
        self.assertEqual(update['mirrors'].encode(), FALLBACK + ORIGINAL)
        self.assertIn('APT::Update::Error-Mode=any', update['arguments'])
        self.assertIn('Acquire::Retries=1', update['arguments'])
        self.assertIn('Acquire::http::Timeout=10', update['arguments'])
        self.assertIn('Acquire::https::Timeout=10', update['arguments'])
        self.assertEqual(update['arguments'][-1], 'update')
        self.assertTrue(any('150s' in event['arguments'] for event in events if event['command'] == 'timeout'))

    def test_bad_signature_refuses_before_mutation_or_update(self):
        result, events = self.run_recovery(FAKE_BAD_SIGNATURE='1')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mirrors.read_bytes(), ORIGINAL)
        self.assertIn('gpgv', [event['command'] for event in events])
        self.assertFalse(any(event['command'] in ('install', 'apt-get') for event in events))

    def test_retrieval_failure_refuses_before_mutation(self):
        result, events = self.run_recovery(FAKE_CURL_FAIL='1')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mirrors.read_bytes(), ORIGINAL)
        self.assertFalse(any(event['command'] in ('gpgv', 'install', 'apt-get') for event in events))

    def test_failed_apt_refresh_refuses_and_restores_original_entries(self):
        result, events = self.run_recovery(FAKE_APT_FAIL='1')
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mirrors.read_bytes(), ORIGINAL)
        self.assertEqual(sum(event['command'] == 'install' for event in events), 2)

    def test_unsupported_layout_refuses_before_retrieval(self):
        self.mirrors.write_bytes(b'https://unrecognized.invalid/ubuntu/\tpriority:1\n')
        result, events = self.run_recovery()
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mirrors.read_bytes(), b'https://unrecognized.invalid/ubuntu/\tpriority:1\n')
        self.assertFalse(any(event['command'] in ('curl', 'gpgv', 'install', 'apt-get') for event in events))

    def test_existing_fallback_is_not_duplicated(self):
        self.mirrors.write_bytes(FALLBACK + ORIGINAL)
        result, events = self.run_recovery()
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mirrors.read_bytes(), FALLBACK + ORIGINAL)
        self.assertFalse(any(event['command'] == 'install' for event in events))
        self.assertEqual(sum(event['command'] == 'apt-get' for event in events), 1)

    def test_executable_accepts_no_path_or_network_inputs(self):
        result = subprocess.run(['bash', str(SCRIPT), str(self.os_release)],
                                env=self.environment, capture_output=True, text=True, timeout=10)
        self.assertEqual(result.returncode, 2)
        self.assertFalse(self.log.exists())


if __name__ == '__main__':
    unittest.main()

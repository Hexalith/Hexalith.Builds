"""Execute the trusted resolve composite with disposable Git and HTTP controls."""
from __future__ import annotations

from contextlib import contextmanager
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest

import yaml

ROOT = Path(__file__).resolve().parents[3]
ACTION = ROOT / 'Github/release-resolve'
REAL_GIT = shutil.which('git')


class ResolveAdapterTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / 'source'
        self.source.mkdir()
        self.git('init', '-b', 'main')
        self.git('config', 'user.name', 'Fixture')
        self.git('config', 'user.email', 'fixture@example.com')
        self.config = {'schema': 'hexalith.ci.v1', 'solution': 'Fixture.slnx', 'dapr': False,
                       'browsers': False, 'registry': False, 'codeql': [], 'hooks': [], 'jobs': {},
                       'tests': {'integration': [], 'exclude': {}}, 'submodules': []}
        self.commit_config()
        self.old_sha = self.sha
        self.config.update(codeql=['csharp'], hooks=['nightly'])
        self.commit_config()
        self.drafts = []
        self.ci = 'success'
        self.requests = []
        self.bin = self.root / 'bin'
        self.bin.mkdir()
        # Intercept only the anonymous remote location; all actual Git object
        # operations still run against real Git histories. Forbid tools in resolve.
        (self.bin / 'git').write_text(f'''#!{sys.executable}
import json, os, subprocess, sys
assert not any(k in os.environ for k in ('GITHUB_TOKEN','GH_TOKEN','GITHUB_SHA'))
a=sys.argv[1:]
with open(os.environ['TEST_LEDGER'],'a') as f: f.write(json.dumps(a)+'\\n')
if 'fetch' in a:
    a=[os.environ['TEST_REMOTE'] if x.startswith('https://github.com/') else x for x in a]
    a=[x.replace('protocol.file.allow=never','protocol.file.allow=always') for x in a]
raise SystemExit(subprocess.call([{REAL_GIT!r},*a]))
''')
        (self.bin / 'git').chmod(0o755)
        for tool in ('dotnet', 'npm', 'uv', 'pip'):
            path = self.bin / tool
            path.write_text('#!/bin/sh\necho forbidden-tool >&2\nexit 99\n')
            path.chmod(0o755)

    def git(self, *args):
        return subprocess.check_output([REAL_GIT, '-C', str(self.source), *args], text=True, stderr=subprocess.DEVNULL).strip()

    def commit_config(self):
        path = self.source / '.github/hexalith-ci.json'
        path.parent.mkdir(exist_ok=True)
        path.write_text(json.dumps(self.config))
        # A checkout of this file must never run an authored filter.
        (self.source / '.gitattributes').write_text('*.json filter=poison\n')
        self.git('add', '.')
        self.git('commit', '-m', 'fixture')
        self.sha = self.git('rev-parse', 'HEAD')

    @contextmanager
    def server(self):
        fixture = self
        class Handler(BaseHTTPRequestHandler):
            def log_message(self, *args):
                pass
            def do_GET(self):
                fixture.requests.append(self.path)
                if '/releases?' in self.path:
                    value = fixture.drafts
                elif '/actions/workflows/' in self.path:
                    value = {'workflow_runs': [{'id': 12, 'head_sha': fixture.sha,
                             'event': 'push', 'status': 'completed', 'conclusion': fixture.ci}]}
                else:
                    self.send_error(404)
                    return
                payload = json.dumps(value).encode()
                self.send_response(200)
                self.send_header('Content-Type', 'application/json')
                self.send_header('Content-Length', str(len(payload)))
                self.end_headers()
                self.wfile.write(payload)
        server = ThreadingHTTPServer(('127.0.0.1', 0), Handler)
        thread = threading.Thread(target=server.serve_forever, daemon=True)
        thread.start()
        try:
            yield f'http://127.0.0.1:{server.server_port}'
        finally:
            server.shutdown()
            server.server_close()
            thread.join()

    def invoke(self, *, switch='false', ref='refs/heads/main', workflow='ci.yml', module_root=''):
        output = self.root / 'output'
        output.write_text('')
        ledger = self.root / 'ledger'
        environment = dict(os.environ, PATH=str(self.bin) + os.pathsep + os.environ['PATH'],
            GITHUB_ACTION_PATH=str(ACTION), GITHUB_TOKEN='adapter-secret-marker', GITHUB_REPOSITORY='Hexalith/Hexalith.Builds',
            GITHUB_SHA=self.sha, GITHUB_REF=ref, GITHUB_OUTPUT=str(output), RELEASE_SWITCH=switch,
            RELEASE_CI_WORKFLOW=workflow, RELEASE_MODULE_ROOT=module_root,
            TEST_REMOTE=str(self.source), TEST_LEDGER=str(ledger))
        action = yaml.safe_load((ACTION / 'action.yml').read_text())
        script = action['runs']['steps'][0]['run']
        with self.server() as base:
            environment['GITHUB_API_URL'] = base
            result = subprocess.run(['bash', '-e', '-o', 'pipefail', '-c', script], env=environment,
                                    cwd=self.root, text=True, capture_output=True)
        values = dict(line.split('=', 1) for line in output.read_text().splitlines())
        self.assertNotIn('adapter-secret-marker', result.stdout + result.stderr + ledger.read_text())
        self.assertNotIn('forbidden-tool', result.stdout + result.stderr)
        return result, values

    def draft(self, **changes):
        value = {'id': 17, 'draft': True, 'prerelease': False, 'author': {'login': 'github-actions[bot]'},
                 'body': 'Notes\n\nhexalith-release: ' + self.old_sha, 'target_commitish': self.old_sha, 'tag_name': 'v1.0.0'}
        value.update(changes)
        return value

    def test_disabled_adapter_keeps_dispatch_settings_and_uses_only_git_and_stdlib(self):
        result, values = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('publishing disabled', result.stdout)
        self.assertEqual({'sha': self.sha, 'mode': 'create', 'enabled': 'false', 'codeql': '["csharp"]',
                          'nightly-hook': 'true', 'config-valid': 'true'}, values)
        self.assertEqual(1, len(self.requests))

    def test_invalid_switch_and_wrong_ref_retain_valid_current_settings(self):
        result, values = self.invoke(switch='')
        self.assertEqual(1, result.returncode)
        self.assertIn('HEXALITH_RELEASE_PUBLISH_ENABLED must be exactly true or false', result.stderr)
        self.assertEqual(self.sha, values['sha'])
        self.assertEqual('true', values['nightly-hook'])
        result, values = self.invoke(ref='refs/heads/topic')
        self.assertEqual(1, result.returncode)
        self.assertIn('refs/heads/main', result.stderr)
        self.assertNotIn('sha', values)
        self.assertEqual('["csharp"]', values['codeql'])

    def test_enabled_adapter_checks_latest_exact_source_and_retains_sha_after_red(self):
        result, values = self.invoke(switch='true', workflow='builds-ci.yml')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn('head_sha=' + self.sha, self.requests[-1])
        self.assertIn('/builds-ci.yml/runs?', self.requests[-1])
        self.ci = 'failure'
        result, values = self.invoke(switch='true')
        self.assertEqual(1, result.returncode)
        self.assertIn('main is red, nothing released', result.stderr)
        self.assertEqual(self.sha, values['sha'])

    def test_complete_draft_uses_dispatch_config_without_historical_ci_or_checkout(self):
        self.drafts = [self.draft()]
        result, values = self.invoke(switch='true')
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.old_sha, values['sha'])
        self.assertEqual('complete', values['mode'])
        self.assertEqual('1.0.0', values['version'])
        self.assertEqual('["csharp"]', values['codeql'])
        self.assertEqual('true', values['nightly-hook'])
        self.assertFalse(any('/actions/' in request for request in self.requests))
        commands = (self.root / 'ledger').read_text()
        self.assertNotIn('checkout', commands)
        self.assertNotIn('submodule', commands.replace('--no-recurse-submodules', ''))

    def test_malformed_draft_fails_by_identity_and_unmarked_draft_is_ignored(self):
        self.drafts = [self.draft(body='ordinary manual notes')]
        result, values = self.invoke()
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual('create', values['mode'])
        self.drafts.append(self.draft(target_commitish=self.sha))
        result, values = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertIn('draft 17 (v1.0.0)', result.stderr)
        self.assertNotIn('sha', values)

    def test_invalid_config_cannot_fabricate_settings_and_fixture_roots_are_guarded(self):
        self.config['unexpected'] = True
        self.commit_config()
        result, values = self.invoke()
        self.assertEqual(1, result.returncode)
        self.assertEqual({}, values)
        self.assertIn('unknown keys', result.stderr)
        result, values = self.invoke(module_root='../escape')
        self.assertEqual(1, result.returncode)
        self.assertEqual({}, values)
        self.assertIn('test-only Builds sample', result.stderr)


if __name__ == '__main__':
    unittest.main()

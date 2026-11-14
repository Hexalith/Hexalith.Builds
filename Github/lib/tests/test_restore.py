"""Qualify isolated ownership writes against disposable real Git remotes."""
from __future__ import annotations

from contextlib import redirect_stderr
from dataclasses import replace
import copy
import http.client
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
import io
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
from unittest.mock import patch
from urllib.error import HTTPError, URLError

from test_expected_state import Repository, LocalTransport, REAL_GIT
import restore as R


class Dispatch:
    def __init__(self, failure=None):
        self.calls = []
        self.failure = failure

    def send(self, repository, token):
        self.calls.append((repository, token))
        if self.failure:
            raise self.failure


class Transport(LocalTransport):
    def __init__(self, module, delivered, builds):
        super().__init__(delivered, builds)
        self.module_source = module
        self.pushes = []
        self.before_push = None
        self.private_roots = []

    def execute(self, git, *args):
        result = subprocess.run([REAL_GIT, '-c', 'protocol.file.allow=always',
            '-c', 'core.hooksPath=' + os.devnull, '-C', str(git.root), *args],
            env=git.environment, capture_output=True)
        if result.returncode:
            raise R.RestoreError('fixture Git operation failed: ' + args[0])
        return result.stdout

    def module(self, git, repository):
        self.calls.append(('module', repository))
        self.private_roots.append(git.root)
        self.execute(git, 'fetch', '--depth=1', '--no-tags', '--no-recurse-submodules',
            self.module_source.root.as_uri(), 'refs/heads/main:refs/heads/main')
        git.run('symbolic-ref', 'HEAD', 'refs/heads/main')
        return git.run('rev-parse', 'HEAD^{commit}').decode().strip()

    def push(self, git, repository, commit, token):
        self.pushes.append(commit)
        if self.before_push:
            self.before_push()
        self.execute(git, 'push', '--no-verify', '--no-recurse-submodules',
                     self.module_source.root.as_uri(), commit + ':refs/heads/main')


class RestoreTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='restore-test-')
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.module = Repository(self.root / 'module')
        self.delivered = Repository(self.root / 'delivered')
        self.builds = Repository(self.root / 'builds')
        self.builds.write('source', 'trusted runtime')
        self.builds_sha = self.builds.commit()
        self.builds.command('tag', '-a', 'v1.2.3', '-m', 'stable accepted tag')
        self.builds.write('source', 'newer trusted runtime')
        self.new_builds_sha = self.builds.commit()
        self.builds.command('tag', '-a', 'v1.3.0', '-m', 'newer stable tag')
        corpus = json.loads((Path(__file__).parent / 'fixtures/perimeter-contract.json').read_text())
        self.document = copy.deepcopy(corpus[0]['document'])
        self.config = b'{"buildsTag":"v1.2.3","solution":"none"}\n'
        self.workflow = b'name: fixture\non: workflow_dispatch\njobs: {}\n'
        for path, data in (('.github/hexalith-ci.json', self.config),
                           ('.github/dependabot.yml', b'version: 2\n'),
                           ('.github/workflows/ci.yml', self.workflow)):
            self.delivered.write('rendered/Fixture/' + path, data)
            self.module.write(path, data)
        self.module.write('product.txt', 'product state\n')
        self.module.commit()
        self.publish()
        # Establish a clean initial perimeter through the existing application
        # contract. Assertions below inspect remote Git state independently.
        R.perimeter.apply(self.document, R.perimeter.Git(self.module.root),
            expected=R.state.Git(self.delivered.root).snapshot(), rendered_root='rendered/Fixture',
            builds=R.state.Git(self.builds.root))
        self.module.command('commit', '-qm', 'test: clean initial perimeter')
        self.module.command('reset', '--hard', 'HEAD')
        self.initial = self.module.command('rev-parse', 'HEAD')
        self.module.command('config', 'receive.denyCurrentBranch', 'ignore')
        self.transport = Transport(self.module, self.delivered, self.builds)
        self.dispatch = Dispatch()
        self.context = R.Context('Example/Fixture', 'push', 'refs/heads/main', 'true',
            'Hexalith/Hexalith.Builds/.github/workflows/ci.yml@refs/tags/v1.2.3', self.builds_sha,
            'Hexalith/Hexalith.Builds')
        self.logs = []

    def publish(self):
        self.delivered.write('rendered/Fixture.perimeter.json', json.dumps(self.document))
        revision = self.delivered.commit()
        self.delivered.command('branch', '-f', 'delivered/Fixture', revision)
        return revision

    def invoke(self, **changes):
        result = R.run(replace(self.context, **changes), 'secret-marker', transport=self.transport,
                       dispatch=self.dispatch, report=self.logs.append)
        self.assertTrue(all(not directory.exists() for directory in self.transport.private_roots))
        return result

    def snapshot(self):
        return R.state.Git(self.module.root).snapshot('main')

    def test_real_repair_preserves_product_and_foreign_partial_content_in_one_commit(self):
        self.module.write('product.txt', 'new product state\n')
        self.module.write('.github/hexalith-ci.json', 'malformed whole JSON')
        self.module.write('.claude/settings.json', '{"foreign":{"keep":true},"permissions":{"allow":["Read(*)"],"deny":[]}}')
        self.module.write('AGENTS.md', 'keep these product instructions\n')
        self.module.write('package.json', '{"name":"product","release":{},"devDependencies":{"@commitlint/cli":"x","keep":"y"}}')
        self.module.write('.husky/commit-msg', '# keep comment\nnpx commitlint --edit\necho keep\n')
        self.module.write('Directory.Build.rsp', 'forbidden')
        parent = self.module.commit()
        accepted = self.delivered.command('rev-parse', 'delivered/Fixture')
        result = self.invoke()
        actual = self.snapshot()
        self.assertEqual(result.commit, self.module.command('rev-parse', 'main'))
        self.assertEqual(parent, self.module.command('show', '-s', '--format=%P', 'main'))
        self.assertEqual('1', self.module.command('rev-list', '--count', parent + '..main'))
        self.assertEqual(R.COMMIT_MESSAGE.strip(), self.module.command('show', '-s', '--format=%B', 'main'))
        self.assertEqual('github-actions[bot]', self.module.command('show', '-s', '--format=%an', 'main'))
        self.assertEqual(accepted, result.delivered_sha)
        self.assertEqual(b'new product state\n', actual.read('product.txt'))
        self.assertEqual(self.config, actual.read(R.CONFIG))
        settings = json.loads(actual.read('.claude/settings.json'))
        self.assertEqual({'keep': True}, settings['foreign'])
        self.assertEqual(['Read(*)'], settings['permissions']['allow'])
        self.assertIn(b'keep these product instructions', actual.read('AGENTS.md'))
        self.assertEqual({'name': 'product', 'devDependencies': {'keep': 'y'}}, json.loads(actual.read('package.json')))
        self.assertEqual(b'# keep comment\necho keep\n', actual.read('.husky/commit-msg'))
        self.assertNotIn('Directory.Build.rsp', actual.items)
        self.assertEqual([result.commit], self.transport.pushes)
        self.assertEqual([('Example/Fixture', 'secret-marker')], self.dispatch.calls)

    def test_missing_and_malformed_partial_files_are_recreated_with_owned_content(self):
        (self.module.root / 'CLAUDE.md').unlink()
        self.module.write('.claude/settings.json', 'malformed JSON')
        self.module.write('.gitmodules', 'malformed Git config')
        self.module.commit()
        self.invoke()
        snapshot = self.snapshot()
        self.assertEqual({'permissions'}, set(json.loads(snapshot.read('.claude/settings.json'))))
        self.assertIn(b'hexalith-cicd:begin', snapshot.read('CLAUDE.md'))
        self.assertIn(b'Hexalith.Builds.git', snapshot.read('.gitmodules'))

    def test_workflow_content_missing_extra_and_mode_drift_stays_identical_and_config_is_skipped(self):
        for mutation in ('content', 'missing', 'extra', 'mode'):
            with self.subTest(mutation=mutation):
                self.setUp_clean_worktree()
                target = self.module.root / '.github/workflows/ci.yml'
                if mutation == 'content':
                    target.write_text('changed caller')
                elif mutation == 'missing':
                    target.unlink()
                elif mutation == 'extra':
                    self.module.write('.github/workflows/unrendered.yml', 'extra caller')
                else:
                    target.chmod(0o755)
                self.module.write(R.CONFIG, 'configuration remains module current')
                self.module.write('AGENTS.md', 'repair independent instructions')
                self.module.commit()
                before = self.snapshot()
                self.invoke()
                after = self.snapshot()
                self.assertEqual({p: v for p, v in before.items.items() if p.startswith(R.WORKFLOWS + '/')},
                                 {p: v for p, v in after.items.items() if p.startswith(R.WORKFLOWS + '/')})
                self.assertEqual(before.read(R.CONFIG), after.read(R.CONFIG))

    def setUp_clean_worktree(self):
        self.module.command('reset', '--hard', self.initial)

    def test_verified_matching_tag_repairs_gitlink_section_and_config(self):
        self.module.command('update-index', '--cacheinfo', '160000', self.new_builds_sha, R.BUILDS_PATH)
        self.module.command('commit', '-qm', 'test: alter gitlink')
        self.module.write('.gitmodules', '[submodule "custom"]\n path = references/Hexalith.Builds\n url = bad\n update = !touch marker\n custom = retained\n[submodule "product"]\n path = other\n url = elsewhere\n')
        self.module.write(R.CONFIG, 'broken')
        self.module.commit()
        result = self.invoke()
        self.assertTrue(result.commit)
        snapshot = self.snapshot()
        self.assertEqual(self.builds_sha, snapshot.items[R.BUILDS_PATH].oid)
        self.assertIn(b'custom = retained', snapshot.read('.gitmodules'))
        self.assertIn(b'url = elsewhere', snapshot.read('.gitmodules'))
        self.assertNotIn(b'update =', snapshot.read('.gitmodules'))
        self.assertEqual(self.config, snapshot.read(R.CONFIG))

    def test_both_tag_transition_directions_preserve_lockstep_and_repair_independent_files(self):
        for accepted_tag, running_tag, source_sha in (('v1.2.3', 'v1.3.0', self.new_builds_sha),
                                                       ('v1.3.0', 'v1.2.3', self.builds_sha)):
            with self.subTest(accepted=accepted_tag, running=running_tag):
                self.setUp_clean_worktree()
                next(e for e in self.document['entries'] if e['kind'] == 'gitlink')['buildsTag'] = accepted_tag
                self.publish()
                self.module.write(R.CONFIG, 'current config')
                self.module.write('.gitmodules', '[submodule "current"]\n path = references/Hexalith.Builds\n url = current\n')
                self.module.write('AGENTS.md', 'current instructions')
                self.module.commit()
                before = self.snapshot()
                self.invoke(workflow_ref=self.context.workflow_ref.rsplit('@', 1)[0] + '@' + running_tag,
                            workflow_sha=source_sha)
                after = self.snapshot()
                for path in (R.BUILDS_PATH, R.CONFIG, '.gitmodules'):
                    self.assertEqual(before.items.get(path), after.items.get(path))
                self.assertNotEqual(before.read('AGENTS.md'), after.read('AGENTS.md'))

    def test_branch_and_sha_sources_repair_only_independent_entries(self):
        for reference in ('refs/heads/main', self.builds_sha):
            with self.subTest(reference=reference):
                self.setUp_clean_worktree()
                self.module.write(R.CONFIG, 'retained current config')
                self.module.write('AGENTS.md', 'repair instructions')
                self.module.commit()
                self.invoke(workflow_ref=self.context.workflow_ref.rsplit('@', 1)[0] + '@' + reference)
                self.assertEqual(b'retained current config', self.snapshot().read(R.CONFIG))

    def test_direct_invocation_rejects_forbidden_context_before_network(self):
        cases = ({'event_name': 'pull_request'}, {'event_name': 'workflow_dispatch'}, {'ref': 'refs/heads/other'},
                 {'drift': ''}, {'drift': 'false'}, {'drift': 'TRUE'}, {'cancelled': 'true'},
                 {'repository': 'Hexalith/Hexalith.Builds'}, {'module_root': 'Samples/Hexalith.Sample'},
                 {'expected_source': 'payload'}, {'repository': 'Example/..'},
                 {'workflow_repository': 'Example/Builds'}, {'workflow_sha': 'main'},
                 {'workflow_ref': 'Example/Builds/.github/workflows/ci.yml@v1.2.3'},
                 {'workflow_ref': 'Hexalith/Hexalith.Builds/.github/workflows/other.yml@v1.2.3'})
        for changes in cases:
            with self.subTest(changes=changes), self.assertRaises((R.RestoreError, R.state.ExpectedStateError)):
                self.invoke(**changes)
        self.assertEqual([], self.transport.calls)
        self.assertEqual([], self.dispatch.calls)

    def test_tag_spoof_and_missing_noncommit_tags_fail_before_push(self):
        for sha in (self.new_builds_sha, 'f' * 40):
            with self.subTest(sha=sha), self.assertRaisesRegex(R.RestoreError, 'tag object'):
                self.invoke(workflow_sha=sha)
        self.builds.command('tag', '-d', 'v1.2.3')
        blob = self.builds.command('rev-parse', 'HEAD:source')
        self.builds.command('tag', 'v1.2.3', blob)
        with self.assertRaisesRegex(R.RestoreError, 'resolve to a commit'):
            self.invoke(workflow_sha=blob)
        self.assertEqual([], self.transport.pushes)
        self.assertEqual([], self.dispatch.calls)

    def test_unavailable_invalid_and_oversized_expected_state_never_publishes(self):
        parent = self.module.command('rev-parse', 'main')
        self.delivered.command('branch', '-D', 'delivered/Fixture')
        with self.assertRaises(R.state.ExpectedStateError):
            self.invoke()
        for data in (b'{bad', b'x' * (R.MAX_BYTES + 1)):
            self.delivered.write('rendered/Fixture.perimeter.json', data)
            revision = self.delivered.commit()
            self.delivered.command('branch', '-f', 'delivered/Fixture', revision)
            with self.assertRaises(R.state.ExpectedStateError):
                self.invoke()
        self.publish()
        self.delivered.write('rendered/Fixture/.github/dependabot.yml', b'x' * (R.MAX_BYTES + 1))
        self.publish()
        with self.assertRaisesRegex(R.state.ExpectedStateError, '4 MiB'):
            self.invoke()
        self.assertEqual(parent, self.module.command('rev-parse', 'main'))
        self.assertEqual([], self.transport.pushes)
        self.assertEqual([], self.dispatch.calls)

    def test_symlink_expected_source_is_rejected_even_when_workflow_is_excluded(self):
        source = self.delivered.root / 'rendered/Fixture/.github/workflows/ci.yml'
        source.unlink()
        source.symlink_to('untrusted')
        self.publish()
        with self.assertRaisesRegex(R.state.ExpectedStateError, 'non-regular'):
            self.invoke()
        self.assertEqual([], self.transport.pushes)

    def test_large_actual_partial_json_is_rejected_before_extraction(self):
        self.module.write('.claude/settings.json', b' ' * (R.MAX_BYTES + 1))
        self.module.commit()
        with self.assertRaisesRegex(R.RestoreError, '4 MiB'):
            self.invoke()
        self.assertEqual([], self.transport.pushes)

    def test_indirect_absent_glob_cannot_delete_workflows_but_other_entries_repair(self):
        path = '.github/workflows/bmad-testarch-ci/extra.yml'
        self.module.write(path, 'preserve forbidden workflow')
        self.module.write('tools/bmad-testarch-ci/obsolete.py', 'also skipped with this broad entry')
        self.module.write('AGENTS.md', 'repair independent file')
        self.module.commit()
        before = self.snapshot()
        self.invoke()
        after = self.snapshot()
        self.assertEqual(before.items[path], after.items[path])
        self.assertIn('tools/bmad-testarch-ci/obsolete.py', after.items)
        self.assertIn('restore: skipped', '\n'.join(self.logs))

    def test_ancestor_and_descendant_conflicts_cannot_remove_workflow_files(self):
        # An accepted absence rule owns a parent of an additional protected
        # workflow. Its removal cannot replace the subtree indirectly.
        self.document['entries'] = [
            {'kind': 'whole', 'path': 'AGENTS.md'},
            {'kind': 'absent-glob', 'pattern': '.github/**'}]
        self.delivered.write('rendered/Fixture/AGENTS.md', 'trusted instructions')
        self.publish()
        self.module.write('AGENTS.md', 'repair')
        self.module.commit()
        before = self.snapshot()
        self.invoke()
        self.assertEqual(before.items['.github/workflows/ci.yml'], self.snapshot().items['.github/workflows/ci.yml'])

    def test_unsafe_structural_conflict_fails_before_publication(self):
        target = self.module.root / 'AGENTS.md'
        target.unlink()
        self.module.write('AGENTS.md/product.txt', 'must preserve product descendant')
        self.module.commit()
        parent = self.module.command('rev-parse', 'main')
        with self.assertRaises(R.perimeter.PerimeterError):
            self.invoke()
        self.assertEqual(parent, self.module.command('rev-parse', 'main'))
        self.assertEqual([], self.dispatch.calls)

    def test_concurrent_product_push_rejects_once_without_retry_or_dispatch(self):
        self.module.write('AGENTS.md', 'repair')
        self.module.commit()
        newest = []
        def race():
            self.module.write('product.txt', 'concurrent product writer\n')
            newest.append(self.module.commit())
        self.transport.before_push = race
        with self.assertRaisesRegex(R.RestoreError, 'push'):
            self.invoke()
        self.assertEqual(newest[0], self.module.command('rev-parse', 'main'))
        self.assertEqual(b'concurrent product writer\n', self.snapshot().read('product.txt'))
        self.assertEqual(1, len(self.transport.pushes))
        self.assertEqual([], self.dispatch.calls)

    def test_post_push_dispatch_failure_then_clean_rerun_does_not_commit_twice(self):
        self.module.write('AGENTS.md', 'repair')
        self.module.commit()
        self.dispatch.failure = R.RestoreError('controlled dispatch unavailable')
        with self.assertRaisesRegex(R.RestoreError, 'dispatch unavailable'):
            self.invoke()
        repaired = self.module.command('rev-parse', 'main')
        self.assertEqual([repaired], self.transport.pushes)
        self.dispatch.failure = None
        result = self.invoke()
        self.assertEqual('', result.commit)
        self.assertTrue(result.dispatched)
        self.assertEqual(repaired, self.module.command('rev-parse', 'main'))
        self.assertEqual([repaired], self.transport.pushes)
        self.assertEqual(2, len(self.dispatch.calls))

    def test_protocol_dispatch_failure_preserves_successful_repair(self):
        class Opener:
            def open(self, *args, **kwargs):
                raise http.client.BadStatusLine('secret-marker raw response text')
        self.module.write('AGENTS.md', 'repair')
        parent = self.module.commit()
        self.dispatch = R.Dispatch(Opener())
        with self.assertRaises(R.RestoreError) as raised:
            self.invoke()
        self.assertEqual('CI dispatch transport failed', str(raised.exception))
        self.assertNotIn('secret-marker', str(raised.exception))
        repaired = self.module.command('rev-parse', 'main')
        self.assertNotEqual(parent, repaired)
        self.assertEqual([repaired], self.transport.pushes)
        self.assertEqual(parent, self.module.command('show', '-s', '--format=%P', repaired))
        self.assertEqual(b'product state\n', self.snapshot().read('product.txt'))
        self.assertIn(b'hexalith-cicd:begin', self.snapshot().read('AGENTS.md'))

    def test_excluded_only_drift_never_commits_pushes_or_dispatches(self):
        self.module.write('.github/workflows/ci.yml', 'changed caller')
        self.module.commit()
        result = self.invoke()
        self.assertEqual('', result.commit)
        self.assertFalse(result.dispatched)
        self.assertEqual([], self.transport.pushes)
        self.assertEqual([], self.dispatch.calls)

    def test_poisoned_environment_hooks_filters_and_module_configuration_never_execute(self):
        self.module.write('AGENTS.md', 'repair')
        self.module.write('.gitattributes', '*.json filter=poison\n*.md filter=poison\n')
        self.module.commit()
        marker = self.root / 'MODULE_CODE_EXECUTED'
        script = self.root / 'poison'
        script.write_text('#!/bin/sh\ntouch ' + str(marker) + '\nexit 89\n')
        script.chmod(0o755)
        for command in ('clean', 'smudge', 'process'):
            self.module.command('config', 'filter.poison.' + command, str(script))
        self.module.command('config', 'filter.poison.required', 'true')
        self.module.command('config', 'core.hooksPath', str(self.root))
        self.module.command('config', 'gpg.program', str(script))
        self.module.command('config', 'commit.gpgsign', 'true')
        poisoned = {'GIT_CONFIG_COUNT': '1', 'GIT_CONFIG_KEY_0': 'core.hooksPath',
            'GIT_CONFIG_VALUE_0': str(script), 'GIT_CONFIG_PARAMETERS': 'poison',
            'GIT_DIR': str(self.module.root / '.git'), 'GIT_WORK_TREE': str(self.module.root),
            'GIT_INDEX_FILE': str(self.module.root / '.git/index'), 'GIT_ATTR_SOURCE': 'poison',
            'GIT_ASKPASS': str(script), 'GH_TOKEN': 'secret-marker', 'PYTHONPATH': str(self.root)}
        with patch.dict(os.environ, poisoned):
            result = self.invoke()
        self.assertTrue(result.commit)
        self.assertFalse(marker.exists())

    def test_attribute_conversion_failure_keeps_private_index_atomic_and_never_publishes(self):
        self.module.write('AGENTS.md', 'repair instructions')
        self.module.write('.github/dependabot.yml', 'also repair this blob')
        self.module.command('add', '--all')
        oid = self.module.command('hash-object', '-w', '--no-filters', '--stdin',
                                  data=b'*.md working-tree-encoding=UTF-16\n')
        self.module.command('update-index', '--add', '--cacheinfo', '100644', oid, '.gitattributes')
        self.module.command('commit', '-qm', 'test: committed poisoned attributes')
        parent = self.module.command('rev-parse', 'HEAD')
        original = R.perimeter.apply
        checked = []
        def observed(document, git, **arguments):
            before = git.index_path().read_bytes()
            with self.assertRaises((R.RestoreError, R.perimeter.PerimeterError)):
                original(document, git, **arguments)
            self.assertEqual(before, git.index_path().read_bytes())
            self.assertFalse(Path(str(git.index_path()) + '.lock').exists())
            self.assertEqual([], list(git.index_path().parent.glob('perimeter-index-*')))
            checked.append(True)
            raise R.RestoreError('controlled atomic application failure')
        with patch.object(R.perimeter, 'apply', side_effect=observed), self.assertRaisesRegex(R.RestoreError, 'atomic'):
            self.invoke()
        self.assertEqual([True], checked)
        self.assertEqual(parent, self.module.command('rev-parse', 'main'))
        self.assertEqual([], self.transport.pushes)
        self.assertEqual([], self.dispatch.calls)

    def test_candidate_invariant_rejects_engine_bug_before_push(self):
        self.module.write('AGENTS.md', 'repair')
        parent = self.module.commit()
        original = R.perimeter.apply
        def unsafe(document, git, **arguments):
            result = original(document, git, **arguments)
            git.run('rm', '--cached', '--force', '.github/workflows/ci.yml')
            return result
        with patch.object(R.perimeter, 'apply', side_effect=unsafe), self.assertRaisesRegex(R.RestoreError, 'protected'):
            self.invoke()
        self.assertEqual(parent, self.module.command('rev-parse', 'main'))
        self.assertEqual([], self.transport.pushes)


class DispatchTests(unittest.TestCase):
    class Response(io.BytesIO):
        status = 204

    def test_fixed_endpoint_body_and_authentication(self):
        calls = []
        class Opener:
            def open(self, request, timeout):
                calls.append((request, timeout))
                return DispatchTests.Response()
        R.Dispatch(Opener()).send('Example/Fixture', 'secret-marker')
        request, timeout = calls[0]
        self.assertEqual('https://api.github.com/repos/Example/Fixture/actions/workflows/ci.yml/dispatches', request.full_url)
        self.assertEqual('POST', request.method)
        self.assertEqual({'ref': 'main'}, json.loads(request.data))
        self.assertEqual('Bearer secret-marker', request.get_header('Authorization'))
        self.assertEqual(30, timeout)

    def test_unexpected_success_status_is_rejected(self):
        for status in (200, 202):
            response = self.Response(b'secret-marker response body')
            response.status = status
            class Opener:
                def open(self, *args, **kwargs):
                    return response
            with self.subTest(status=status), self.assertRaises(R.RestoreError) as raised:
                R.Dispatch(Opener()).send('Example/Fixture', 'secret-marker')
            self.assertEqual('CI dispatch failed: HTTP ' + str(status), str(raised.exception))
            self.assertTrue(response.closed)

    def test_main_redacts_token_and_escapes_hostile_failure_diagnostics(self):
        diagnostic = R.RestoreError('secret-marker\n::error::\x1bhostile diagnostic')
        output = io.StringIO()
        with (patch.dict(os.environ, {'RESTORE_TOKEN': 'secret-marker'}, clear=True),
              patch.object(R, 'run', side_effect=diagnostic), redirect_stderr(output)):
            self.assertEqual(1, R.main())
        self.assertEqual('restore: [REDACTED]\\u000a::error::\\u001bhostile diagnostic\n', output.getvalue())

    def test_protocol_errors_during_open_or_read_are_sanitized(self):
        class BrokenBody(self.Response):
            def read(self, amount):
                raise http.client.IncompleteRead(b'secret-marker response body', 100)
        for failure in (http.client.BadStatusLine('secret-marker raw status'), BrokenBody()):
            class Opener:
                def open(self, *args, **kwargs):
                    if isinstance(failure, Exception):
                        raise failure
                    return failure
            with self.subTest(failure=type(failure).__name__), self.assertRaises(R.RestoreError) as raised:
                R.Dispatch(Opener()).send('Example/Fixture', 'secret-marker')
            self.assertEqual('CI dispatch transport failed', str(raised.exception))
            self.assertNotIn('secret-marker', str(raised.exception))
            self.assertTrue(raised.exception.__suppress_context__)

    def test_default_opener_refuses_redirect_without_contacting_its_destination(self):
        source_requests, destination_requests = [], []
        class Destination(BaseHTTPRequestHandler):
            def do_GET(self):
                destination_requests.append((self.command, self.path, self.headers.get('Authorization')))
                self.send_response(204)
                self.end_headers()
            do_POST = do_GET
            def log_message(self, *args):
                pass
        destination = ThreadingHTTPServer(('127.0.0.1', 0), Destination)
        class Source(BaseHTTPRequestHandler):
            def do_POST(self):
                body = self.rfile.read(int(self.headers.get('Content-Length', '0')))
                source_requests.append((self.path, self.headers.get('Authorization'), body))
                self.send_response(302)
                self.send_header('Location', f'http://127.0.0.1:{destination.server_port}/capture')
                self.send_header('Content-Length', '0')
                self.end_headers()
            def log_message(self, *args):
                pass
        source = ThreadingHTTPServer(('127.0.0.1', 0), Source)
        threads = [threading.Thread(target=server.serve_forever, kwargs={'poll_interval': 0.01}, daemon=True)
                   for server in (source, destination)]
        for thread in threads:
            thread.start()
        fixture = self
        class LoopbackHTTPSConnection(http.client.HTTPConnection):
            def __init__(self, host, *, context=None, **kwargs):
                fixture.assertEqual('api.github.com', host)
                super().__init__('127.0.0.1', source.server_port, **kwargs)
        try:
            # Keep production's endpoint, opener and redirect-handler chain.
            # Only replace the HTTPS socket transport with a loopback control.
            with (patch('urllib.request.getproxies', return_value={}),
                  patch('http.client.HTTPSConnection', LoopbackHTTPSConnection),
                  self.assertRaisesRegex(R.RestoreError, 'refused a redirect')):
                R.Dispatch().send('Example/Fixture', 'secret-marker')
        finally:
            for server, thread in zip((source, destination), threads):
                server.shutdown()
                server.server_close()
                thread.join(timeout=1)
        self.assertEqual([('/repos/Example/Fixture/actions/workflows/ci.yml/dispatches',
                          'Bearer secret-marker', b'{"ref":"main"}')], source_requests)
        self.assertEqual([], destination_requests)

    def test_bounded_errors_and_redirects_never_expose_tokens(self):
        class Opener:
            def __init__(self, result):
                self.result = result
            def open(self, *args, **kwargs):
                if isinstance(self.result, Exception):
                    raise self.result
                return self.result
        for result, expected in ((self.Response(b'x' * (R.MAX_BYTES + 1)), '4 MiB'),
                (HTTPError('https://secret-marker', 403, 'secret-marker', {}, io.BytesIO(b'secret-marker')), 'HTTP 403'),
                (URLError('secret-marker'), 'transport')):
            with self.subTest(expected=expected), self.assertRaisesRegex(R.RestoreError, expected) as raised:
                R.Dispatch(Opener(result)).send('Example/Fixture', 'secret-marker')
            self.assertNotIn('secret-marker', str(raised.exception))
        with self.assertRaisesRegex(R.RestoreError, 'redirect'):
            R._NoRedirect().redirect_request(None, None, 302, 'secret-marker', {}, 'https://secret-marker')


if __name__ == '__main__':
    unittest.main()

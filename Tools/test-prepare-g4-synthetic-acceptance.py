#!/usr/bin/env python3
"""Exercise preparation separately from validator acceptance of the fixed corpus."""
import importlib.util
import json
from pathlib import Path
import unittest

repository = Path(__file__).resolve().parents[1]
spec = importlib.util.spec_from_file_location('prepare', repository/'Tools/prepare-g4-synthetic-acceptance.py')
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)


class PreparationTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        corpus = repository/'test/fixtures/evidence/acceptance'
        cls.current = {p.relative_to(corpus).as_posix():p.read_bytes() for p in corpus.rglob('*.json')}
        cls.pins = json.loads(cls.current['positive/p0-acceptance.json'])['platformPins']
        cls.earlier = dict(cls.pins, eventStoreVersion='3.120.0')
        cls.stale = helper.prepare(cls.current, cls.earlier)

    def test_refresh_is_deterministic_and_idempotent(self):
        prepared = helper.prepare(self.stale, self.pins)
        self.assertEqual(prepared, self.current)
        self.assertEqual(helper.prepare(self.stale, self.pins), prepared)
        self.assertEqual(helper.prepare(prepared, self.pins), prepared)

    def test_preserves_expectations_approvals_and_negative_mutations(self):
        for name,body in self.stale.items():
            if name.endswith('.expected.json'):
                self.assertEqual(body,self.current[name])
            if name.startswith(('positive/', 'negative/')) and not name.endswith('.expected.json'):
                self.assertEqual(json.loads(body)['approvals'],json.loads(self.current[name])['approvals'])
        self.assertEqual(json.loads(self.stale['negative/pin-mismatch.json'])['platformPins']['daprRuntimeVersion'],'1.18.0')
        self.assertEqual(json.loads(self.stale['negative/tampered-evidence-hash.json'])['runs'][0]['evidenceSha256'],'0'*64)
        self.assertEqual(json.loads(self.stale['negative/manifest-hash-mismatch.json'])['fixture']['manifestSha256'],'0'*64)
        self.assertEqual(self.stale['negative/duplicate-key.json'].count(b'"status": "accepted"'),2)

    def test_interrupted_refresh_fails_closed(self):
        partial = dict(self.stale)
        partial['bound/run-persisted-vstest.json'] = self.current['bound/run-persisted-vstest.json']
        with self.assertRaisesRegex(ValueError,'binding drift'):
            helper.prepare(partial,self.pins)

    def test_isolated_stale_pin_fails_closed(self):
        partial = dict(self.current)
        partial['negative/missing-approval.json'] = self.stale['negative/missing-approval.json']
        with self.assertRaisesRegex(ValueError,'pin drift'):
            helper.prepare(partial,self.pins)

    def test_unmatched_escaped_pin_refresh_fails_closed(self):
        escaped = dict(self.stale)
        escaped['positive/p0-acceptance.json'] = escaped['positive/p0-acceptance.json'].replace(b'3.120.0',br'3.120\u002e0')
        with self.assertRaisesRegex(ValueError,'pin drift'):
            helper.prepare(escaped,self.pins)

    def test_missing_bound_artifact_fails_closed(self):
        partial = dict(self.current)
        del partial['bound/run-persisted-vstest.json']
        with self.assertRaisesRegex(ValueError,'Missing referenced'):
            helper.prepare(partial,self.pins)

    def test_missing_control_fails_closed(self):
        partial = dict(self.current)
        del partial['negative/missing-approval.json']
        with self.assertRaisesRegex(ValueError,'Missing fixture'):
            helper.prepare(partial,self.pins)


if __name__ == '__main__':
    unittest.main()

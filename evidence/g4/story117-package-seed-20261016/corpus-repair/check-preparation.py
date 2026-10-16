#!/usr/bin/env python3
"""Independently check deterministic preparation and unchanged control material."""
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import subprocess
import tarfile

root = Path(__file__).resolve().parents[4]
evidence = Path(__file__).resolve().parent
corpus = 'test/fixtures/evidence/acceptance/'
hash_bytes = lambda b: hashlib.sha256(b).hexdigest()
spec = importlib.util.spec_from_file_location('preparation', root/'Tools/prepare-g4-synthetic-acceptance.py')
helper = importlib.util.module_from_spec(spec)
spec.loader.exec_module(helper)
archive = subprocess.check_output(['git', 'archive', 'HEAD', corpus], cwd=root)
with tarfile.open(fileobj=io.BytesIO(archive)) as tar:
    before = {m.name[len(corpus):]: tar.extractfile(m).read() for m in tar.getmembers()
              if m.isfile() and m.name.endswith('.json')}
initial = json.loads((evidence/'before-inputs.json').read_bytes())['files']
for name, body in before.items():
    assert hash_bytes(body) == initial['references/Hexalith.Builds/'+corpus+name], name
catalog = json.loads((root/'artifacts/story117-seed-20261016-corpus-repaired-verified/platform-version-catalog.json').read_bytes())
after = helper.prepare(before, catalog)
assert helper.prepare(before, catalog) == after, 'two preparations differ'
assert helper.prepare(after, catalog) == after, 'not idempotent'
for name, body in after.items():
    assert (root/corpus/name).read_bytes() == body, name
    if name.endswith('.expected.json'):
        assert body == before[name], name
    if not name.startswith('bound/') and not name.endswith('.expected.json'):
        assert json.loads(body)['approvals'] == json.loads(before[name])['approvals'], name
assert after['negative/duplicate-key.json'].count(b'"status": "accepted"') == before['negative/duplicate-key.json'].count(b'"status": "accepted"') == 2
assert json.loads(after['negative/pin-mismatch.json'])['platformPins']['daprRuntimeVersion'] == '1.18.0'
for name,field in [('tampered-evidence-hash.json','runs'),('manifest-hash-mismatch.json','fixture')]:
    original=json.loads(before['negative/'+name]);updated=json.loads(after['negative/'+name])
    if field=='runs': assert original[field][0]['evidenceSha256']==updated[field][0]['evidenceSha256']=='0'*64
    else: assert original[field]['manifestSha256']==updated[field]['manifestSha256']=='0'*64
changed = {name:{'beforeSha256':hash_bytes(before[name]), 'afterSha256':hash_bytes(body)}
           for name, body in after.items() if body != before[name]}
assert len(changed)==52
expected = {name:hash_bytes(body) for name,body in before.items() if name.endswith('.expected.json')}
record={'synthetic':True,'sourceRevision':subprocess.check_output(['git','rev-parse','HEAD'],cwd=root,text=True).strip(),
        'pins':{key:catalog[key] for key in helper.PIN_NAMES},'changedFiles':changed,
        'unchangedExpectationsSha256':expected,'twoIndependentPreparationsIdentical':True,'idempotent':True,
        'sampleApprovalsUnchanged':True,'duplicateKeyControlPreserved':True,'intentionalHashMismatchesPreserved':True,
        'legacyV1ModuleManifestAndGenericReadinessUnchanged':True}
# Every non-acceptance fixture is unchanged from the captured working tree.
for name,expected_hash in initial.items():
    if name.startswith('references/Hexalith.Builds/test/fixtures/') and not name.startswith('references/Hexalith.Builds/'+corpus):
        assert hash_bytes((root.parents[1]/name).read_bytes()) == expected_hash,name
(evidence/'fixture-preparation.json').write_text(json.dumps(record,indent=2)+'\n')
print(f'Verified {len(changed)} deterministic edits, {len(expected)} exact unchanged expectations, unchanged sample approvals, intentional mismatches, duplicate key and all non-acceptance fixtures.')

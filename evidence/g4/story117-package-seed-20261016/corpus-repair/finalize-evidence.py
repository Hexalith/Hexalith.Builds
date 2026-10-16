#!/usr/bin/env python3
"""Retain the completed local diagnostic without converting it to release evidence."""
from datetime import datetime, timezone
import copy
import gzip
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile

here=Path(__file__).resolve().parent
builds=here.parents[3]
workspace=builds.parents[1]
evidence=here.parent
package=builds/'artifacts/story117-seed-20261016-corpus-repaired-verified'
sha=lambda body:hashlib.sha256(body).hexdigest()
file_hash=lambda path:sha(path.read_bytes())
relative=lambda path:path.relative_to(builds).as_posix()
write=lambda path,value:path.write_text(json.dumps(value,indent=2)+'\n')
old=json.loads((here/'qualification.before.json').read_bytes())
record=copy.deepcopy(old)
inventory=json.loads((package/'g4-tool-package-inventory.json').read_bytes())
assert inventory['qualification']['releaseEligible'] is False
assert inventory['qualification']['sourceValidation']=={'mode':'executed','result':'passed'}
assert inventory['qualification']['controls']=={'mode':'executed','result':'passed'}
head=subprocess.check_output(['git','rev-parse','HEAD'],cwd=builds,text=True).strip()
assert head==old['source']['sourceRevision']
status=subprocess.check_output(['git','status','--porcelain'],cwd=builds,text=True).splitlines()
initial=json.loads((here/'before-inputs.json').read_bytes())['files']
protected=old['protectedWorkspaceFilesSha256']
for name,expected in protected.items(): assert file_hash(workspace/name)==expected,name
platform={name:expected for name,expected in initial.items() if name.startswith('references/Hexalith.Platform/')}
for name,expected in platform.items():assert file_hash(workspace/name)==expected,name
for name,expected in initial.items():
    if name.startswith('references/Hexalith.Builds/src/') or name in ['references/Hexalith.Builds/Props/Directory.Packages.props','references/Hexalith.Builds/Tools/package-version-audit.json']:
        assert file_hash(workspace/name)==expected,name
fixture=json.loads((here/'fixture-preparation.json').read_bytes())
source_files=dict(old['source']['sourceFilesSha256'])
for name in list(source_files):source_files[name]=file_hash(builds/name)
for path in [builds/'Tools/prepare-g4-synthetic-acceptance.py',builds/'Tools/test-prepare-g4-synthetic-acceptance.py',builds/'test/fixtures/evidence/acceptance/README.md']:
    source_files[relative(path)]=file_hash(path)
for name in fixture['changedFiles']:
    path=builds/'test/fixtures/evidence/acceptance'/name
    source_files[relative(path)]=file_hash(path)
patch=subprocess.check_output(['git','diff','--binary','HEAD','--','Tools','src','test/fixtures/evidence/acceptance'],cwd=builds)
(here/'Hexalith.Builds.prepared.patch').write_bytes(patch)
source_binding={'sourceRevision':head,'dirty':True,'workingTree':status,'sourceFilesSha256':source_files,
                'preparedPatch':relative(here/'Hexalith.Builds.prepared.patch'),'preparedPatchSha256':sha(patch),
                'fixturePreparationSha256':file_hash(here/'fixture-preparation.json'),
                'note':'Uncommitted local preparation; the revision alone does not identify these bytes. No release provenance or acceptance.'}
write(here/'source-binding.json',source_binding)
(here/'final-proof.json').write_bytes((package/'qualification-evidence/packaged-seed-subscriptions.json').read_bytes())

members={}
for path in package.rglob('*'):
    if path.is_file(): members['diagnostic/'+path.relative_to(package).as_posix()]=path.read_bytes()
for name in source_files:members['source/'+name]=(builds/name).read_bytes()
for name in ['source-binding.json','Hexalith.Builds.prepared.patch','fixture-preparation.json']:
    members['source/'+name]=(here/name).read_bytes()
archive_path=here/'artifacts.tar.gz'
with archive_path.open('wb') as target:
    with gzip.GzipFile(fileobj=target,mode='wb',filename='',mtime=0) as compressed:
        with tarfile.open(fileobj=compressed,mode='w') as archive:
            for name,body in sorted(members.items()):
                info=tarfile.TarInfo(name);info.size=len(body);info.mode=0o644;info.mtime=0
                archive.addfile(info,io.BytesIO(body))
bundle={'path':relative(archive_path),'sha256':file_hash(archive_path),'sizeBytes':archive_path.stat().st_size,
        'membersSha256':{name:sha(body) for name,body in sorted(members.items())}}
write(here/'artifacts.manifest.json',bundle)

def attempt(name):
    item=json.loads((here/(name+'.command.json')).read_bytes())
    item['log']='corpus-repair/'+item['log']
    return item

def command(name):
    item=attempt(name)
    assert item['exitCode']==0,name
    return item

full=command('full-g4-verified')
observations=json.loads((here/'remote-observations.json').read_bytes())
read=lambda name:json.loads((here/(name+'.json')).read_bytes())
remote=copy.deepcopy(old['refreshedRemoteSource'])
remote.update({'observedAtUtc':observations['recordedAtUtc'],'buildsMain':read('builds-main'),'buildsMainCi':read('builds-main-ci'),
               'platformMain':read('platform-main'),'platformRemoteBuildsGitlink':read('platform-builds-pin')['sha'],
               'platformReleaseCount':len(read('platform-releases')),'platformTagCount':len(read('platform-tags')),
               'platformWorkflowCount':read('platform-workflows')['total_count'],
               'buildsAheadOfLocal':read('builds-upstream-comparison')['ahead_by'],
               'note':'Read-only GitHub and registry observations; no fetch, ref, gitlink, dependency or upstream source change.'})
record.update({'recordedAtUtc':datetime.now(timezone.utc).isoformat(),
               'scope':'Synthetic G-4 P0 acceptance-corpus pin repair and complete local source/package technical qualification; dirty preparation remains release-ineligible.',
               'source':source_binding,'finalProofPath':relative(here/'final-proof.json'),
               'refreshedRemoteSource':remote,'registryObservations':[dict(r,body='corpus-repair/'+r['body']) for r in observations['observations'] if 'url' in r],
               'retainedArtifactBundle':bundle})
packages=[]
for item in inventory['packages']:
    for role in ['nupkg','snupkg']:
        artifact=item[role];packages.append({'path':relative(package/artifact['file']),'sha256':artifact['sha256'].lower(),'sizeBytes':artifact['sizeBytes']})
record['localDiagnostic']={'observedVersionUsedOnlyInIsolatedLocalFeed':'4.31.2','representsPublishedBytes':False,
                           'packages':packages,'inventoryPath':relative(package/'g4-tool-package-inventory.json'),
                           'inventorySha256':file_hash(package/'g4-tool-package-inventory.json'),'qualification':inventory['qualification']}
record['verificationSummary'].update({'sourceGateEvidenceTests':{'passed':107,'failed':0,'skipped':0,'formerFailuresResolved':31},
                                      'sourceGateModuleTests':{'passed':811,'failed':0,'skipped':1},
                                      'sourceGateIntegrationTests':{'passed':1,'failed':0,'skipped':12},
                                      'preparationRegressionsPassed':7,'unchangedExactAcceptanceExpectations':37,
                                      'syntheticAcceptanceNegativesPassedPerMode':36,'completeQualificationEvidenceArtifacts':55,
                                      'packagedControls':'Complete source/package parity, P0 positive HXI210, all 36 exact negatives and both actual-package seed modes pass; complete inventory emitted; releaseEligible=false.',
                                      'existingSkips':'One Module checkout-dependent and 12 Integration live opt-in cases; no live/staging pass inferred.'})
record['selectedChecks'].update({'packageDiagnostic':full,'finalExactPackageProbe':full,
                                'artifactValidator':command('artifact-regression'),'publisherContracts':command('publisher-regression'),
                                'packageGateContracts':command('gate-regression'),'fullSourceGate':full,'fullPackagedControls':full,
                                'directAcceptancePositive':command('acceptance-positive'),'releaseBuild':full,
                                'sourceGuardContracts':command('release-source-controls'),'releaseLifecycleContracts':command('release-lifecycle-controls'),
                                'platformPins':command('platform-pin-guards'),'releaseSource':command('release-source-controls'),
                                'releaseLifecycle':command('release-lifecycle-controls'),'platformPinGuards':command('platform-pin-guards'),
                                'platformBuildsResolution':command('platform-builds-resolution')})
record['selectedChecks'].pop('directAcceptanceRefusal',None)
record['allAttemptsHistoricalNote']='Prior preparation attempts are historical; current full source/package results are selectedChecks and corpusRepair.'
record['preservation'].update({'platformCapturedInputsUnchangedThisRepair':len(platform),'syntheticExpectationFilesUnchanged':37,
                               'syntheticSampleApprovalsUnchanged':True,'nonAcceptanceFixturesUnchanged':True})
record['evidenceReuse']='All source suites and complete package workflow were rerun. Prior Platform behavior/assertion evidence, catalog/audit results and published 4.31.2 signed-negative evidence are retained only for unchanged input bytes, verified against both initial snapshots. New diagnostic archives have new outer hashes and the same seed payload; no skipped/live/staging result is promoted.'
record['refreshedRemoteSource'].update({'platformRemoteBuildsGitlinkAvailable':False,'platformRemoteBuildsGitlinkCommitHttpStatus':422,
    'platformRemoteBuildsGitlinkComparisonHttpStatus':404,'buildsMainFinalCheck':command('builds-main-final'),
    'platformRemotePinCommitCheck':attempt('platform-pin-commit-final'),
    'platformRemotePinComparisonCheck':attempt('platform-pin-comparison-final')})
record['remainingBlocker']='The observed remote Platform Builds pin 86b9f351db87b00504cc98e125b0951ef2f4fa67 is unavailable by direct GitHub query (HTTP 422; comparison 404). Local synthetic corpus and full technical G-4 preparation are resolved. Release remains blocked by dirty/uncommitted source and modified fixture provenance, reviewed reconciliation with concurrent upstream main, successful exact-head push CI, separate Git/publication authorization and a real released tool accepted by Platform with exact pin/range, coherent gitlink and owner declaration/attestation/intake prerequisites. No staging qualification or story closure.'
record['decision']['fullLocalTechnicalQualificationPassed']=True
record['decision']['releaseEligible']=False
record['previousQualification']={'receiptPath':relative(here/'qualification.before.json'),'receiptSha256':file_hash(here/'qualification.before.json'),
                                 'handoffPath':relative(here/'handoff.before.json'),'handoffSha256':file_hash(here/'handoff.before.json'),
                                 'retainedArtifactBundle':old['retainedArtifactBundle'],
                                 'note':'Historical blocked preparation, preserved unchanged; references describe the previous source snapshot.'}
record['corpusRepair']={'status':'resolved-local-preparation','beforeInputsPath':relative(here/'before-inputs.json'),
                       'fixturePreparationPath':relative(here/'fixture-preparation.json'),'fullQualification':full,
                       'preparationRegression':command('preparation-regression-verified'),
                       'determinismCheck':command('fixture-determinism'),'directAcceptancePositive':command('acceptance-positive'),
                       'releaseRefusal':command('diagnostic-release-refusal'),
                       'initialFullAttempt':attempt('full-g4'),
                       'focusedPrerequisiteRerun':command('prerequisite-focused'),
                       'review':{'layer':'blind-hunter','findingsPatched':6,'deferred':0,'remainingIntroducedBugs':0,
                                 'details':'Windows paths, partial binding drift, isolated stale pins, escaped pins, missing inputs, preparation tests.'}}
record['corpusRepair']['evidenceReview']={'findingsPatched':6,'deferred':0,'remainingInconsistencies':0,
    'resolutions':['Current handoff limits','Distinct unavailable remote Platform pin','Current selected source/package results','Document digests','Historical deferred evidence wording','Copied command/body paths']}
record['repositoryFilesSha256']={name:file_hash(builds/name) for name in old['repositoryFilesSha256']}
for name in source_files:record['repositoryFilesSha256'][name]=file_hash(builds/name)
for path in here.rglob('*'):
    if path.is_file() and '__pycache__' not in path.parts and not path.name.startswith(('evidence-verification','evidence-negative')):
        record['repositoryFilesSha256'][relative(path)]=file_hash(path)
write(evidence/'qualification.json',record)
print(json.dumps({'bundleSha256':bundle['sha256'],'bundleMembers':len(members),'inventorySha256':record['localDiagnostic']['inventorySha256'],
                  'qualificationSha256':file_hash(evidence/'qualification.json'),'packages':packages},indent=2))

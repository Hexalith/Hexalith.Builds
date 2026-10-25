from pathlib import Path
import datetime, hashlib, json, subprocess, zipfile
out=Path('/tmp/module-cli-release-20261025-2gbedle5/resumed-evidence-path.txt').read_text().strip()
out=Path(out)
repo='Hexalith/Hexalith.Builds'
run_id='40193969210'
source='e171c5a788eab183b32683431b084198c9b8d373'
stamp=datetime.datetime.now(datetime.timezone.utc).strftime('%Y%m%dT%H%M%SZ')

def retain(name,args):
    start=datetime.datetime.now(datetime.timezone.utc).isoformat()
    p=subprocess.run(args,capture_output=True)
    (out/(name+'.stdout.bin')).write_bytes(p.stdout)
    (out/(name+'.stderr.txt')).write_bytes(p.stderr)
    (out/(name+'.command.json')).write_text(json.dumps(dict(command=args,observedAtUtc=start,exitCode=p.returncode,stdoutSha256=hashlib.sha256(p.stdout).hexdigest(),stderrSha256=hashlib.sha256(p.stderr).hexdigest()),indent=2)+'\n')
    if p.returncode: raise RuntimeError(f'{name} exited {p.returncode}')
    return p.stdout
raw=retain('completed-active-release', ['gh','api',f'repos/{repo}/actions/runs/{run_id}'])
run=json.loads(raw)
assert run['head_sha']==source and run['status']=='completed' and run['conclusion']=='success', (run['head_sha'],run['status'],run['conclusion'])
retain('completed-active-release-jobs',['gh','api',f'repos/{repo}/actions/runs/{run_id}/jobs?per_page=100'])
logs=retain('completed-active-release-logs',['gh','api',f'repos/{repo}/actions/runs/{run_id}/logs'])
archive=out/'completed-active-release-logs.zip'
archive.write_bytes(logs)
with zipfile.ZipFile(archive) as z:
    combined='\n'.join(z.read(n).decode(errors='replace') for n in z.namelist())
    (out/'completed-active-release-native-lines.txt').write_text('\n'.join(line for line in combined.splitlines() if any(term in line for term in ['Test summary:','total:','succeeded:','failed:','skipped:','dotnet test ','Verified remote','Published release','Created tag','next release version is','sourceValidation','releaseEligible']))+'\n')
retain('completed-release-main-ref',['gh','api',f'repos/{repo}/git/ref/heads/main'])
releases=json.loads(retain('completed-release-list',['gh','api',f'repos/{repo}/releases?per_page=20']))
candidates=[r for r in releases if not r['draft'] and r['published_at']>=run['created_at']]
assert len(candidates)==1, [(r['tag_name'],r['published_at']) for r in candidates]
release=candidates[0]
tag=release['tag_name']
version=tag.removeprefix('v')
ref=json.loads(retain('published-release-tag-ref',['gh','api',f'repos/{repo}/git/ref/tags/{tag}']))
obj=ref['object']
if obj['type']=='tag':
    obj=json.loads(retain('published-release-tag-object',['gh','api',obj['url']]))['object']
assert obj['type']=='commit' and obj['sha']==source, obj
assets=[a for a in release['assets'] if a['name']=='g4-tool-package-inventory.json']
assert len(assets)==1
inventory=retain('published-g4-tool-package-inventory',['gh','api','-H','Accept: application/octet-stream',f"repos/{repo}/releases/assets/{assets[0]['id']}"])
(out/'g4-tool-package-inventory.json').write_bytes(inventory)
inv=json.loads(inventory)
assert inv['version']==version and inv['configuration']=='Release'
q=inv['qualification']
assert q['releaseEligible'] is True
assert q['sourceTree']['revision']==source and q['sourceTree']['clean'] is True
assert q['sourceValidation']==dict(mode='executed',result='passed')
assert q['controls']==dict(mode='executed',result='passed')
assert q['packageBuild']==dict(mode='official',result='passed')
assert q['fixtures']['mode']=='repository-tracked' and q['fixtures']['root']=='test/fixtures'
assert {p['id'] for p in inv['packages']}=={'Hexalith.Builds.Module.Cli','Hexalith.Builds.Evidence.Cli'} and len(inv['packages'])==2
artifacts=[p[k] for p in inv['packages'] for k in ['nupkg','snupkg']]
assert len({p['file'] for p in artifacts})==4
builds=Path('/home/administrator/projects/hexalith/conversations/references/Hexalith.Builds')
for row in q['fixtures']['files']:
    data=subprocess.check_output(['git','show',f"{source}:test/fixtures/{row['file']}"],cwd=builds)
    assert hashlib.sha256(data).hexdigest().upper()==row['sha256'].upper()
    assert len(data)==row['sizeBytes']
identity=dict(observedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat(),runId=int(run_id),runUrl=run['html_url'],releaseSha=source,tag=tag,version=version,releaseUrl=release['html_url'],inventoryAsset=assets[0],inventorySha256=hashlib.sha256(inventory).hexdigest(),fullQualification=q,qualifiedArtifacts=artifacts,verifiedFixtureCount=len(q['fixtures']['files']),fixtureSourceBindingVerified=True)
(out/'published-release-identity.json').write_text(json.dumps(identity,indent=2)+'\n')
print(json.dumps(dict(version=version,tag=tag,releaseSha=source,inventorySha256=identity['inventorySha256'],fixtureCount=identity['verifiedFixtureCount']),indent=2))

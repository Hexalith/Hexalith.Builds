#!/usr/bin/env python3
"""Read-only GitHub, NuGet and OCI observations; retain no credentials."""
import base64
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import subprocess
from urllib.request import Request, urlopen
from urllib.error import HTTPError

root=Path(__file__).resolve().parent
now=lambda:datetime.now(timezone.utc).isoformat()
observations=[]

def github(item):
    name,endpoint,query=item
    argv=['gh','api',endpoint]
    if query:argv+=['--jq',query]
    started=now()
    result=subprocess.run(argv,stdout=subprocess.PIPE,stderr=subprocess.PIPE)
    body=result.stdout
    path=root/(name+'.json')
    path.write_bytes(body)
    return {'name':name,'method':'GET','endpoint':endpoint,'query':query,'startedAtUtc':started,'finishedAtUtc':now(),
            'exitCode':result.returncode,'status':'observed' if result.returncode==0 else 'unavailable',
            'stderr':result.stderr.decode(),'body':path.name,'sha256':hashlib.sha256(body).hexdigest()}

first=[('builds-main','repos/Hexalith/Hexalith.Builds/commits/main','{sha,date:.commit.committer.date,html_url}'),
       ('platform-main','repos/Hexalith/Hexalith.Platform/commits/main','{sha,date:.commit.committer.date,html_url}'),
       ('builds-release','repos/Hexalith/Hexalith.Builds/releases/latest','{tag_name,published_at,html_url,assets:[.assets[]|{name,url: .browser_download_url}]}'),
       ('builds-release-commit','repos/Hexalith/Hexalith.Builds/commits/v4.31.2','{sha,html_url}'),
       ('platform-releases','repos/Hexalith/Hexalith.Platform/releases?per_page=100','.'),
       ('platform-tags','repos/Hexalith/Hexalith.Platform/tags?per_page=100','.'),
       ('platform-workflows','repos/Hexalith/Hexalith.Platform/actions/workflows','{total_count,workflows:[.workflows[]|{id,name,path,state}]}')]
with ThreadPoolExecutor(max_workers=4) as pool: observations.extend(pool.map(github,first))
builds=json.loads((root/'builds-main.json').read_bytes())['sha']
platform=json.loads((root/'platform-main.json').read_bytes())['sha']
second=[('builds-main-ci',f'repos/Hexalith/Hexalith.Builds/actions/workflows/ci.yml/runs?head_sha={builds}&event=push&per_page=100','{total_count,runs:[.workflow_runs[]|{id,head_sha,head_branch,event,status,conclusion,html_url}]}'),
        ('builds-upstream-comparison',f'repos/Hexalith/Hexalith.Builds/compare/728174f506d56204657e0c52e98a4453aef79f6a...{builds}','{status,ahead_by,behind_by,commits:[.commits[]|{sha,message:.commit.message}],files:[.files[]|{filename,status,sha,patch}]}'),
        ('platform-builds-pin',f'repos/Hexalith/Hexalith.Platform/contents/references/Hexalith.Builds?ref={platform}','{sha,type,submodule_git_url}'),
        ('platform-retained-pin','repos/Hexalith/Hexalith.Builds/commits/5636b7f36e28e2731111b049f974b1fa951d1c36','{sha,html_url}')]
for module,repo,tag in [('eventstore','EventStore','v3.122.0'),('tenants','Tenants','v5.8.0'),('memories','Memories','v2.27.1')]:
    second.append((module+'-release-commit',f'repos/Hexalith/Hexalith.{repo}/commits/{tag}','{sha,html_url}'))
    second.append((module+'-release-declaration',f'repos/Hexalith/Hexalith.{repo}/contents/module/hexalith.module-manifest.v2.json?ref={tag}','{sha,size,encoding,content}'))
with ThreadPoolExecutor(max_workers=4) as pool: observations.extend(pool.map(github,second))

config=json.loads((Path.home()/'.docker/config.json').read_bytes())
registry='registry.hexalith.com'
auth=config.get('auths',{}).get(registry,{}).get('auth')
if not auth:
    helper=config.get('credHelpers',{}).get(registry,config.get('credsStore'))
    result=subprocess.run(['docker-credential-'+helper,'get'],input=(registry+'\n').encode(),stdout=subprocess.PIPE,stderr=subprocess.PIPE,check=True)
    credential=json.loads(result.stdout)
    auth=base64.b64encode((credential['Username']+':'+credential['Secret']).encode()).decode()

def request(name,url,authenticated=False):
    started=now()
    headers={'Accept':'application/vnd.oci.image.index.v1+json, application/vnd.oci.image.manifest.v1+json, application/vnd.docker.distribution.manifest.v2+json'} if authenticated else {}
    if authenticated:headers['Authorization']='Basic '+auth
    try:
        with urlopen(Request(url,headers=headers),timeout=45) as response:
            body=response.read();status=response.status;digest=response.headers.get('Docker-Content-Digest')
    except HTTPError as error:
        body=error.read();status=error.code;digest=None
    path=root/(name+'.json');path.write_bytes(body)
    row={'name':name,'method':'GET','url':url,'startedAtUtc':started,'finishedAtUtc':now(),'httpStatus':status,
         'digest':digest,'sha256':hashlib.sha256(body).hexdigest(),'body':path.name}
    if authenticated:row['credentialSource']='existing Docker configuration/helper; no credential retained'
    observations.append(row)
    return row

request('nuget-module-index','https://api.nuget.org/v3-flatcontainer/hexalith.builds.module.cli/index.json')
request('nuget-evidence-index','https://api.nuget.org/v3-flatcontainer/hexalith.builds.evidence.cli/index.json')
for module,version in [('eventstore','3.122.0'),('tenants','5.8.0'),('memories','2.27.1')]:
    manifest=request('registry-'+module+'-manifest',f'https://{registry}/v2/{module}/manifests/{version}',True)
    if manifest['httpStatus']==200:
        digest=manifest['digest'] or 'sha256:'+manifest['sha256']
        request('registry-'+module+'-referrers',f'https://{registry}/v2/{module}/referrers/{digest}',True)
request('registry-catalog',f'https://{registry}/v2/_catalog',True)
(root/'remote-observations.json').write_text(json.dumps({'recordedAtUtc':now(),'observations':observations},indent=2)+'\n')
print(f'Retained {len(observations)} read-only observations; Builds main {builds}, Platform main {platform}.')

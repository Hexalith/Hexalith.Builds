"""Temporary hosted verification; removed before final story delivery."""
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time
from urllib.request import urlopen
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / 'Github/lib'))
import ci_pipeline as pipeline

MODULE = ROOT / 'stage4-probe'


def prepare():
    MODULE.mkdir()
    (MODULE / '.github').mkdir()
    (MODULE / 'global.json').write_bytes((ROOT / 'global.json').read_bytes())
    (MODULE / 'Directory.Build.props').write_text('<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally></PropertyGroup></Project>')
    (MODULE / 'Directory.Build.targets').write_text('<Project />')
    version = next(x.attrib['Version'] for x in ET.parse(ROOT / 'Props/Directory.Packages.props').iter('PackageVersion') if x.attrib.get('Include') == 'Microsoft.Playwright')
    (MODULE / 'Directory.Packages.props').write_text(f'<Project><ItemGroup><PackageVersion Include="Microsoft.Playwright" Version="{version}" /></ItemGroup></Project>')
    (MODULE / 'Probe.csproj').write_text('<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType></PropertyGroup><ItemGroup><PackageReference Include="Microsoft.Playwright" /></ItemGroup></Project>')
    (MODULE / 'Program.cs').write_text('System.Console.WriteLine("stage4-probe");')
    (MODULE / 'Probe.slnx').write_text('<Solution><Project Path="Probe.csproj" /></Solution>')
    config = {'schema':'hexalith.ci.v1','buildsTag':'v4.46.0','solution':'Probe.slnx','dapr':True,'browsers':True,'registry':True,'codeql':[],'tests':{'integration':[],'exclude':{}},'hooks':[],'jobs':{},'submodules':[]}
    (MODULE / '.github/hexalith-ci.json').write_text(json.dumps(config))
    prepared = pipeline.prepare_config(ROOT, repository='Hexalith/Hexalith.Builds', module_root='stage4-probe')
    pipeline.restore_and_build(prepared)
    for hook, phase in [('after-unit-tests','verify'),('after-integration-tests','complete')]:
        folder=ROOT / '.github/actions' / hook
        folder.mkdir(parents=True)
        (folder/'action.yml').write_text(f'name: Probe\ndescription: Temporary runtime observation\nruns:\n  using: composite\n  steps:\n    - shell: bash\n      run: python3 Tools/ci/stage4_runtime_probe.py {phase}\n')


def verify():
    version = subprocess.check_output(['dapr','--version'],text=True)
    print(version,flush=True)
    assert '1.19.1' in version
    assert (Path.home()/'.dapr/bin/daprd').is_file()
    process = subprocess.Popen(['dapr','run','--app-id','stage4-probe','--dapr-http-port','3517','--','python3','-c','import time; time.sleep(90)'],start_new_session=True)
    try:
        deadline=time.monotonic()+60
        while True:
            try:
                with urlopen('http://127.0.0.1:3517/v1.0/healthz',timeout=2) as response:
                    assert response.status==204
                break
            except Exception:
                if process.poll() is not None or time.monotonic()>deadline: raise
                time.sleep(1)
    finally:
        os.killpg(process.pid,signal.SIGTERM)
        process.wait(timeout=30)
    browsers=list((Path.home()/'.cache/ms-playwright').glob('chromium-*/chrome-linux*/chrome'))
    assert browsers, 'installed Chromium missing'
    page=MODULE/'probe.html'
    page.write_text('<html><body>stage4-browser-ready</body></html>')
    result=subprocess.run([str(browsers[0]),'--headless','--no-sandbox','--disable-gpu','--dump-dom',page.as_uri()],text=True,capture_output=True,check=True,timeout=30)
    assert 'stage4-browser-ready' in result.stdout
    assert 'registry.hexalith.com' in json.loads((Path.home()/'.docker/config.json').read_text()).get('auths',{})
    (MODULE/'runtime-verified').touch()
    print('STAGE4-LIVE-ENVIRONMENT-PASSED: Dapr health, Chromium DOM, registry login',flush=True)


if sys.argv[1]=='prepare': prepare()
elif sys.argv[1]=='verify': verify()
elif sys.argv[1]=='complete':
    assert (MODULE/'runtime-verified').is_file()
    print('STAGE4-LIVE-HOOKS-PASSED',flush=True)

"""Retain fresh feed, schema, unchanged declaration and malformed-key probes."""
import argparse
import base64
import datetime
import gzip
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import urllib.request
import xml.etree.ElementTree as ET
import zipfile


def sha(data):
    return hashlib.sha256(data).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--builds', type=Path, required=True)
    parser.add_argument('--conversations', type=Path, required=True)
    parser.add_argument('--version', required=True)
    parser.add_argument('--release-sha', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--inventory', type=Path, required=True)
    args = parser.parse_args()
    builds = args.builds.resolve()
    conversations = args.conversations.resolve()
    out = args.output.resolve()
    out.mkdir(exist_ok=False)
    work = Path(tempfile.mkdtemp(prefix='module-cli-published-20261025-'))
    env = dict(os.environ, NUGET_PACKAGES=str(work / 'cache'),
               NUGET_HTTP_CACHE_PATH=str(work / 'http-cache'),
               DOTNET_CLI_HOME=str(work / 'dotnet-home'),
               DOTNET_NOLOGO='1', DOTNET_CLI_TELEMETRY_OPTOUT='1')
    overrides = {key: env[key] for key in ['NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH',
                  'DOTNET_CLI_HOME', 'DOTNET_NOLOGO', 'DOTNET_CLI_TELEMETRY_OPTOUT']}
    commands = []
    (out / 'environment.json').write_text(json.dumps(overrides, indent=2) + '\n')

    def run(name, command, cwd=builds, expected=0):
        started = datetime.datetime.now(datetime.timezone.utc).isoformat()
        result = subprocess.run([str(x) for x in command], cwd=cwd, env=env,
                                capture_output=True, timeout=300)
        (out / f'{name}.stdout.txt').write_bytes(result.stdout)
        (out / f'{name}.stderr.txt').write_bytes(result.stderr)
        commands.append(dict(name=name, command=[str(x) for x in command], cwd=str(cwd),
                             observedAtUtc=started, exitCode=result.returncode,
                             stdoutSha256=sha(result.stdout), stderrSha256=sha(result.stderr)))
        (out / 'commands.json').write_text(json.dumps(commands, indent=2) + '\n')
        if result.returncode != expected:
            raise RuntimeError(f'{name}: exit {result.returncode}; expected {expected}')
        return result

    def download(name, url):
        with urllib.request.urlopen(url, timeout=120) as response:
            data = response.read()
            headers = dict(response.headers)
        (out / name).write_bytes(data)
        (out / f'{name}.http.json').write_text(json.dumps(dict(url=url, headers=headers,
            sha256=sha(data), observedAtUtc=datetime.datetime.now(datetime.timezone.utc).isoformat()), indent=2) + '\n')
        return gzip.decompress(data) if data[:2] == b'\x1f\x8b' else data

    manifest = conversations / 'module/hexalith.module-manifest.v2.json'
    original = manifest.read_bytes()
    config = work / 'NuGet.Config'
    config.write_text('<?xml version="1.0" encoding="utf-8"?>\n<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>\n')
    shutil.copyfile(config, out / 'NuGet.Config')
    run('feed-tool-install', ['dotnet', 'tool', 'install', 'Hexalith.Builds.Module.Cli',
        '--version', args.version, '--tool-path', work / 'tool', '--configfile', config, '--no-cache'])
    tool = work / 'tool/hexalith-module'
    version = run('feed-tool-version', [tool, '--version']).stdout.decode().strip()
    assert version == f'{args.version}+{args.release_sha}', version
    packages = list(work.rglob(f'hexalith.builds.module.cli.{args.version}.nupkg'))
    assert packages, 'Fresh installation retained no package archive'
    assert len({sha(p.read_bytes()) for p in packages}) == 1
    package = packages[0]
    raw = package.read_bytes()
    run('feed-signature-verification', ['dotnet', 'nuget', 'verify', package, '--all'])
    lower = f'hexalith.builds.module.cli/{args.version}'
    download('feed-service-index.json', 'https://api.nuget.org/v3/index.json')
    registration = json.loads(download('feed-registration.json.gz', f'https://api.nuget.org/v3/registration5-gz-semver2/{lower}.json'))
    feed_catalog = json.loads(download('feed-catalog.json', registration['catalogEntry']))
    assert feed_catalog['packageHashAlgorithm'] == 'SHA512'
    feed_hash = feed_catalog['packageHash']
    assert feed_hash == base64.b64encode(hashlib.sha512(raw).digest()).decode()
    assert feed_catalog['packageSize'] == len(raw)
    nuspec_bytes = download('feed.nuspec', f'https://api.nuget.org/v3-flatcontainer/{lower}/hexalith.builds.module.cli.nuspec')
    nuspec = ET.fromstring(nuspec_bytes)
    repository = next(x for x in nuspec.iter() if x.tag.endswith('repository')).attrib
    assert repository['commit'] == args.release_sha, repository
    with zipfile.ZipFile(package) as archive:
        assert '.signature.p7s' in archive.namelist()
        prefix = 'tools/net10.0/any/'
        dll_bytes = archive.read(prefix + 'Hexalith.Builds.Tooling.dll')
        dll = work / 'Hexalith.Builds.Tooling.dll'
        dll.write_bytes(dll_bytes)
        archive_nuspec = archive.read('Hexalith.Builds.Module.Cli.nuspec')
        assert archive_nuspec == nuspec_bytes
        payload_entries = {name: sha(archive.read(name)) for name in sorted(archive.namelist())
                           if name != '.signature.p7s'}
        (out / 'published-payload-entries.json').write_text(json.dumps(payload_entries, indent=2) + '\n')
        inspect = Path(__file__).with_name('inspect-published-resources.ps1')
        resources = run('embedded-resource-identity', ['pwsh', '-NoProfile', '-File', inspect,
                    '-AssemblyPath', dll, '-OutputDirectory', out / 'embedded']).stdout
        embedded = json.loads(resources)
        assert embedded['informationalVersion'] == f'{args.version}+{args.release_sha}'
        schema_rows = []
        for name in ['hexalith.module-manifest.v2.json', 'hexalith.test-environment.v1.json', 'hexalith.test-result.v1.json']:
            loose = archive.read(prefix + 'schemas/' + name)
            (out / name).write_bytes(loose)
            matching = [x for x in embedded['resources'] if x['name'].endswith('.' + name)]
            assert len(matching) == 1
            source = subprocess.check_output(['git', 'show', f'{args.release_sha}:schemas/{name}'], cwd=builds)
            assert sha(loose) == matching[0]['sha256'] == sha(source)
            schema_rows.append(dict(schema=name, sourceSha256=sha(source), looseSha256=sha(loose),
                                    embeddedSha256=matching[0]['sha256'], allEqual=True))
        catalog_name = 'Hexalith.Builds.Tooling.Manifest.platform-version-catalog.json'
        catalog = json.loads((out / 'embedded' / catalog_name).read_bytes())
    installed_dlls = list((work / 'tool/.store').rglob('Hexalith.Builds.Tooling.dll'))
    assert installed_dlls and all(sha(x.read_bytes()) == sha(dll_bytes) for x in installed_dlls)
    inventory = json.loads(args.inventory.read_bytes())
    assert inventory['version'] == args.version
    assert inventory['qualification']['releaseEligible'] is True
    assert inventory['qualification']['sourceTree']['clean'] is True
    assert inventory['qualification']['sourceTree']['revision'] == args.release_sha
    for lane in ['sourceValidation', 'controls']:
        assert inventory['qualification'][lane] == dict(mode='executed', result='passed')
    assert inventory['qualification']['packageBuild'] == dict(mode='official', result='passed')
    source_catalog = subprocess.check_output(['git', 'show', f'{args.release_sha}:Props/Directory.Packages.props'], cwd=builds)
    (out / 'source-Directory.Packages.props').write_bytes(source_catalog)
    identity = dict(version=args.version, releaseSha=args.release_sha, toolVersion=version,
        feed='https://api.nuget.org/v3/index.json', pathAtMeasurement=str(package),
        packageSha256=sha(raw), packageSha512Base64=feed_hash, sizeBytes=len(raw),
        signed=True, signatureVerificationExitCode=0, repository=repository,
        registration=registration, feedCatalog=feed_catalog,
        toolingAssemblySha256=sha(dll_bytes), installedAssembliesMatchPackage=True,
        informationalVersion=embedded['informationalVersion'], schemas=schema_rows,
        embeddedCatalog=catalog, embeddedCatalogSha256=sha((out/'embedded'/catalog_name).read_bytes()),
        sourcePackageCatalogSha256=sha(source_catalog), qualifiedInventorySha256=sha(args.inventory.read_bytes()),
        inventoryPackage=[x for x in inventory['packages'] if x['id']=='Hexalith.Builds.Module.Cli'][0],
        signingBoundary='NuGet.org adds repository signing; the governed publisher log must bind its signed remote hash to the qualified unsigned payload.')
    (out / 'identity.json').write_text(json.dumps(identity, indent=2) + '\n')
    ordinary = {}
    adoption = {}
    for format_name in ['human', 'json']:
        ordinary[format_name] = run(f'ordinary-{format_name}', [tool, 'validate', '--manifest',
            'module/hexalith.module-manifest.v2.json', '--output', format_name], conversations)
        adoption[format_name] = run(f'adoption-{format_name}', [tool, 'validate', '--manifest',
            'module/hexalith.module-manifest.v2.json', '--adoption', '--output', format_name], conversations, expected=1)
    report = json.loads(ordinary['json'].stdout)
    assert report['status'] == 'validated' and report['diagnostics'] == []
    assert 'validated' in ordinary['human'].stdout.decode()
    report = json.loads(adoption['json'].stdout)
    prior = json.loads((builds / 'evidence/g4/module-configuration-keys-20261025/candidate-adoption-json.stdout.txt').read_bytes())
    predecessor_equal = report['adoption'] == prior['adoption']
    adoption_differences = dict(
        removed=[row for row in prior['adoption'] if row not in report['adoption']],
        added=[row for row in report['adoption'] if row not in prior['adoption']])
    (out / 'adoption-comparison.json').write_text(json.dumps(dict(
        predecessorRowsEqual=predecessor_equal, differences=adoption_differences,
        currentRows=report['adoption'], predecessorRows=prior['adoption']), indent=2) + '\n')
    human_rows = []
    for line in adoption['human'].stdout.decode().splitlines():
        match = re.match(r'^(PASS|FAIL) (\S+) file=(.*?): (.*)$', line)
        if match:
            human_rows.append(dict(requirement=match[2], passed=match[1]=='PASS', file=match[3], reason=match[4]))
    human_json_equal = human_rows == report['adoption']
    (out / 'human-adoption-rows.json').write_text(json.dumps(human_rows, indent=2) + '\n')
    invalid_keys = ['', ' ', '1TOKEN', '_TOKEN', 'TOKEN_', 'APP__API_TOKEN', 'App::Token', 'APP_API_TOKEN\n', 'A'*257]
    controls = []
    for field_index, field in enumerate(['modules[0].integration.secrets[1].configurationKey',
                                      'modules[0].runtime.extensions[0].configurationInputs[12].key']):
        for key_index, value in enumerate(invalid_keys):
            data = json.loads(original)
            if field_index == 0:
                data['modules'][0]['integration']['secrets'][1]['configurationKey'] = value
            else:
                data['modules'][0]['runtime']['extensions'][0]['configurationInputs'][12]['key'] = value
            name = f'invalid-{field_index}-{key_index}'
            input_path = work / f'{name}.json'
            input_path.write_text(json.dumps(data, indent=2) + '\n')
            before = sha(input_path.read_bytes())
            for format_name in ['human', 'json']:
                result = run(f'{name}-{format_name}', [tool, 'validate', '--manifest', input_path,
                             '--output', format_name], conversations, expected=1)
                if format_name == 'json':
                    negative = json.loads(result.stdout)
                    assert negative['status'] == 'failed'
                    assert len(negative['diagnostics']) == 1
                    diagnostic = negative['diagnostics'][0]
                    assert diagnostic['ruleId'] == 'HXP002' and diagnostic['field'] == field
                    assert diagnostic['source'] == str(input_path)
                else:
                    lines = [line for line in result.stdout.decode().splitlines() if line.startswith('HXP')]
                    assert len(lines)==1 and lines[0].startswith('HXP002 ')
                    assert f'source={input_path} field={field}:' in lines[0]
                assert sha(input_path.read_bytes()) == before
                controls.append(dict(field=field, key=value, format=format_name, exitCode=1,
                                     ruleId='HXP002', inputSha256=before, unchanged=True))
    assert manifest.read_bytes() == original
    summary = dict(identity='identity.json', ordinary=dict(humanExit=0, jsonExit=0, diagnostics=[]),
        adoption=dict(humanExit=1,jsonExit=1, rows=report['adoption'],
            diagnostics=report['diagnostics'], humanJsonRowsEqual=human_json_equal,
            predecessorRowsEqual=predecessor_equal, differences=adoption_differences),
        malformedControls=controls, manifestSha256Before=sha(original), manifestSha256After=sha(manifest.read_bytes()),
        authority=dict(story16_1RuntimeHold='ACTIVE', runtimeEntryAllowed=False, deploymentPerformed=False,
                       platformAcceptanceClaimed=False, pinChanges=False, indexedGitlinkChanges=False))
    (out / 'results.json').write_text(json.dumps(summary, indent=2) + '\n')
    assert predecessor_equal and human_json_equal, 'Adoption difference requires triage; all malformed-key probes have been retained'
    print(json.dumps(dict(version=args.version, releaseSha=args.release_sha,
                         packageSha256=identity['packageSha256'], malformedProbes=len(controls),
                         adoptionRows=len(report['adoption']), output=str(out)), indent=2))


if __name__ == '__main__':
    main()

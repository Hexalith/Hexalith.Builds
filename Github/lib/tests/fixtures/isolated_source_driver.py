"""Actual SDK source integration; synthetic local Git identities are not release evidence."""
import json
import os
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, sys.argv[1])
import build_projects
import ci_config
import ci_pipeline
import module_preparation

data = json.loads(Path(sys.argv[2]).read_bytes())
root = Path(data["root"])
prepared = SimpleNamespace(root=root, document=json.loads((root / ".github/hexalith-ci.json").read_bytes()))
ci_config.validate_config(prepared.document, repository="Example/Fixture")
authority = json.loads(Path(data["authority"]).read_bytes())
workspace = Path(data["workspace"])
values = data["environment"]
environment = module_preparation.BuildEnvironment(workspace, values, authority,
    module_preparation._inputs(prepared, authority), module_preparation._files(workspace))
before = dict(environment.files)
with module_preparation.applied(prepared, authority, environment):
    build_projects.preflight(prepared)
    build_projects.build_declared(prepared, ci_pipeline.build_flags(), ci_pipeline.run_command)
    build_projects.build_declared(prepared, ci_pipeline.build_flags(), ci_pipeline.run_command)
assert module_preparation._files(workspace) == before
assert build_projects.VARIABLE not in os.environ
assert str(root) not in build_projects._ENVIRONMENTS
assert (root / "fixture/bin/Release/net11.0/Child.dll").is_file()
print("Source isolated declaration: real SDK guard/restore/build repeated; canonical DLL and immutable lifetime passed")

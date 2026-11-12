---
story: '4-1'
created: '2026-11-12'
status: ready-for-dev
planning_only: true
baseline_revision: 811b04fa570bf575d1cb42f8b661ea6c05c25f61
implementation_repository: references/Hexalith.Builds
implementation_baseline_revision: adfbf21896ef982ae9d1c3ee1db247e87bda22c0
implementation_integration_revision: 3c2f5944aaa28dc7854a00a8a9df171aa810b0c7
---

# Perimeter contract and library plan

Story 4-1 supplies the shared perimeter contract and comparison/application
library for CAP-4/8 and AD-4. This delivery contains planning documentation only.
The canonical story, with the complete intent contract, wire fields, Code Map,
edge-case matrix, tasks and acceptance criteria, lives in the enclosing CiCd
repository at
`_bmad-output/specs/spec-cicd/stories/4-1-perimeter-contract-and-library-in-builds.md`.
That story and architecture AD-4/10/19 govern later implementation.

## Implementation files

1. Add `schemas/hexalith.perimeter.v1.json`, draft 2020-12, closed root
   `schema: "hexalith.perimeter.v1"` and `entries`. Close each of nine entry kinds:
   `whole`, `whole-dir`, `gitlink`, `gitmodules-section`, `json-superset`,
   `marker-block`, `absent-glob`, `absent-json`, `absent-line`. The canonical
   story freezes the initial kind-specific fields and their semantics.
2. Add `Github/lib/perimeter_config.py`, the sole stdlib document loader, with
   byte/file interfaces, own-checkout schema identity, duplicate-key/non-JSON
   rejection and exact unsupported-schema/kind messages. Reuse the strict
   approach in [ci_config.py](../Github/lib/ci_config.py), preserving CI behavior.
3. Add `Github/lib/perimeter.py`, the single shared Git-backed engine. Callers
   provide Git snapshots, rendered-root prefix, trusted Builds tag repository
   and optional entry selection. Compare HEAD or index; apply current index
   only. Return deterministic path/kind/reason drift. No path lists, cloning,
   commits, pushing, releases or restore policy belong in this library.
4. Add `Github/lib/tests/fixtures/perimeter-contract.json`,
   `Github/lib/tests/test_perimeter_config.py` and
   `Github/lib/tests/test_perimeter.py`. Use the same valid/invalid corpus for
   JSON Schema and independent stdlib validation, plus temporary real Git
   repositories to prove comparison, repair and unchanged working-tree bytes.
5. Clarify the existing test-step name in
   [builds-ci.yml](../.github/workflows/builds-ci.yml), document the contract/API
   in [Tools/ci/README.md](../Tools/ci/README.md), and correct the sample's schema
   ownership wording in [Samples/Hexalith.Sample/README.md](../Samples/Hexalith.Sample/README.md).
   Existing discovery already runs the tests in both locked Python lanes;
   jsonschema 4.26.0 remains test-only. No new workflow job/dependency is needed.
6. Record later implementation/CI/release evidence in
   `docs/cicd-4-1-perimeter-contract-and-library-results.md`. Commit/push actual
   additive implementation as a commitlint-validated releasable `feat`, obtain
   exact-source green Builds CI, then perform the interactive release and
   acceptable-tag adoption described below.

## Required behavior

Enumerate and read module content from Git tree/index objects, never module
working-tree files. Read expected rendered whole content through its Git
snapshot too. Write blobs with `git hash-object -w --path=<path> --stdin`,
index additions with `git update-index --add --cacheinfo`, gitlinks as `160000`,
and deletions with `git rm --cached --sparse` on exact enumerated paths.
Preflight source/tag/document/type conflicts before mutation. Preserve
unrelated staged entries, regular modes and explicitly selected
`GIT_INDEX_FILE`. Do not copy the release adapter unchanged: it removes that
variable and has no binary stdin. Pin attributes to the selected index tree
using `GIT_ATTR_SOURCE` or equivalent so dirty `.gitattributes` is ignored.

Whole files compare blobs/modes without parsing even malformed module JSON.
Whole-directory ownership includes removal of extra tracked descendants.
Gitlinks resolve exact stable `buildsTag^{}` in the trusted Builds clone,
without checking out the module's submodule. Missing or malformed partial files
are drift and repair to only owned content; passing semantic partial entries
are never rewritten. Normalize CRLF for partial semantics, not whole blobs.

JSON superset requires deny values but retains extra denies, allow lists and
other keys. Gitmodules identifies a section by its `path`, keeps the existing
name, owns path/url, removes branch/update and duplicate sections for that
path, and retains unrelated keys/sections. Valid marker blocks retain outside
text; no markers appends the block, malformed/duplicate markers repair to only
the owned block. Absence entries remove only selected paths/keys/lines and pass
for missing targets. Malformed absence-only JSON repairs to `{}`.

Perimeter data describes the owned paths and forbidden data. It includes
semantic-release and its plugins plus commitlint pins in npm dependency
sections, root release/commitlint config keys, response/config/skill globs and
commitlint hook lines. `.github/actions/**` remains module-owned and untouched.
Git wildmatch `*` stops at `/`; `**/` handles zero or multiple directories.
Do not substitute Python fnmatch or default Git pathspec wildcard semantics.

Fixtures must cover all nine kinds, schema/loader parity and fixed diagnostics,
unknown/missing fields, unsafe paths, malformed whole/partial files,
preservation/idempotence, duplicate Gitmodules sections/markers, annotated tags,
regular modes/type conflicts, root/nested globs, literal path characters,
sparse/outside-cone operations, dirty/staged attributes, unrelated staged data,
caller-selected indexes and failures without index mutation. Observe actual
index/blob contents and unchanged working trees, without workflow-text asserts.

## Scope and continuity

The shared CI stage-1 guard and release drift seams remain story 5-1 work.
This story supplies their library/schema without enabling enforcement, replacing
the sample's placeholder perimeter, or retiring `commitlint-caller.yml`.
Rendering, delivery, restore jobs and `check.py` remain later integrations.
Allow callers to select entries so restore can later skip workflows and
lockstep entries; do not hard-code that policy here. Preserve AD-20 frozen
module-executed runtime and all unrelated package/catalog data.

CiCd's `builds-version` is already `v5.0.1`. Story 3-7 completed bootstrap with
immutable bot release `443097864` at source
`3c2f5944aaa28dc7854a00a8a9df171aa810b0c7`. Retained qualification draft
`441591623` is unmarked for production selection; preserve it and its history.
Earlier blocked bootstrap paragraphs are historical. Fresh readiness checks
remain necessary before story 4-1's later release.

The initial Builds checkout was
`adfbf21896ef982ae9d1c3ee1db247e87bda22c0`. Planning fetched and fast-forwarded
three already-published product/catalog commits to
`3c2f5944aaa28dc7854a00a8a9df171aa810b0c7`; those changes are upstream
integration, separate from this documentation and later library work.

## Later release and adoption

First perimeter-schema addition is a minor under AD-10; later new fields/kinds
require v2 and a Builds major. Accept the actual shared version calculation,
without hard-coding the next numeric tag.

After implementation is pushed and its exact-source `builds-ci.yml` is green,
an interactive CiCd session freshly checks publication prerequisites and runs:

```bash
gh workflow run builds-release.yml --repo Hexalith/Hexalith.Builds --ref main
```

CAP-15 forbids adding this dispatch to an unattended continuation. Use the
configured scoped CiCd-agent profile and existing shared release protocol.
Correlate full source/run identity, sole outcome, authenticated original
artifact/digest and eventual immutable bot release/tag/main ancestry. A
non-bootstrap `ready` outcome waits for nightly publish-ready under AD-19;
the bootstrap-only manual publication exception does not apply. Do not change
live settings or qualification resources to manufacture a release.

Only after acceptable publication does CiCd's root `builds-version` move to the
actual tag plus LF, with release evidence and the pushed Builds pointer. If
rendered outputs exist by then, render and commit them with that change (AD-5).
No module is delivered or bumped by story 4-1. An unattended implementation may
prepare code and verification, but must leave dispatch to an interactive CiCd
session and retain incomplete release/adoption acceptance until it happens.

## Verification and planning delivery

Implementation runs the focused `test_perimeter*.py` unittest discovery, both
locked interpreter suites through `Tools/ci/run_python_tests.py`, authenticated
online lint through `Tools/ci/lint_workflows.py`, and the committed-object API
check through `Tools/ci/check_public_api.py`. Hosted CI must retain and pass the
shared Builds/sample/Windows, catalog, quality and both Python lanes. The
canonical story records exact commands and acceptance criteria.

This invocation validates planning frontmatter, links, full Git revisions,
unchecked implementation tasks, documentation scope and whitespace, and
validates the exact commit message with pinned commitlint 21.2.4. Commit/push
this companion only in Builds, then stage the canonical story and pushed
gitlink in CiCd without a CiCd commit/push. Halt after planning. No runtime
implementation, test suite, release dispatch, publication or version bump runs.

Git implementation references: [wildmatch](https://git-scm.com/docs/gitignore),
[attribute source](https://git-scm.com/docs/git),
[hash-object](https://git-scm.com/docs/git-hash-object),
[cacheinfo](https://git-scm.com/docs/git-update-index), and
[cached/sparse removal](https://git-scm.com/docs/git-rm).

Planning readiness passed on 2026-11-12: all implementation tasks remain
unchecked; the canonical story records no unresolved intent gap. Frontmatter,
canonical Git baselines, local links, task/acceptance structure and whitespace
were validated. Runtime code, workflows, schema, version pin and release state
are unchanged by this planning delivery.

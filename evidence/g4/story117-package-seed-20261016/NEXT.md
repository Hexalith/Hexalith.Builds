# Story 1.17: synthetic corpus repaired; clean release prerequisite remains

The complete local G-4 source/package workflow now passes without a source skip
and with `-RequireControls`. All 31 former HXE202 failures are resolved. The
synthetic P0 positive and 36 negatives preserve their exact expectations in source
and installed-package modes. The actual-package configured/declarative
subscription gate passes. Synthetic records and Contract Sample Approver entries
remain fabricated validator inputs, never real acceptance evidence.

The new retained diagnostic is
`artifacts/story117-seed-20261016-corpus-repaired-verified`. Its complete inventory
records executed/passed source validation and controls, an official package
build, modified repository fixtures and dirty source, with `releaseEligible=false`.
The real pre-tag gate rejects it. It uses the observed version `4.31.2` solely in
an isolated local feed; these bytes must never be published under that version.
The preceding blocked receipt and archives remain historical under `corpus-repair/`
and the original `artifacts.tar.gz`; neither is relabeled as fully qualified.

From `references/Hexalith.Builds`, current reversible verification commands are:

```bash
python3 Tools/prepare-g4-synthetic-acceptance.py
python3 Tools/test-prepare-g4-synthetic-acceptance.py
python3 evidence/g4/story117-package-seed-20261016/verify-evidence.py --workspace ../..
```

The helper checks all four current supported pins, preserves deliberate off-pin
and bad-hash controls, and rejects ambiguous partial preparation before writing.
It does not advance the deliberately frozen EventStore 3.120.0 v1 module-manifest
contract, generic readiness fixtures, or package evidence assertions. Normal
regeneration of this synthetic corpus is explicitly selected with `--write`.

A fresh complete diagnostic rerun (new directory required) is:

```bash
env DOTNET_PROCESSOR_COUNT=2 pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 \
  -Version 4.31.2 \
  -PackageDirectory artifacts/story117-corpus-next-diagnostic \
  -RequireControls -RetainPackageDirectory
```

The reduced processor count limits test/build concurrency; it removes no check.
The first broad attempt's two transient fake-executable launch assertion failures
are retained. Their unchanged focused class passed 15/15 and the complete rerun
passed. The workflow retains 13 existing opt-in/checkout skips (one Module and 12
live Integration cases). These are not passing live, staging or acceptance evidence.

The remaining blocker is clean, reviewed and committed source reconciled with
concurrent upstream work, successful push CI for that exact canonical head, and
a separately authorized real release followed by Platform acceptance. Read-only
observations retain Builds main `d84d30f5a95135f534feecd33ae6b7173d40260c`, three
commits ahead of local `728174f506d56204657e0c52e98a4453aef79f6a`, with failed
exact-head push CI. Do not overwrite upstream Dapr binding/security and startup
readiness work. Platform has advanced independently; its observed remote Builds
pin is `86b9f351db87b00504cc98e125b0951ef2f4fa67`, distinct from that observed
Builds main. Its direct commit lookup returns HTTP 422 and comparison with Builds
main returns 404; do not materialize or accept an unresolvable pin. Local Platform still pins
`5636b7f36e28e2731111b049f974b1fa951d1c36`. Neither pin is tool acceptance.

Before any separately authorized Git work, inspect again:

```bash
git status --short --branch
git remote -v
git log --format='%H %s' --max-count=8
git diff --check
gh api repos/Hexalith/Hexalith.Builds/compare/728174f506d56204657e0c52e98a4453aef79f6a...main \
  --jq '{status,ahead_by,behind_by,commits:[.commits[].sha],files:[.files[].filename]}'
```

Separate authorization is required for fetch/integration/branching and owning-repo
staging, commit and push. Include the reviewed corpus/helper, existing seed/audit
and package-gate preparation, and evidence; validate the actual Conventional
Commit message with the pinned commitlint before use. Reconcile upstream in a
reviewable isolated checkout, preserve both changes, and rerun all affected gates
without skips on the clean committed source. This dirty inventory is never reused
as a release-eligible inventory.

Read the actual head and its CI after reconciliation/push:

```bash
release_sha=$(gh api repos/Hexalith/Hexalith.Builds/git/ref/heads/main --jq .object.sha)
gh api "repos/Hexalith/Hexalith.Builds/actions/workflows/ci.yml/runs?head_sha=${release_sha}&event=push&status=completed&per_page=100" \
  --jq '.workflow_runs[] | {head_sha,head_branch,event,status,conclusion,html_url}'
```

Only after successful current-head push CI and separate publication authorization:

```bash
gh workflow run build-release.yml --repo Hexalith/Hexalith.Builds --ref main
```

Semantic-release selects the real next version. Preserve its clean-source and
pre-tag gates. Observe the actual released commit/tag, qualified inventory,
signatures and signed archive hashes afterward; do not invent any of them.
Platform still needs a real released tool acceptance decision, exact tool pin and
range, coherent Builds gitlink, released owner declarations/attestations and intake
implementation. EventStore's selected release declaration remains available;
Tenants/Memories requests still return 404 and selected image referrers remain
empty. No Platform release/tag/workflow exists in the retained observation.

Publication, deployment/staging and production activation require separate
authorization and their prerequisites. Preserve `V1Only`, unset T0, all historical
approvals and Story 1.17 `backlog`/open. All 14 staging checks remain unexecuted.

# Module CLI APP_API_TOKEN compatibility

The accepted Conversations CLI **4.35.2** rejects the unchanged declaration's
`APP_API_TOKEN` keys with HXP002. A fresh local package from Builds source
**passes ordinary validation**, in human and JSON formats. This is a local
compatibility result; publication, pin changes and Platform qualification remain
separate authorized work. Story 16.1's runtime hold remains **ACTIVE**.

## Diagnosis

The published package identifies source
`4248b8c70cb35ba79fdd7e4dd63049d4c73211a0` in its NuSpec and assembly
informational version. Its loose manifest schema and the resource
`Hexalith.Builds.Tooling.Manifest.hexalith.module-manifest.v2.json` in
`Hexalith.Builds.Tooling.dll` have identical SHA-256
`888d719b58493b4ebe14975e96262eca87e00afee8fe3a451a83ac7da366293e`.
Their configuration-key pattern permits letters, digits and colon paths but no
underscores. The two rejected Conversations fields are:

- `modules[0].integration.secrets[1].configurationKey`
- `modules[0].runtime.extensions[0].configurationInputs[12].key`

Builds commit `72e48d9544bacc218a5fa8290fc3f68ffdea8974` already corrected
[`configurationKey`](../schemas/hexalith.module-manifest.v2.json), with source
schema, embedded-schema, public-command and adoption regression tests. It permits
single underscores between nonempty alphanumeric words in each colon-separated
segment. Each segment starts with an ASCII letter; whitespace, controls, empty
segments, leading/trailing/doubled underscores and keys above 256 characters
remain invalid. No second schema relaxation is required.

The Tooling project embeds that root schema at build time. The Module CLI packs
the loose schema and project-referenced Tooling DLL. The official builder restores
and builds the solution in Release, then packs both tools without `--no-build`.
The inspected 4.35.2 artifact is consistent with its older source revision;
there is no evidence that its packaging used a stale DLL.

The qualification gap was in
[`test-g4-tool-package-contracts.ps1`](../Tools/test-g4-tool-package-contracts.ps1):
it compared the loose manifest schema with source but checked only the embedded
test-environment/result schemas. Its installed-tool positive fixture contained no
underscores. The fix adds embedded manifest/source equality, a separate installed
APP_API_TOKEN fixture in both affected fields, and exact per-field HXP002 checks
for malformed keys. Existing shared fixtures and schema rules retain their bytes.
The gate's synthetic tests exercise missing embedding, rejected SDK keys,
incorrectly accepted malformed keys, wrong diagnostic fields and input mutation.

## Local verification

The additive [measurement record](../evidence/g4/module-configuration-keys-20261025/results.json)
binds package/source/input identities, command logs and preservation checks.
The additive [completion observation](../evidence/g4/module-configuration-keys-20261025/completion-results.json)
retains final review checks and discloses concurrent Git activity after measurement.
The local version is `0.0.0-app-api-token.20261025`; it deliberately provides no
Platform acceptance claim. Its informational revision describes the existing
source base, with the qualification-script working-tree changes recorded separately.

| Check | Result |
| --- | --- |
| Accepted 4.35.2, unchanged Conversations manifest | Exit 1; exactly the two original HXP002 fields |
| Official local package builder | Exit 0; Release build, zero warnings/errors; packaged loader check passes |
| Local package source/loose/embedded manifest schema | Equal SHA-256 `06d6804f3b111e803edd0d9142b03f36df344e26e2d71e50e2291fe15039daa4` |
| Installed candidate, ordinary validation, human/JSON | Exit 0; `validated`, no diagnostics |
| Installed qualification SDK controls | Both positive formats pass; all 18 individual malformed-key cases reject with HXP002 and exact source/field; inputs unchanged |
| Synthetic package gate | Exit 0; five injected refusals, all SDK control calls observed, 63 existing artifact-validator scenarios pass |
| Focused Debug schema, CLI and adoption regressions | Exit 0; 647 tests, zero failed/skipped/not-run |
| Installed candidate, adoption, human/JSON | Exit 1; 7 PASS, 17 FAIL of 24 rows |

The diagnostic qualification reuses the exact locally built packages through a
custom copy helper and skips broad source validation/runtime controls. Its
inventory explicitly has `releaseEligible=false`; it is not pre-tag release
qualification. The existing Builds seed HTTP/metadata probe runs locally; no
Conversations runtime, SM-2 demonstration or deployment runs.
Its qualification log records every malformed case's field, JSON-escaped value
and input hash. Embedded-schema hashes were reconfirmed by copying resource
streams directly, without text decoding. The compressed complete Conversations
baseline inventory and preservation comparison are retained with the results.

To repeat with fresh local outputs, run from Builds (choose a new temporary
directory; do not overwrite these saved observations):

```bash
measurement=$(mktemp -d /tmp/module-key-recheck-XXXXXX)
pwsh -NoProfile -File Tools/build-g4-tool-packages.ps1 \
  -Version 0.0.0-app-api-token.20261025 -OutputDirectory "$measurement/packages"
NUGET_PACKAGES="$measurement/cache" dotnet tool install Hexalith.Builds.Module.Cli \
  --version 0.0.0-app-api-token.20261025 --tool-path "$measurement/tool" \
  --add-source "$measurement/packages"
pwsh -NoProfile -File Tools/test-g4-tool-package-contract-gate.ps1
pwsh -NoProfile -File Tools/test-g4-tool-package-contracts.ps1 \
  -Version 0.0.0-app-api-token.20261025 \
  -PackageDirectory "$measurement/installed-controls" \
  -SkipSourceValidation -RetainPackageDirectory
```

Then, from Conversations, invoke that absolute `"$measurement/tool/hexalith-module"`
path with `validate --manifest module/hexalith.module-manifest.v2.json`, adding
`--output json`, `--adoption`, or both for the other observations. The original
[command log](../evidence/g4/module-configuration-keys-20261025/commands.json)
records exact measurement commands, exits and stream hashes. Temporary package
binaries are not checked in; rebuild them in a fresh directory. The retained
diagnostic inventory's two qualification-evidence entries are included beside it.
The last command builds another fresh package set and repeats the real installed
SDK/malformed-key checks; its skipped broad lanes still prevent release eligibility.
Saved helper/configuration files preserve the original measurement inputs and
temporary paths; use fresh paths for replay instead of executing those captured
inputs against the saved observation directory.

## Remaining adoption blockers

Only the declaration row changes from FAIL to PASS. All **fourteen dependency
selector** and **three Dapr** failures remain. The exact paths, reasons and
diagnostics survive in the [new JSON observation](../evidence/g4/module-configuration-keys-20261025/candidate-adoption-json.stdout.txt)
and [readable observation](../evidence/g4/module-configuration-keys-20261025/candidate-adoption-human.stdout.txt).
These match the seventeen non-declaration failures in the existing Conversations
`module/validation/epic-16-adoption-v2/` evidence. That package and
`_bmad-output/planning-artifacts/epic-16-adoption-v2/README.md` remain historical,
unchanged observations of the accepted 4.35.2 tool.

Static checks cover tracked supported sources only. Human review, owner-reviewed
entry, source/package/hosted parity, provider/identity and recovery qualification,
AD-5/6 obligations, Planning Tooling successors and human SM-2 remain pending.
Both frozen AppHosts and Story 16.1 draft/backlog status remain unchanged. Passing
ordinary validation grants no runtime entry, waiver or adoption completion.

## Release and Platform handoff

These are **future steps requiring authorization**, not actions performed here.

1. Review and commit the Builds fix and tests under its pinned commitlint policy.
   The existing schema correction landed in a `build` commit; ensure the authorized
   change has a semantic-release-recognized bug-fix release effect. Let
   semantic-release select the version rather than assigning a published version
   to these local bytes. Obtain successful push CI for the exact current branch
   SHA.
2. Manually dispatch [Builds Release](../.github/workflows/build-release.yml) for
   that exact current `main` SHA and approve its protected `production`
   environment. Push/merge alone does not release. The existing prerelease route
   uses its own branch/environment. Keep source-tip/CI checks intact.
3. The pre-tag path is `verify-g4-tool-release.ps1` ->
   `test-g4-tool-package-contracts.ps1 -RequireControls` ->
   `build-g4-tool-packages.ps1`. Require the complete source/control lanes,
   clean tracked fixture provenance and `releaseEligible=true`. Publish only
   through the existing hashed-inventory publisher. Do not substitute this
   diagnostic run, skip flags or a custom builder for that gate.
4. Restore the actual released artifact from its publication feed into a fresh
   isolated cache. Record its NuSpec/source/informational identity and package,
   Tooling DLL and embedded/loose schema hashes. Repeat ordinary Conversations
   validation unchanged, both output modes, and malformed-key controls. A local
   build cannot substitute for this released-artifact observation.
5. Hand the exact released version/content/catalog identity and those results to
   Platform. Its current declared range is `[4.35.2, 5.0.0-0)` and its README
   permits later 4.x releases under that policy; range eligibility does not
   authorize pin edits in this task or prove fresh composition qualification.
   Obtain the applicable owner decision and released-tool qualification. When an
   exact Platform pin is authorized to move, update its tool entry with disabled
   roll-forward and its Builds catalog gitlink to the release-tag commit; change
   the range only if required by that decision. Preserve unrelated tool entries.
6. After the direct Platform checkout's exact pin is approved and available,
   separately authorize Conversations to consume that tool entry using Platform's
   enrollment kit. Restore and record the exact released identity; rerun ordinary
   validation and both adoption modes against the intended reviewed/tracked
   candidate. Preserve old evidence and publish additive observations. Resolve
   the remaining failures and owner/runtime prerequisites through their own
   scopes before any Story 16.1 entry decision.

No release, deployment, Git staging/commit/push, accepted pin or gitlink update,
Platform acceptance assertion, or runtime-hold transition is part of this fix.

## Concurrent workspace activity

During final checks, a separate `/pushall` operation committed the tested Builds
scripts and part of these observations at
`6f3bafa4170c90ab3be1d5531995b44cbe9a3b5a`, then advanced ten Conversations
root gitlinks, including Builds, at
`cb7d8f2094c87493a717b3c2b883cdcae67c9224`. This session
performed no Git mutation and does not undo or authorize that separate activity.
All 4,206 pre-existing Conversations file contents still match the before
inventory. The unchanged-gitlink PASS describes the earlier measurement time;
the final workspace has separately advanced dependencies. The committed original
`results.json` is preserved byte-for-byte, with final facts recorded additively.
These commits provide no release, deployment, Platform acceptance or Story 16.1
runtime-entry credit.
The native validation/adoption results apply to their recorded source/dependency
snapshot; they do not certify the concurrently changed dependency snapshot.

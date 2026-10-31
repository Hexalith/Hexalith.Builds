# Producer qualification evidence handoff

The producer publication correction is prepared locally for review. Original
4.38.3 qualification remains **BLOCKED**. Exact content was recovered for 50 of
55 declared filenames; no authenticated original-run carrier was found. Five
files remain unavailable. No qualification evidence was regenerated.

The [recovery ledger](../evidence/g4/producer-publication-4383-20261031/recovery-ledger.json)
records every expected filename, SHA-256, size, observed bytes, target-tree
carrier path and Git blob, and the separate original-run provenance result.
Recovered bytes are under that packet's `matching-content/qualification-evidence/`.
These historical/fixture carriers establish content equality only.

## Original identity and trace

The input is Conversations
`module/validation/epic-16-published-artifact-qualification/attempt-20261031-4383-03/`.
The producer source and `v4.38.3` tag are both
`d1931b1e803144048d927fff16cb8119afde541e`.
The published inventory asset is ID `684839586`, exactly 74,432 bytes, SHA-256
`2421408b564af44ba75a83670aee147d21347d09ed5b823d091c24a6dbc29b5e`.
An authenticated fresh download equals the Conversations copy byte for byte.
The [observation packet](../evidence/g4/producer-publication-4383-20261031/)
retains run metadata, artifact listings, jobs, log excerpts, release assets and tag.
`retrieval-receipts.json` records authenticated read-only commands, UTC capture
times, exit results, response filenames, sizes and hashes; raw API responses
are retained under `observations/`.

| Stage | Generated and retained bytes | Published carrier |
| --- | --- | --- |
| CI `40669428503`, attempt 1, job `122752147580` | Qualification version `0.0.0-ci.521`; `/tmp/hexalith-builds-g4-packages-3edeea625f664c7b99360e445fac2ea4/`; inventory completed at `2026-10-28T10:36:04.5214560Z`; qualifier `finally` deletes directory without `-RetainPackageDirectory` | Actions artifact list empty |
| Release `40673566658`, attempt 1, job `122766264075`, preliminary gate | Version `0.0.0-ci.458`; `/tmp/hexalith-builds-g4-packages-f2ca591f2e344801ae06b3d4cc943f65/`; inventory completed at `2026-10-28T11:23:04.0290786Z`; deleted by the same cleanup | Actions artifact list empty; these are different-version bytes |
| Same Release, semantic-release actual version | `verify-g4-tool-release.ps1` invokes the qualifier with `-RetainPackageDirectory -RequireControls`; `/home/runner/work/Hexalith.Builds/Hexalith.Builds/artifacts/g4-tool-packages/4.38.3/`; inventory completed at `2026-10-28T11:40:50.0768725Z` | Only inventory JSON uploaded; no evidence asset, Actions upload, or workflow evidence cache |

At the exact source, `test-g4-tool-package-contracts.ps1` creates the
`qualification-evidence` directory, copies the positive fixture, saves packaged
command outputs and evidence, captures the subscription proof, saves each negative
control, then writes `qualification.log`. `Complete-QualificationInventory`
enumerates those retained files and hashes their bytes. The publisher verifies
all declared evidence locally before NuGet push. `package.json` configures
GitHub publication of the inventory alone. Runner-local retention therefore
does not make the declared bytes available to consumers. The logs establish
creation paths and completion; they do not contain recoverable byte-exact copies
of all missing files. No workflow cache retains these package directories.

## Recovery limits

The following inventory rows remain **BLOCKED**, with no matching bytes:

- `qualification-evidence/packaged-down-evidence.json`
- `qualification-evidence/packaged-seed-subscriptions.json`
- `qualification-evidence/packaged-test-evidence.json`
- `qualification-evidence/packaged-unavailable-evidence.json`
- `qualification-evidence/qualification.log`

Recovery inspected 14,832 local Builds Git objects, 34,283 matching-basename
files across the workspace and temporary directories, and 851 relevant ZIP
archives. No matching bytes for those five were found. This describes searched
storage, not proof that no owner-held backup exists. The other 50 rows match
retained source/historical bytes at the exact target tree. In particular,
`source-release-passed.json` is copied from a synthetic tracked fixture;
its synthetic revision and `0.0.0-contract` version are not release provenance.
All 55 original-run provenance results remain unverified or unavailable.

## Prepared correction

- CI uses a retained, ignored directory, archives completed evidence, and uploads
  the carrier, inventory and retained evidence with `if: always()` for 30 days.
- The actual-version release verifier creates
  `g4-tool-qualification-evidence.zip` before tagging. The archive contains the
  original inventory bytes, every declared evidence file at its inventory path,
  and `producer-run.json`. Existing qualifier and publisher checks still apply.
- Archive creation is atomic and preserves any existing carrier. The publisher
  rechecks the archive's exact member set, bytes and current source/run identity
  immediately before package publication; a missing or changed carrier blocks it.
- The archiver rejects missing/extra/duplicate/unsafe filenames, aliased files,
  hash or size mismatch, empty inventories and source/run/workflow mismatch.
  Release carriers require release eligibility; CI carriers retain their
  original eligibility state and identify themselves as CI.
- Semantic-release uploads the archive alongside the existing inventory asset.
  An always-run release step retains the same carrier and evidence in Actions
  for 90 days, including when publication fails after qualification. Partial
  raw output retained after a failed qualification grants no passing credit.
- Always-run attempt metadata records the producer step's outcome and run
  identity even when qualification fails. It explicitly grants no qualification
  credit. `GITHUB_JOB` records the workflow job key; the receiving reviewer maps
  it to the numeric job ID through authenticated run/jobs metadata and retains
  that metadata with the acceptance packet.
- No tool/dependency pins, release assets, accepted evidence, gitlinks or
  checkout revisions are changed. Existing `actions/upload-artifact` pin is reused.

## Remaining publication actions

These actions require separate authorization; none was executed here.

1. A producer custodian may supply an authenticated original carrier from the
   actual-version path above or an existing backup. Verify all 55 names, hashes
   and sizes against the preserved inventory, plus source SHA, version, run
   `40673566658`, attempt 1 and job identity. Historical byte matches alone do
   not establish this provenance. Publish any recovery additively after review;
   preserve the existing inventory and release assets.
2. If no complete original carrier can be supplied, review this correction as
   an **evidence successor**. Record the selected full committed successor SHA
   and obtain authorization for landing/pushing it and running its CI. The
   current checkout has other changes since the original source; their inclusion
   in a successor requires review. No committed successor SHA is claimed here.
3. Require successful push CI on that exact current `main` SHA. Download the
   artifact named `g4-ci-qualification-SHA-RUNID-ATTEMPT`; verify its source and
   workflow identity and all inventory rows. Its `0.0.0-ci.*` evidence cannot
   replace the actual-version release evidence.
4. Obtain separate release/publication authorization and the existing protected
   environment approval. Recheck that remote `main` still equals the reviewed
   successor SHA, then dispatch `build-release.yml` at `main`. Semantic-release
   must select a new version; do not rebuild/relabel it as original 4.38.3 or
   replace existing 4.38.3 assets. The source guard must pass before and after
   approval. If there is no new release version, the release owner must first
   resolve version eligibility through the normal reviewed release process.
5. Download the release asset `g4-tool-qualification-evidence.zip` and the
   inventory, and independently download Actions artifact
   `g4-release-qualification-SHA-RUNID-ATTEMPT`. Record download source, IDs and
   SHA-256 digests. Require the release and Actions ZIP bytes to agree. Require
   the embedded inventory to equal the standalone published inventory; verify
   every inventory filename, hash and size and the archive's exact member set
   (inventory + declared evidence + `producer-run.json`). Bind the version,
   clean source SHA, release run ID/attempt/job, workflow SHA/ref and tag. Verify
   published package identity through the existing consumer qualification.
6. Supply that separately reviewed successor packet to the Conversations and
   producer policy owners. Obtain their explicit successor acceptance before
   refreshing any candidate binding or tool pin. Keep the original 4.38.3
   attempt and its blockers intact. This producer fix grants no owner acceptance.

For authorized retrieval, use authenticated `gh run download RUNID --repo
Hexalith/Hexalith.Builds --name ARTIFACT_NAME --dir FRESH_DIRECTORY` and `gh
release download vVERSION --repo Hexalith/Hexalith.Builds --pattern
g4-tool-qualification-evidence.zip --pattern g4-tool-package-inventory.json
--dir ANOTHER_FRESH_DIRECTORY`, substituting the recorded identities. Do not
reuse a populated download directory or overwrite original evidence.

Conversations Story 16.1 remains **draft/backlog**, implementation hold **ACTIVE**,
with execution, owner approval, release and push authority unclaimed. Its broad
regression timed out independently (exit 143; 4,775 collected tests). Complete
that scope separately; focused passing partitions do not close it. Adoption,
Platform identity/acceptance, and remaining runtime owner gates also retain
their recorded states. Artifact identity and the 36 passing controls are preserved.

## Local validation

The following focused checks pass; results are retained in the packet's
`validation.json`. They verify the producer correction and recovery report;
they do not complete original or successor hosted qualification.

```text
python3 Tools/test-archive-g4-qualification-evidence.py
python3 Tools/test-self-ci.py
pwsh -NoProfile -File Tools/test-g4-release-contract.ps1
pwsh -NoProfile -File Tools/test-publish-g4-tool-packages.ps1
actionlint .github/workflows/ci.yml .github/workflows/build-release.yml
python3 evidence/g4/producer-publication-4383-20261031/verify-recovery.py
git diff --check
```

The carrier suite exercises 13 cases, including disk interruption, path and
inventory faults, source/run identity drift, and publication-time corruption.
The publisher suite uses mocked package uploads in an isolated temporary
repository. The installed semantic-release GitHub asset resolver was also
checked against an offline directory containing both configured assets.

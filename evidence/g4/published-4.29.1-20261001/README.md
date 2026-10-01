# Published `4.29.1` candidate observations

These are metadata-only candidate observations for P0, not acceptance evidence.
The selected tuple is EventStore `3.110.0` / tag `v3.110.0` /
`27279fe6431925a6ea046c3f89af61487185c7de` and Builds tools `4.29.1` /
`21ce044ab465ccb2adab58b3d66e394ffbecf3c2`.

The independent accepted P1R record still binds EventStore `3.106.0` and Builds
`ad52f350a2f0bc47849179ae17b4594dafff5363`. Fresh validation of the
2026-09-29 G-6 packet exited `1` with `G6-EVIDENCE-INVALID: Central package pin
drift: CommunityToolkit.Aspire.Hosting.Dapr`. No live qualification, rollback
drill, owner decision, or P4 handoff ran. Full P0 acceptance remains pending.

| Check | Actual result |
| --- | --- |
| Isolated NuGet.org restore and tool versions | Passed; both `4.29.1+21ce044ab465ccb2adab58b3d66e394ffbecf3c2` |
| Published synthetic command controls | All 97 matched: 13 positive, 2 unavailable, 16 manifest negatives, 29 readiness negatives, 36 acceptance negatives, 1 missing-acceptance control |
| Six installed help contracts | Tool-specific help verified |
| Fixture provenance | All 233 copied metadata-only fixture files match the exact release inventory hashes and sizes |
| Repeated synthetic `down` | Both `0/HXI001`; no live resources started |
| Synthetic `run` and `test full` | Both `2/HXR003`; nonpassing evidence retained |
| Missing full-P0 acceptance record | Installed validator returned `6/HXE200` |
| Clean source/package gate, `0.0.0-p0.20261001.1`, `-RequireControls` | Interrupted with exit `143` after bounded silent restore/build startup; no inventory; no bypass |
| Focused Debug evidence/module builds | Both passed with zero warnings/errors |
| Evidence test assembly | 107 passed, zero failed/skipped |
| Module test assembly in isolated `TMPDIR` | 214 passed, zero failed/skipped |
| First module assembly attempt | 21/214 failed because unrelated `/tmp/.git` changed temporary fixture roots; retained |

`observations.json` records the exact commands, prerequisite hashes, scope, and
remaining mandatory work. `remote-controls/controls.json` binds each retained
command stream, expected outcome, exit code, and stable diagnostic vector.
The synthetic positive acceptance file and its approvers are fabricated
validator inputs. A passing control is not a production acceptance decision.
The three `failed-harness-attempt-*.json` files retain the temporary harness's
fixture enumeration and expectation-handling failures and their original
streams; the passing campaign used a new consumer and empty cache.

`release-tool-package-inventory.json` is the unmodified public
[release inventory](https://github.com/Hexalith/Hexalith.Builds/releases/download/v4.29.1/g4-tool-package-inventory.json).
`remote-controls/remote-package-provenance.json` records restored archive and
payload-entry hashes, nuspec IDs/versions/source commits, and signature
presence. The restored signed archive hashes are:

- `Hexalith.Builds.Module.Cli`: `e0405045ba53a23b891bafe523a9cfad3d8b86c4e8b9d4495dbcefea964c1e9f`
- `Hexalith.Builds.Evidence.Cli`: `739ae9b3da7233510a1588c2629940b835c29b3d9c940a843f139d7b55d1eddb`

Those differ from the inventory's unsigned hashes. Independent payload
equivalence remains unproven because the original qualified archives were not
release assets and neither inspected release/CI run exposed Actions artifacts.
Signature presence alone is insufficient proof. Original archives, current
clean source/package parity, both native persisted lanes, all required profile
classes and causal failure/report evidence, cancellation, live idempotent
cleanup, and exercised rollback remain mandatory after matching independent
P1R/G-6 acceptance.

Only the actual bound artifacts and dated Builds Owner, Platform Owner, and
named Test Architect decisions may create `evidence/g4/6.1-p0-acceptance.json`.
Validate that exact record with the installed tool before P4 handoff. The
current rollback validator binds an attestation; it does not prove a drill.
P0 does not satisfy Story 6.1's other independent gates.

`artifact-manifest.json` binds every retained file in this directory except
itself. `.gitattributes` disables text conversion for these evidence bytes.

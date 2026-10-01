# G-6 current-target source preparation

These are prerequisite observations, not P1R, G-6, or P0 acceptance.

The original validator rejected stable Fluent UI `5.0.0` with a truthful
`stable` disposition and accepted it when mislabeled as an RC exception. The
source correction requires the disposition to match the release class. Stable
versions require `stable`; RC versions require
`approved-release-candidate-exception`; other prerelease classes are rejected.
Existing owner-role, date, tuple, and artifact requirements remain enforced.

From Hexalith.Builds, the corrected source passed:

```text
PYTHONDONTWRITEBYTECODE=1 python3 Tools/test-runtime-toolchain-evidence-validator.py
```

The exact result is retained in `mutation-suite.stdout.txt`: 30 packet scenarios
for each of three baselines, 48 baseline-drift controls, 165 authority controls,
and two historical baseline SHA-256 pins. The 24 new classification controls
include accepted stable/RC cases and rejected missing, mismatched, unknown, and
beta-as-RC dispositions. A hermetic copy of the new tests against the original
validator from Builds `21ce044ab465ccb2adab58b3d66e394ffbecf3c2` fails with
`Fluent UI RC exception missing`, as expected. Its original output is retained
in `original-validator-regression.stderr.txt`.

The actual prerequisite gate still fails. From Hexalith.Projects:

```text
PYTHONDONTWRITEBYTECODE=1 python3 tests/tools/run_g6_candidate_gate.py --baseline references/Hexalith.Builds/Tools/runtime-toolchain-baseline-2026-09-29.json --packet _bmad-output/implementation-artifacts/qualification-evidence/g-6-runtime-toolchain-20260929/packet.json
```

Exit `1` reports seven failed checks: Toolkit catalog drift, five packet gitlinks
that differ from current root gitlinks, and the CI Builds execution SHA differing
from the root Builds gitlink. Exact diagnostics are in `current-gate.stderr.txt`.
At the assessment checkpoint the historical baseline JSON files, P1R acceptance
record, and G-6 packet were byte-identical to their observed revisions;
`assessment.json` preserves those hashes and the modified source/test files.
The concurrent P1R transition subsequently recorded four-role acceptance of
EventStore `3.110.0` with Builds `21ce044ab465ccb2adab58b3d66e394ffbecf3c2`
at `2026-10-01T06:17:01Z`. `p1r-consumed.json` binds that current record and the
passing production-authority guards. This transition does not accept G-6.
No replacement approved runtime/toolchain baseline, new packet
acceptance, or live qualification was created. A fresh packet must bind the
corrected source, current tuple/revisions, reports, exact mutation result, and
independent owner decisions. The published `4.29.1` tool archives are unchanged.

`candidate-metadata-audit.json` independently verifies all 215
[published-candidate artifacts](../published-4.29.1-20261001/README.md), 194 command
streams for 97 checks, all 81 negative vectors against their source expectations,
and 233 release-inventory fixture bindings. Ordered diagnostics and repeated
rules are preserved. These controls supply no persisted or owner acceptance.

P0 still needs fresh accepted G-6 inputs, clean source/package qualification,
original qualified archives for independent remote payload comparison, current
native persisted/failure/cancellation/cleanup evidence, exercised rollback,
and dated Builds Owner, Platform Owner, and named Test Architect decisions.

`artifact-manifest.json` binds every file in this directory except itself.

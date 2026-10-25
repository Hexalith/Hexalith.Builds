# Module CLI 4.38.0 review disposition

The published-artifact configuration checks pass. Exact-source CI, the protected
release, actual feed identities, ordinary validation in both formats and all 36
malformed-key controls are verified. Adoption remains **6 PASS / 18 FAIL**; no
adoption or runtime entry is accepted.

Three independent review layers examined the source/evidence diff. Thirteen
findings were individually triaged. Ten groups concern incoming runtime work and
remain follow-ups; two findings were rejected. B10 and G1 describe the same
native-host coverage gap. The [complete triage](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json)
retains every verdict, evidence, source origin and route.

The runtime changes came from separate work in commits
`984c4d50205d00b5238c1cf19e93b232c3282c22`,
`0ade5ec2b4f9b81fcd3a4bbb1672c11b0131a274` and
`6e40ea5f6243391bea80084b8ddd695df58fb1ff`. They are present in released source
`e171c5a788eab183b32683431b084198c9b8d373`; they were preserved, not introduced
or repaired by this configuration-key release continuation. Passing the existing
release gates does not resolve these additional runtime concerns.

| Findings | Verdict | Separate runtime follow-up |
| --- | --- | --- |
| B1 | high | Serialize exact cleanup with native child launch; the single pre-launch owner check leaves an interleaving after cleanup finishes. |
| B2 | medium | Preserve the first timeout cause when owner cancellation arrives during child stopping. |
| B3 | high | Treat inaccessible Windows/macOS process environments as discovery uncertainty rather than verified absence. |
| B4 | high | Propagate incomplete Linux `/proc` discovery instead of reporting an empty verified scan. |
| B5 | medium | Validate/redact persisted resource identities before listing; reviewer reported a fake-secret echo and source inspection confirms missing validation. |
| B6 | medium | Give `list` the structured cancellation handling used by the existing validation command. |
| B7 | medium | Align claim-receipt admission and read bounds; the retained 67,001-byte receipt cannot be read after successful acquisition or release. |
| B9 | medium, unverified | Establish the intended machine-crash durability contract and test it; atomic replacement is present, but no explicit stable-storage barrier was found. |
| B10, G1 | medium | Execute native ownership/discovery/cleanup tests on Windows and macOS; Ubuntu and injected byte tests do not qualify those paths. |
| E1 | medium | Clear or rebind execution/catalog metadata before a later batch entry can refuse its descriptor. A retained unit repro shows stale first-entry bindings on the second failure. |

These groups are appended to the [deferred-work ledger](../../../_bmad-output/implementation-artifacts/deferred-work.md).
The [exact append and prior-byte preservation record](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/deferred-append-record.json)
and [final whitespace correction](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/deferred-finalization-record.json)
and [retained repro provenance](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/repro-artifact-provenance.json)
make the follow-up reviewable. Repros use same-source Debug fixtures, not a new
Platform or released-artifact runtime qualification. Some reviewer terminal
streams were not separately retained; that limitation is explicit rather than
reconstructed as native evidence.

B8's asserted oversized lifecycle-record path was retracted after the upstream
1 MiB descriptor limit was examined; no reachable oversized record was shown.
B11 identifies a failed historical collector capture with original temporary
paths, not a supported current replay entry point. Adding a replacement collector
interface is not needed to inspect the retained identities; the parameterized
artifact probe already accepts explicit roots, version, source, inventory and a
fresh output directory. Both rejected findings remain in the triage record.

The artifact observations ran on Linux; governed verification jobs ran on Ubuntu.
No Windows/macOS runtime coverage, Platform qualification or Story 16.1 runtime
entry is claimed. These follow-ups must remain visible in the separate owner
decision; this review does not change pins, gitlinks, deployment state or the
ACTIVE runtime hold.

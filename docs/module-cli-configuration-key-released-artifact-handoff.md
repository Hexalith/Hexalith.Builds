# Corrected Module CLI 4.38.0 artifact handoff

Observed 2026-10-25; exact command timestamps are retained. The corrected Module CLI is published as **4.38.0** from
`e171c5a788eab183b32683431b084198c9b8d373`. Its actual signed NuGet.org artifact passes ordinary
Conversations validation in both formats and rejects all 36 malformed-key probes.
Adoption remains incomplete: **6 PASS / 18 FAIL**, including every historical
failure and one newly observed checkout/index mismatch. Story 16.1's runtime hold
remains ACTIVE; no Platform acceptance, application deployment or pin/gitlink edit
is performed by this continuation. Independent review is complete; the
[runtime follow-ups and coverage limits](module-cli-configuration-key-release-review.md)
remain explicit and grant no runtime-entry credit.

## Published source and governed release

| Identity | Value |
| --- | --- |
| NuGet package / command | `Hexalith.Builds.Module.Cli` / `hexalith-module` |
| Publication feed | [NuGet.org V3](https://api.nuget.org/v3/index.json) |
| Semantic-release version | `4.38.0` |
| Exact tag/source | [`v4.38.0`](https://github.com/Hexalith/Hexalith.Builds/releases/tag/v4.38.0) → `e171c5a788eab183b32683431b084198c9b8d373` |
| Successful exact-source push CI | [40192040716](https://github.com/Hexalith/Hexalith.Builds/actions/runs/40192040716) |
| Successful protected Release | [40193969210](https://github.com/Hexalith/Hexalith.Builds/actions/runs/40193969210) |
| Final published inventory SHA-256 | `1405abc0e1476e8b41d275f46369d2034a0e761154b42d39c85acc33ae595786` |
| Source commit merge | Normal PR #138; rule suite 4710425344 passed |

The [published inventory](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/g4-tool-package-inventory.json) binds Release
configuration, exactly two approved tool IDs/four package artifacts, official
package build, executed/passed source validation and controls, a clean exact-source
revision and `releaseEligible: true`. Both source guards passed against current
main and its successful push CI. Pinned commitlint validated the exact imported
PR #138 message and every message used by this continuation.

Final read-only inspection observed later external main
`0c735503728e18de93937b1495abebcfa6e33f9a`, two commits ahead of the release,
including runtime corrections. The published tag still resolves to `e171c5a…`;
the verified local checkout stays there. The [remote observation](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/post-release-remote-source-observation.json)
retains the changed paths. Later main receives no qualification credit from this
artifact handoff, and its corrections do not alter the already published package.

The production environment retained its required reviewer and disabled admin
bypass. Actual review history records approval by `jpiquot` with a comment stating
that no separate interactive review occurred; independent human review is not
inferred. Another session dispatched the successful run 21 seconds before our
redundant queued 40193995250. Cancelled only that queued duplicate with zero jobs
or publication. Its cancelled result earns no release credit.

Historical governance remains explicit: this continuation's initial c81e691
main push bypassed active branch rules and was a deviation. No release was taken
from it. Normal PR #134 and PR #136 passed merge rule suites; external `/pushall`
bypasses, PR #135 consumption, every cancellation and both native CI failures are
retained in the [progress record](module-cli-configuration-key-release-progress.md)
and additive evidence. The successful final route changes or bypasses no gate.

## Full native qualification and fixture scope

Both the initial `0.0.0-ci.451` full lane and actual `4.38.0` pre-tag full lane
executed these distinct Release test assemblies, each with zero failed tests:

| Project | Total | Succeeded | Skipped |
| --- | ---: | ---: | ---: |
| Evidence.Tests | 178 | 178 | 0 |
| Module.Tests | 2116 | 2113 | 3 |
| Tooling.IntegrationTests | 13 | 1 | 12 |

The three Module skips need Platform checkout context; twelve Integration skips
are opt-in live cases. Neither earns Platform/live-runtime qualification credit.
[Native results and publisher hashes](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/completed-release-native-proof.json)
link to the retained complete workflow log archive and skip observations.

The existing qualifier inventories 289 visible fixtures using PowerShell
`Get-ChildItem -File -Recurse` without `-Force`. The exact 289-file scope, unique
paths, source-bound hashes/sizes and ordered manifest digest were independently
recomputed. Git tracks 290 fixture files: hidden
`package-consumer/.config/dotnet-tools.json` is outside that existing enumeration;
its separate raw source identity is retained. Inventory coverage is not described
as every tracked fixture. [Scope/filter proof](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/published-fixture-source-set-and-attributes.json)
and [independent parent proof](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/parent-verification/scoped-fixture-verification.json)
retain the distinction.

Tracked `.gitattributes` requires CRLF for C#; 23 fixture checkout byte streams
differ from their raw LF Git blobs. All 289 qualified byte hashes/sizes match
`git cat-file --filters` at the release source; the exact attributes and effective
path filters are retained. The first supplemental raw-blob/all-tree assumptions
and their failed assertions remain separate observations; no product gate changed.

## Actual feed artifact identities

Fresh attempt 03 used a new isolated tool directory, NuGet package/HTTP caches and
DOTNET_CLI_HOME, with a clear-only NuGet.org configuration and `--no-cache`.
The observation host was Linux; the governed jobs ran on Ubuntu. Windows/macOS
runtime discovery paths remain outside the measured qualification scope.
Signature verification exited 0. Feed catalog SHA-512 and size match the restored
signed archive. NuSpec repository commit, public CLI version and Tooling assembly
informational version all bind exact `e171c5a788eab183b32683431b084198c9b8d373`; version output is
`4.38.0+e171c5a788eab183b32683431b084198c9b8d373`.

| Measured item | SHA-256 / size |
| --- | --- |
| Actual signed Module nupkg | `f352d3e0f03f21dbfe42c89adb7a8765cb073a2351281acaa3cf304235426ab1` / 37,220,356 bytes |
| Qualified inventory Module nupkg | `27BA93BC935E711703600F4978BF98E1DF3A51DFB34A3D27236B110F177E969D` / 37,207,269 bytes |
| Packaged/installed Tooling DLL | `0266257a74c1dbc49407c61b07d90f7c95b806d937ea4f338b98bf8d128256fd` |
| Archive NuSpec | `4a7a58f42fdc44ae84269c4909975741d2d275ae7c9cd76268c25950b57a0c8a` / 899 bytes |
| Feed NuSpec | `6fa07e2c603eb3c39e3967fc9fa1ce16eff2f511a66f63a2502a86cae0353d90` / 896 bytes |

NuGet.org repository signing changes the outer ZIP. The governed publisher
verified the signed remote SHA against the qualified package's uncompressed
payload; the freshly restored signed hash equals that publisher observation.
Qualified unsigned and restored signed outer hashes are distinct identities.
The feed NuSpec omits exactly the archive's three-byte UTF-8 BOM; every remaining
byte and repository attribute is equal. Both raw NuSpecs remain retained.

[Complete identity](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/published-artifact-03/identity.json),
[retained signed package/DLL identity](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/published-artifact-03/retained-payload-identity.json)
and raw feed responses/signature streams permit inspection. Each following hash
is equal across the exact-source Git blob, loose package schema and raw embedded
Tooling resource:

| Schema | Equal source/loose/embedded SHA-256 |
| --- | --- |
| hexalith.module-manifest.v2.json | `06d6804f3b111e803edd0d9142b03f36df344e26e2d71e50e2291fe15039daa4` |
| hexalith.test-environment.v1.json | `bf1e46acc773991037daaae20dff1d815faf4af13ab7887d88339609607ff357` |
| hexalith.test-result.v1.json | `9773678febac38025fae837154341d9036ce8aa94e66b0d30c7e15ee668d7e39` |

## Embedded Platform catalog and source Props

The raw embedded resource
`Hexalith.Builds.Tooling.Manifest.platform-version-catalog.json` has SHA-256
`b44390bdb569e2d9b909c0473092f3ae0f768b95b7bed37d06905ca648ed1ead`. [Exact bytes](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/published-artifact-03/embedded/Hexalith.Builds.Tooling.Manifest.platform-version-catalog.json)
contain:

```json
{
  "schemaVersion": "1",
  "eventStoreVersion": "3.127.0",
  "frontComposerVersion": "4.7.0",
  "daprSdkVersion": "1.18.10",
  "aspireHostingVersion": "13.6.3",
  "aspireHostingDaprVersion": "13.6.1-beta.935",
  "aspireAppHostSdkVersion": "13.6.3",
  "daprRuntimeVersion": "1.18.7",
  "daprCliVersion": "1.18.2",
  "redisImage": "docker.io/library/redis",
  "redisImageTag": "7.4-alpine",
  "redisImageDigest": "sha256:ff02b58f971e7d7d156a1267e283fcbbeee91773b6aa36c49dac28ecfe28eadf",
  "eventStoreHostingDaprRange": "13.6.1-beta.935"
}
```

Exact release-source `Props/Directory.Packages.props` Git blob
`b31e9a952317c0c62a1bf518208e1a2cef2a19e2` has SHA-256
`553796eef36f2789f60bb47bc8902b5fb9f1df5b3a942cde7f0523768bf1d2a1`. Its declared CRLF checkout SHA-256 is
`c43f62b33c765e28519b6c72058c6317649ec53963d7cbe7f0d0ddabb3898375`. Both
[raw source blob](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/published-artifact-03/source-Directory.Packages.props) and
[checkout bytes](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/published-artifact-03/source-checkout-Directory.Packages.props)
are retained; these source and embedded JSON identities remain separate records.

## Unchanged declaration and all remaining adoption failures

Conversations declaration SHA-256 remains
`0bed1c297c9c9d78fdb8f37197ac5f71d829191c0535fdd78e4034aaa017bdf0`. Ordinary human/JSON validation exits 0/0 with no
diagnostics. Both configuration-key fields were mutated only in 18 temporary
copies, testing nine malformed values in human and JSON modes: 36 exits 1, each
with one exact-field HXP002 and the same canonical input-file identity. All
malformed inputs and original declaration bytes remained unchanged by validation.
[Raw commands/streams](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/published-artifact-03/commands.json),
[results](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/published-artifact-03/results.json) and the retained 18 input copies
record every observation.

Adoption human/JSON exits 1/1, with identical 24 rows: 6 PASS and 18 FAIL.
Workspace, declaration, references, nested-references, tool-pin and
hosting-inventory pass. Every failure is retained below and verbatim in the raw
reports:

| Requirement | File | Remaining issue |
| --- | --- | --- |
| gitlinks | `references/Hexalith.Builds` | Checkout/index source mismatch (new) |
| dependency-selectors | `Directory.Build.props` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `Directory.Packages.props` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `src/Hexalith.Conversations.AppHost/Hexalith.Conversations.AppHost.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `src/Hexalith.Conversations.Client/Hexalith.Conversations.Client.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `src/Hexalith.Conversations.Contracts/Hexalith.Conversations.Contracts.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `src/Hexalith.Conversations.Server/Hexalith.Conversations.Server.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `src/Hexalith.Conversations/Hexalith.Conversations.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `tests/Hexalith.Conversations.AppHost.Tests/Hexalith.Conversations.AppHost.Tests.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `tests/Hexalith.Conversations.Contracts.Tests/ContractPackageInventoryTest.cs` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `tests/Hexalith.Conversations.Contracts.Tests/Hexalith.Conversations.Contracts.Tests.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `tests/Hexalith.Conversations.IntegrationTests/Hexalith.Conversations.IntegrationTests.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `tests/Hexalith.Conversations.Server.Tests/Hexalith.Conversations.Server.Tests.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `tests/Hexalith.Conversations.Tests/Hexalith.Conversations.Tests.csproj` | File-based/sibling/nested/package-fallback selection |
| dependency-selectors | `tests/fixtures/Hexalith.Conversations.MinimalModule/Directory.Build.props` | File-based/sibling/nested/package-fallback selection |
| dapr-subscriptions | `src/Hexalith.Conversations.Server/Projections/ConversationProjectionReadModelKeys.cs` | Literal Dapr component name |
| dapr-subscriptions | `tests/Hexalith.Conversations.IntegrationTests/Projections/ConversationGatewayLiveFixture.cs` | Literal Dapr component name |
| dapr-subscriptions | `tests/Hexalith.Conversations.IntegrationTests/Projections/ConversationProjectionReadStorePopulationLiveTests.cs` | Literal Dapr component name |

All 17 historical failures remain byte-for-byte identical rows. The former
passing gitlinks row is replaced by one failure: the released Builds checkout
`e171c5a788eab183b32683431b084198c9b8d373` differs from Conversations' current
indexed Builds link `68e65f2023eca117a2aa08a022ee33d8f4c34a44`.
Conversations externally advanced to `ed53473ea4c67aa10ffc46c9b951d9864fe263d4`
with that index change at 11:24:40 UTC. This continuation preserves the index and
all concurrent user work; it does not execute the CLI's suggested repair.

Attempt 01 stopped on the additional raw NuSpec BOM assumption; attempt 02 stopped on an
additional absolute-path presentation assumption. Fresh attempt 03 completed all required
measurements, then its final predecessor-equality assertion exited1 on the real
new gitlink row. That native wrapper exit remains unchanged. The
[additive triage/completion ledger](../evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/actual-feed-measurement-triage.json)
classifies the difference without hiding it or rerunning to manufacture exit0.
Adoption acceptance is not claimed.

## Exact Platform handoff and authority

The preserved Platform checkout is
`b9cf0e78c42ca8bfe1aa5488cc4340c238887acb`, with tool pin `4.35.2`,
`rollForward: false`, range `[4.35.2, 5.0.0-0)`, and indexed Builds gitlink
`0ade5ec2b4f9b81fcd3a4bbb1672c11b0131a274`. README's accepted 4.35.2 historical
source/tag is `4248b8c70cb35ba79fdd7e4dd63049d4c73211a0`; the current index is a
pre-existing mismatch, not a new qualified pair. No Platform file/index repair
or qualification is performed.

Adopting this measured artifact would pair Builds tag commit
**`e171c5a788eab183b32683431b084198c9b8d373`** with exact Module tool version
**`4.38.0`** and **`rollForward: false`**. The separate owner decision and full
Platform qualification must consider the recorded runtime follow-ups; the owner
may require a later corrected successor. This handoff supplies exact identities
for the published artifact and grants no runtime-entry authority.

The final source also retains external commit 6e40ea5's compiled
`ModuleRunEvidenceArtifactValidator.cs` policy delta: completed selected-test
validation no longer requires removed disposition and empty remaining resources;
terminal completed/success, report-hash and positive all-passing counts remain.
The 372-input equality proof covers module-key schema and workflow/qualifier/
publisher/fixture/catalog/lock/SDK inputs, not unchanged runtime/all validators.
That external policy is disclosed separately and earns no Platform acceptance.

Protected Conversations/Platform pins, declaration, prior observations and
Story 16.1 spec/sprint bytes are compared against original/resumed inventories.
Observed external checkout/index changes remain explicit. The hold stays ACTIVE;
no application deployment or new Platform qualification is claimed. Final docs
and additive evidence remain local for owner review, with no extra main push.

Historical collector attempts preserve their original temporary paths and are
not portable replay entry points. The retained `probe-published-artifact-03.py`
accepts explicit repository roots, version, release SHA, inventory and a new output
directory. Its final comparison can return 1 after completing all probes when
adoption differs from the predecessor; the triage ledger explains this observation.
Never reuse a saved observation directory for replay.

# Platform signed operational records

`Hexalith.Builds.Tooling.ReleaseRecords` provides eight v1 contracts and two v2 contracts with offline embedded schemas, `schemas/hexalith.release-records.v1.json` and `schemas/hexalith.release-records.v2.json`. These records are separate from Builds package-release receipts. Tooling remains part of the existing CLI packages; this change adds no package publication, executable, storage, locking or deployment implementation.

## Contracts and writers

Each v1 branch has a distinct `schema` identifier, `https://schemas.hexalith.com/platform/{kind}.v1`, and a corresponding `{kind}-v1` schema anchor. Every envelope requires `schema`, `kind`, `recordId`, `writerId`, `keyId`, `subject`, `environment`, `createdUtc`, `recordedUtc`, `payload`, `algorithm` and `signature`. Publication records require a null environment; other records require `staging` or `production`. Optional payload values are explicit nulls. The schema closes every envelope and typed object; named dictionaries are the only extensible property maps.

| Kind / payload | Required writer | Bound content |
| --- | --- | --- |
| `release` / `ReleaseRecord` | Publication | Release identity, retained Helm package, module artifacts/images, composed host, catalog route content, stable McpCli hash, profile template, sidecar patch, realm and recovery-hook contract versions, check suites, qualified environment-layer/shared sets, module classifications and named evidence digests |
| `attempt` / `AttemptRecord` | Target environment executor for ordinary attempts, in-place restore and baseline re-deploy (production executor for the production case); recovery executor for replacement-capacity DR | Release reference, kind, environment-current values, render mode, lock epoch, original entry/first mutation/outage times, pre-mutation/readiness/verification/maximum-lifetime deadlines, check timeout and grace, single recovery allowance and consumption, observed stop, pre-incident ingress, outcome and takeover links |
| `qualification` / `QualificationRecord` | Issuing attempt's writer | Exact release/attempt references, verified profile, extended environment-layer/shared sets and evidence |
| `production-promoted` / `ProductionPromotedRecord` | Issuing production attempt's writer | Exact release, attempt and qualification references and active profile |
| `release-available` / `ReleaseAvailableRecord` | Publication | Exact release, terminal working staging release attempt and staged composed image |
| `latest-working` / `LatestWorkingPointerRecord` | Same environment's working terminal attempt's writer | Release, exact terminal attempt and strictly advancing epoch |
| `promotion-stop` / `PromotionStopRecord` | Environment executor(s), off-site monitor, Administrator or deputy | Environment, monotonically sequenced stop revision, preceding stop reference, cause and evidence; this kind only sets stops |
| `administrator` / `AdministratorRecord` | Independently enrolled exclusive Administrator identity | Action and target, exact attempt/observed stop/predecessor references where applicable, expected stop revision, reason, cause dispositions and optional named recovery with acceptance checks and finite duration |

A superseded predecessor's final attempt revision is signed by the verified successor's writer, including a recovery-executor-only DR writer taking over an ordinary production release. Cryptographic verification admits that writer for a superseded envelope; transition validation then requires the exact linked successor and current higher CAS epoch. An otherwise entitled executor cannot replace another attempt's writer.

`EnvironmentCurrent` contains only secret/credential references and scopes, key/catalog generations and catalog root digest, secret-contract digest, applied realm version, active profile, hostnames and the SHA-256 digest of the operations-repository definitions (`operationsDefinitionsDigest`). The digest covers the pinned charts or manifests and values, rendered environment-layer objects, profile inventory, realm-contract instance and secret contract. Never put secret values in a record. `subject` is the release identity for release-related records, the environment for pointers/stops, and the action target for Administrator records. Reference identity and subject consistency are checked against verified context during transitions. Management grant and revocation records name `production`. Infrastructure approvals use one record per affected environment.

The v1 attempt schema requires `operationsDefinitionsDigest`. Attempt envelopes issued against the earlier schema without this field fail validation. The digest must be supplied at signing time.

## Staging-evidence policy v2

The `attempt.v2` schema requires `stagingEvidence` with the exact policy ID `hexalith.platform.staging-evidence-policy.v2` and the attempt's decision ID. Every revision retains that binding. An authorized v2 successor can terminalize a v1 predecessor while the predecessor retains its v1 encoding. V2 predecessors require v2 successors. `attempt.v1` omits the binding and rejects it when supplied. Select `ReleaseRecordVersion.V2` explicitly when calling `SchemaFor`, `Sign`, `Encode`, `GetSigningBytes` or `Verify` for a v2 attempt or `promotion-decision.v2` action. The original overloads select v1. Verification uses the selected schema exclusively. Use verified v1 release and latest-working records for those unchanged contracts.

Each `promotion-decision.v2` envelope signs the target release ID as `subject`, the exact retained release record reference, the policy and decision ID, the action and the independently enrolled writer and key. Action-specific fields are present only where required.

| V2 action | Required writer | Additional signed bindings |
| --- | --- | --- |
| `reserve-admission` | Production-scoped Platform `promotion-evaluator` | Exact in-progress, pre-mutation production release attempt and its current epoch |
| `reserve-administrator` | Independently enrolled exclusive Administrator with production scope | The release and decision identify this separate reservation. |
| `supersede` | Initiating staging attempt's `executor` | Exact initiating staging start or prerequisite `in-place-restore` attempt and its current staging epoch |
| `complete` | Production-scoped `executor` that wrote the authoritative working terminal attempt | Exact admission reservation, exact terminal attempt and epoch, and matching latest-working pointer |
| `release-reservation` | Production-scoped `executor` that wrote the authoritative non-working terminal attempt | Exact admission reservation and exact terminal attempt and epoch |

A staging start can supersede another release's decision. Cross-release supersession requires distinct retained release record IDs and decision IDs. A prerequisite reset may name that decision's own release and decision or be triggered by another release's staging work. The staging writer uses the staging epoch. Administrator reservation has no attempt or epoch field.

The source is Platform revision `e899cf23dda711fbd85db9ec2b87365d9453ab60` with policy SHA-256 `cfff4df7a1548a38274841aa983f8b851e4709c264b7aa24292702ad4fa4f0c6`. That policy inherits v1 at Platform revision `54d90be3b661591048640d3585a8eab4aea0e534`, blob `48b6a4df0ed150b7b44e9fba664e9a118c2b5e22`, SHA-256 `a8ebbf0a2cac6dc42045bd2b41e2402a20a1bed7630b8b9c899fbc726e85859d`. [Pinned staging-evidence policy v2](https://github.com/Hexalith/Hexalith.Platform/blob/e899cf23dda711fbd85db9ec2b87365d9453ab60/docs/contracts/staging-evidence-policy-v2.md)

## Independent trust and custody

Construct `RecordTrustPolicy` from independently supplied `RecordWriterKey` enrollment. Each entry declares a writer/key identifier pair, a public P-256 SubjectPublicKeyInfo PEM, explicit roles, allowed kinds, allowed environments, revocation state and `ExclusiveAdministratorCustody`. Neither record contents, store credentials, development JWTs nor a valid signature enroll a writer. Use one consistent independently administered policy to verify all records participating in a transition; refresh it when enrollment or revocation changes.

The roles are `publication`, `executor`, `recovery-executor`, `promotion-evaluator`, `monitor`, `administrator` and `deputy`. Roles and scopes are ordinal and case sensitive; enrollment must use canonical principal identities. A key must satisfy both its explicit scopes and the kind's writer matrix. Production DR requires `recovery-executor`; ordinary and in-place attempts require `executor`. The recovery role alone grants no staging authority. Production-promoted records require production scope. Publication has no environment dimension.

Administrator enrollment must assert exclusive custody and have only the Administrator role. The policy rejects any Administrator/non-Administrator overlap in either writer identity or normalized public-key fingerprint, including aliases under different key IDs or PEM line endings. Revoked enrollment still participates in custody-overlap checks. This is a trusted enrollment assertion: cryptography cannot prove physical custody or detect an undeclared key copy. Identity/key provisioning and production trust anchors remain caller responsibilities. Policies retain public material only; signing consumes a caller-owned `ECDsa` private key without retaining or disposing it.

## Encoding, signatures and failure behavior

1. JSON is UTF-8 without a BOM. Encoded output has no whitespace or final newline. Objects sort decoded property names with ordinal comparison, recursively. Arrays preserve declared order. Strings use `Utf8JsonWriter`'s default escaping. Numbers are bounded integers; no floating-point inputs are accepted. UTC timestamps use the exact `System.Text.Json` `DateTimeOffset` spelling, including the UTC `+00:00` offset, escaped canonically as `\u002B00:00`.
2. Signing bytes are that canonical envelope with only the top-level `signature` property omitted. Schema, kind, record identity, writer/key identity, subject, environment, both timestamps, every payload value and `algorithm` remain covered.
3. `ES256` uses the platform P-256 ECDSA implementation, SHA-256 and a 64-byte IEEE P1363 `r || s` signature encoded as standard base64. ECDSA signatures can differ for the same content. `VerifiedRecord<T>.Digest` is `sha256:` plus lowercase SHA-256 of the signing bytes, so exact signed content references remain stable across signatures.
4. Decode is bounded to one MiB, 32 levels, and the schema's 128-entry collection limits and 512-character identifiers. Duplicate decoded property names, unknown fields, wrong kinds/versions/algorithms, comments, trailing commas, extra JSON, malformed UTF-8 and representations that lose information during typed binding fail closed. Whitespace, property order and equivalent JSON string escapes do not change signing bytes.
5. `Verify<T>` returns either an immutable `VerifiedRecord<T>` plus its content digest or a fixed metadata-only failure code. It never returns a partially trusted payload. Typical codes include `schema-rejected`, `ambiguous-json`, `untrusted-writer`, `invalid-signature`, `invalid-record`, `noncanonical-value` and `record-too-large`. Signing, encoding and transition failures use `RecordValidationException.Code`. Failures do not echo untrusted content.

All payload collections are immutable. The codec owns the verification wrapper constructor and reconstructs typed content from the input bytes, so a caller changing its original byte array or copying an untrusted DTO cannot mutate verified context. A `RecordReference` binds both record ID and canonical signing-content digest; IDs alone never establish correspondence.

## Transition checks inside CAS

Verify signatures and resolve current trust independently. Then, inside the later store's atomic compare-and-set, read and verify the actual current records/epoch and call the relevant `RecordTransitionValidator` operation. Never treat validation performed before that atomic operation as authority to commit afterward.

```csharp
// Illustrative store pseudocode: no store implementation is provided here.
store.CompareAndSet(environment, expectedStoreVersion, current =>
{
    // Verify the current store bytes under the same independent current policy.
    var prior = RequireVerified<AttemptRecord>(current.AttemptBytes, trust);
    var release = RequireVerified<ReleaseRecord>(current.ReleaseBytes, trust);
    var candidate = RequireVerified<AttemptRecord>(proposedBytes, trust);
    RecordTransitionValidator.ValidateAttempt(candidate, release, current.Epoch, prior);
    return current.WithAttempt(proposedBytes);
});
```

Terminal attempts reject every changed signed value, including writer/key replacement. An exact canonical terminal envelope replay, including the signature, may be a no-op; active replay still requires the current CAS epoch. Established phase deadlines remain fixed, with one replacement allowed when the single automatic recovery is consumed. First phase-deadline population can update the ordinary lifetime to the latest recorded phase deadline plus grace; recovery-kind lifetimes remain fixed notification boundaries. For supersession, supply the prior nonterminal attempt and the verified successor as `takeover`. Its `Supersedes` reference must name the exact prior content, the proposed predecessor's `SupersededBy` must name that successor, its writer must sign the predecessor's final revision, and both final revision and successor must use the same current higher epoch in the same environment. Preserve all other predecessor payload values. Commit the successor, predecessor terminalization and epoch atomically in the later store. Takeover eligibility, authority-side fencing/draining and job credential revocation remain operational/store responsibilities; a linked signature alone cannot prove them.

`ValidateLatestWorking` requires the exact working terminal attempt, same environment, its writer, matching current epoch and a higher epoch than the current pointer. `ValidateQualification` and `ValidateProductionPromoted` enforce exact issuing-attempt authority, release references and profile binding. Production-promoted may be issued by an in-progress production attempt before upgrade; downstream lifecycle policy must invalidate it on a non-working outcome and decide whether it is eligible for use. `ValidateReleaseAvailable` requires the exact terminal working staging release attempt and release image.

```csharp
store.CompareAndSet(environment, expectedStoreVersion, current =>
{
    var stop = RequireVerified<PromotionStopRecord>(current.StopBytes, trust);
    var clear = RequireVerified<AdministratorRecord>(clearBytes, trust);
    var working = RequireVerified<AttemptRecord>(current.WorkingAttemptBytes, trust);
    RecordTransitionValidator.ValidateAdministrator(clear, currentStop: stop, attempt: working);
    // Store must also check create-only action identity and the observed revision.
    return current.ClearStopIfRevision(clear.Record.Payload.ExpectedStopRevision!.Value);
});
```

Stop sets use `ValidatePromotionStop` with the currently observed revision and preceding verified stop. Clearance requires an Administrator record naming the exact current stop and revision and the verified current working attempt in the same environment. The record must declare the stop’s cause `resolved` or `accepted`, so approval of an earlier stop cannot erase a later cause. Reviewing all outstanding causes across the stop history remains a downstream store responsibility. Release approval and takeover authorization name the approved attempt. A recovery start names its recovery plan. An observed stop and its expected revision must be supplied together. For admission and management actions with predecessor links, `ValidateAdministrator` checks exact verified lineage, environment, target and authority family. The store must separately enforce create-only identities and reject missing or conflicting current lineage, including a falsely claimed first record.

Record validation cannot establish committed storage, artifact provenance, evidence adequacy, staging age, live environment health, production admission eligibility, legitimate takeover entry or completed fencing. The downstream store and lifecycle consumers own those checks and atomicity. No private key, operational identity, lock, production action or publication workflow is supplied by this library.

### V2 decision CAS handoff

Story 4.7 calls `ValidatePromotionDecision` with the authoritative pending decision binding and verified retained release. Admission and supersession also require the exact current initiating attempt and the applicable environment's current epoch. For supersession, resolve the initiating attempt against its actual release and decision. When it is a reset, verify that the reset is the prerequisite for the conflicting staging start.

Completion and release resolve the current admission reservation, the exact admitted attempt revision it names and the authoritative terminal revision separately. Completion also resolves the signed working pointer. Check pointer advancement against the previous pointer when committing or the committed pointer when reconciling. The validator checks the admitted and terminal attempts for the same lineage and immutable fields, and requires the terminal attempt and closing action to use the current epoch. An in-progress or superseded attempt cannot establish a terminal outcome.

Story 4.7 owns durable revisions, atomic compare-and-set, reservation ordering, replay, cross-attempt takeover reconciliation and resolution of interrupted commits. It must obtain authoritative context inside CAS and keep reservations pending during uncertain outcomes. Lifecycle consumers own live evidence eligibility, takeover fencing, v2 activation and Administrator reservation lifecycle.

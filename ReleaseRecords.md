# Platform signed operational records

`Hexalith.Builds.Tooling.ReleaseRecords` provides eight v1 contracts and an offline embedded schema, `schemas/hexalith.release-records.v1.json`. These records are separate from Builds package-release receipts. Tooling remains part of the existing CLI packages; this change adds no package publication, executable, storage, locking or deployment implementation.

## Contracts and writers

Each branch has a distinct `schema` identifier, `https://schemas.hexalith.com/platform/{kind}.v1`, and a corresponding `{kind}-v1` schema anchor. Every envelope requires `schema`, `kind`, `recordId`, `writerId`, `keyId`, `subject`, `environment`, `createdUtc`, `recordedUtc`, `payload`, `algorithm` and `signature`. Publication records require a null environment; other records require `staging` or `production`. Optional payload values are explicit nulls. The schema closes every envelope and typed object; named dictionaries are the only extensible property maps.

| Kind / payload | Required writer | Bound content |
| --- | --- | --- |
| `release` / `ReleaseRecord` | Publication | Release identity, retained Helm package, module artifacts/images, composed host, catalog route content, stable McpCli hash, profile template, sidecar patch, realm and recovery-hook contract versions, check suites, qualified environment-layer/shared sets, module classifications and named evidence digests |
| `attempt` / `AttemptRecord` | Target environment executor for ordinary attempts, in-place restore and baseline re-deploy (production executor for the production case); recovery executor for replacement-capacity DR | Release reference, kind, environment-current values, render mode, lock epoch, original entry/first mutation/outage times, pre-mutation/readiness/verification/maximum-lifetime deadlines, check timeout and grace, single recovery allowance and consumption, observed stop, pre-incident ingress, outcome and takeover links |
| `qualification` / `QualificationRecord` | Issuing attempt's writer | Exact release/attempt references, verified profile, extended environment-layer/shared sets and evidence |
| `production-promoted` / `ProductionPromotedRecord` | Issuing production attempt's writer | Exact release, attempt and qualification references and active profile |
| `release-available` / `ReleaseAvailableRecord` | Publication | Exact release, terminal working staging attempt and staged composed image |
| `latest-working` / `LatestWorkingPointerRecord` | Same environment's working terminal attempt's writer | Release, exact terminal attempt and strictly advancing epoch |
| `promotion-stop` / `PromotionStopRecord` | Environment executor(s), off-site monitor, Administrator or deputy | Environment, monotonically sequenced stop revision, preceding stop reference, cause and evidence; this kind only sets stops |
| `administrator` / `AdministratorRecord` | Independently enrolled exclusive Administrator identity | Action and target, exact attempt/observed stop/predecessor references where applicable, expected stop revision, reason, cause dispositions and optional named recovery with acceptance checks and finite duration |

A superseded predecessor's final attempt revision is signed by the verified successor's writer, including a recovery-executor-only DR writer taking over an ordinary production release. Cryptographic verification admits that writer for a superseded envelope; transition validation then requires the exact linked successor and current higher CAS epoch. An otherwise entitled executor cannot replace another attempt's writer.

`EnvironmentCurrent` contains only secret/credential references and scopes, key/catalog generations and catalog root digest, secret-contract digest, applied realm version, active profile and hostnames. Never put secret values in a record. `subject` is the release identity for release-related records, the environment for pointers/stops, and the action target for Administrator records. Reference identity and subject consistency are checked against verified context during transitions.

## Independent trust and custody

Construct `RecordTrustPolicy` from independently supplied `RecordWriterKey` enrollment. Each entry declares a writer/key identifier pair, a public P-256 SubjectPublicKeyInfo PEM, explicit roles, allowed kinds, allowed environments, revocation state and `ExclusiveAdministratorCustody`. Neither record contents, store credentials, development JWTs nor a valid signature enroll a writer. Use one consistent independently administered policy to verify all records participating in a transition; refresh it when enrollment or revocation changes.

The roles are `publication`, `executor`, `recovery-executor`, `monitor`, `administrator` and `deputy`. Roles and scopes are ordinal and case sensitive; enrollment must use canonical principal identities. A key must satisfy both its explicit scopes and the kind's writer matrix. Production DR requires `recovery-executor`; ordinary and in-place attempts require `executor`. The recovery role alone grants no staging authority. Production-promoted records require production scope. Publication has no environment dimension.

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

`ValidateLatestWorking` requires the exact working terminal attempt, same environment, its writer, matching current epoch and a higher epoch than the current pointer. `ValidateQualification` and `ValidateProductionPromoted` enforce exact issuing-attempt authority, release references and profile binding. Production-promoted may be issued by an in-progress production attempt before upgrade; downstream lifecycle policy must invalidate it on a non-working outcome and decide whether it is eligible for use. `ValidateReleaseAvailable` requires the exact terminal working staging attempt and release image.

```csharp
store.CompareAndSet(environment, expectedStoreVersion, current =>
{
    var stop = RequireVerified<PromotionStopRecord>(current.StopBytes, trust);
    var clear = RequireVerified<AdministratorRecord>(clearBytes, trust);
    RecordTransitionValidator.ValidateAdministrator(clear, currentStop: stop);
    // Store must also check create-only action identity and the observed revision.
    return current.ClearStopIfRevision(clear.Record.Payload.ExpectedStopRevision!.Value);
});
```

Stop sets use `ValidatePromotionStop` with the currently observed revision and preceding verified stop. Clearance requires an Administrator record naming the exact current stop and revision and declaring its supplied cause `resolved` or `accepted`, so approval of an earlier stop cannot erase a later cause. Reviewing all outstanding causes across the stop history remains a downstream store responsibility. Admission/management actions may carry predecessor links; `ValidateAdministrator` checks exact verified lineage, environment, target and authority family. The store must separately enforce create-only identities and reject missing or conflicting current lineage, including a falsely claimed first record.

Record validation cannot establish committed storage, artifact provenance, evidence adequacy, staging age, live environment health, production admission eligibility, legitimate takeover entry or completed fencing. The downstream store and lifecycle consumers own those checks and atomicity. No private key, operational identity, lock, production action or publication workflow is supplied by this library.

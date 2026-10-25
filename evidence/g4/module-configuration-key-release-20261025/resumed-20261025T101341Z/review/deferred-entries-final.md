
- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: serialize exact cleanup with selected native child launch to prevent a late process under a cleaned owner.
  evidence: high; review B1 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. Concurrent cleanup can pass the single EnsureIssuedStateAsync check and release its provisioning lease before ExecuteSelectedAsync writes/recreates the workspace and starts a child. The selected gate has no cleanup-marker/provisioning lease. Static interleaving confirmed; no new Platform qualification. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: preserve the first execution-timeout cause when owner cancellation arrives during child stopping.
  evidence: medium; review B2 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. RunCoreAsync records a first timestamp, kills/waits, then throws on the current caller cancellation token. NativeTestExecutor samples owner cancellation afterwards, so a later cancellation can replace the earlier timeout cause. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: fail closed on inaccessible Windows/macOS process environments rather than report verified absence.
  evidence: high; review B3 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. Windows invalid OpenProcess and macOS errors1/13 return null without setting Verified=false. No independent ownership exclusion protects an owned process that becomes inaccessible. Actual-host behavior remains unqualified. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: propagate incomplete Linux /proc discovery so cleanup cannot release claims on unverified absence.
  evidence: high; review B4 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. Linux scanner returns(FindProcesses(runId),true), while absent /proc and read exceptions yield no observed processes. The caller can treat this incomplete scan as verified absence and release claims. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: validate or redact persisted remaining-resource identities before list output.
  evidence: medium; review B5 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. ValidResources checks nonnull collections only; ResourceIds and listing serialize containerIds directly. Reviewer reported fake-secret echo on same-source Debug CLI; original stream capture was not retained, so source inspection and reviewer testimony are distinguished. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: map list cancellation to the structured cancellation result and documented exit contract.
  evidence: medium; review B6 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. CreateListCommand awaits ReadAsync without the OperationCanceledException mapping present in validation; enumeration checks cancellation per file. Reported SIGINT repro returned exit1 with empty streams. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: align claim receipt write/read bounds so successfully acquired claims remain discoverable and cleanable.
  evidence: medium; review B7 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. Acquire has no receipt-length admission bound; ReadReceipt rejects over64KiB. Retained reviewer fixture is67,001 bytes and ReleaseOwned leaves the receipt. Public Acquire has no identity-count guard. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: resolve the unverified stable-storage durability expectation for terminal metadata before resource deletion.
  evidence: medium (unverified); review B9 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. Atomic rename and absence of an explicit stable-storage barrier are confirmed; whether the current promise includes machine/power-loss durability and a reachable lost-terminal-record case were not established. If true impact is medium; settle with an explicit crash contract and fault-injected storage test. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: qualify native Windows/macOS process discovery and exact-owner cleanup on those actual hosts.
  evidence: medium; review B10,G1 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. Native Windows/macOS discovery is exercised only through injected/synthetic tests in the Ubuntu lanes. Actual-host ownership/cleanup verification is missing; same root coverage gap as G1. Pre-verified gap: source/reference searches and actual CI platforms show no native Windows/macOS reader exercise; override/ContainsEntry tests do not cover discovery or cleanup. Same coverage root cause as B10. Separate incoming runtime owner work; no Platform/runtime-entry credit.

- source_spec: `_bmad-output/implementation-artifacts/spec-release-corrected-module-cli.md`
  summary: Builds: clear/rebind environment execution/catalog metadata when a later batch entry refuses descriptor creation.
  evidence: medium; review E1 in references/Hexalith.Builds/evidence/g4/module-configuration-key-release-20261025/resumed-20261025T101341Z/review/triage.json at e171c5a788eab183b32683431b084198c9b8d373. Batch loop resets ExecutionTiming but not EnvironmentBindings; a descriptor refusal returns before assigning new bindings. TestResultWriter checks owner/attempt/environment only. Retained same-source unit repro contains second selection with first execution/catalog bindings. Separate incoming runtime owner work; no Platform/runtime-entry credit.

# Deferred Work

## Deferred from: code review of 6-1-p0-deliver-g4-persisted-runner-and-evidence-tooling (Chunk B, 2026-07-21)

- **YAML parse-bomb / unbounded alias expansion** — `hexalith-evidence validate` deserializes the input twice (`Deserialize<object>` for duplicate-key detection, then `YamlStream.Load`) with no anchor/alias/depth bound, so a billion-laughs document can exhaust CPU/memory before any fail-closed diagnostic. `src/libraries/Hexalith.Builds.Tooling/Evidence/ReadinessEvidenceValidator.cs:459-485`. Deferred: input is semi-trusted in-repo authored content and YamlDotNet lacks native alias caps; revisit if the validator is ever exposed to untrusted matrices.
- **`IsArtifactHashes` does not validate keys for path/secret shape** — only hash values are checked as upper-hex/64; the keys (documented as repo-relative artifact paths) are not canonicalized or run through `ManifestSecretDetector`. `src/libraries/Hexalith.Builds.Tooling/RunEvidence/ModuleRunEvidenceArtifactValidator.cs:173-178`. Deferred: low surface — the readiness validator extracts only `finalStatus`/`exitCode` from the summary and never propagates artifact-hash keys.

## Deferred from: code review of 6-1-p0-deliver-g4-persisted-runner-and-evidence-tooling (Chunk C, 2026-07-21)

All of the following are latent behind the always-unavailable prerequisite gate (`RuntimePrerequisiteGate.Check` returns unavailable), so they cannot manifest in the reachable path; they belong with the live-runner implementation at P1/G-6.

- **Run-identity teardown scoping** — `ModuleInvocationStateStore.DownAsync` deletes state keyed on manifest hash, not RunId, so concurrent same-manifest invocations would tear down each other's state, violating AC 4 "operates only on resources bearing its run identity". `Runtime/ModuleInvocationStateStore.cs:83`.
- **World-shared state directory** — run state lives in `Path.GetTempPath()/hexalith-builds/runs` with no per-user scoping; on shared hosts another user/tool can enumerate/read/delete these metadata files. `Runtime/ModuleInvocationStateStore.cs:96`.
- **No cleanup on failure/cancellation** — the cancellation and lifecycle-failure handlers write evidence but never invoke teardown, so AC 4's "attempt safe cleanup" half is unimplemented. `Runtime/ModuleCommandExecutionService.cs:210-262`.
- **Non-atomic state write** — `CreateAsync` uses `File.WriteAllTextAsync` (no temp-file+Move), so a crash mid-write orphans a truncated file that `DownAsync` (which skips `JsonException`) can never reclaim. `Runtime/ModuleInvocationStateStore.cs:58`.
- **Identical tenant/resource namespaces + RunId truncation** — `ModuleRuntimePlan` sets `TenantNamespace == ResourceNamespace` and truncates the 128-bit RunId to 48 bits, so AC 5's distinct run-unique isolation axes are not realized. `Runtime/ModuleRuntimePlan.cs`.
- **Dead HXR003 block** — the unreachable positive path persists state then returns `unavailable`/`HXR003` (duplicating `HXR002` semantics); if the first gate is ever opened without restructuring, `run`/`test` will write persistent state yet still report exit 2. `Runtime/ModuleCommandExecutionService.cs:173-208`.
- **`down` manifest-reread TOCTOU** — `DownAsync` re-reads the manifest to hash it; a manifest removed between validation and this read yields exit 3 instead of idempotent completion. `Runtime/ModuleCommandExecutionService.cs:133`.
- **Cancellation reclassifies a failed path** — cancellation during the evidence write of an already-failed invocation unwinds into the cancelled handler (exit 130), dropping the causal failure's exit code/evidence. `Runtime/ModuleCommandExecutionService.cs:293`.

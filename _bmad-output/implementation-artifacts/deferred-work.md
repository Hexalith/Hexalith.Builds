# Deferred Work

## Deferred from: code review of 6-1-p0-deliver-g4-persisted-runner-and-evidence-tooling (Chunk B, 2026-07-21)

- **YAML parse-bomb / unbounded alias expansion** — `hexalith-evidence validate` deserializes the input twice (`Deserialize<object>` for duplicate-key detection, then `YamlStream.Load`) with no anchor/alias/depth bound, so a billion-laughs document can exhaust CPU/memory before any fail-closed diagnostic. `src/libraries/Hexalith.Builds.Tooling/Evidence/ReadinessEvidenceValidator.cs:459-485`. Deferred: input is semi-trusted in-repo authored content and YamlDotNet lacks native alias caps; revisit if the validator is ever exposed to untrusted matrices.
- **`IsArtifactHashes` does not validate keys for path/secret shape** — only hash values are checked as upper-hex/64; the keys (documented as repo-relative artifact paths) are not canonicalized or run through `ManifestSecretDetector`. `src/libraries/Hexalith.Builds.Tooling/RunEvidence/ModuleRunEvidenceArtifactValidator.cs:173-178`. Deferred: low surface — the readiness validator extracts only `finalStatus`/`exitCode` from the summary and never propagates artifact-hash keys.

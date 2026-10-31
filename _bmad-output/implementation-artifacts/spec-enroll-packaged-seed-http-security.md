---
title: Enroll packaged seed shared HTTP security
status: verified-locally
baseline_commit: 2132fe0e276059dcd9fc35a7115fd2a1795ce538
---

Latest upstream2132 includes all585 catalog/fixture/race repairs and the OQ8 retirement; preserve it intact. Hosted Build/package CI now fails only when the actual packaged EventStore seed starts: shared SDK HTTP security requires UseEventStoreHttpHostSecurity, but the host restricts that public3.129 API to source builds. Package inspection confirms UseEventStoreHttpHostSecurity, RequireEventStoreChannel and the actor endpoint marker exist in selected published SDK3.129.0.

Enroll actual host security and actual channel endpoint helpers in both package/source builds, retaining source-only catalog capability guards. Give the archive-only MVC probe ephemeral real channel/JWT keys; authenticate discovery and require real missing/invalid channel401 at the mapped notification receiver in both modes. Keep existing configured200/declarative404 and exact receiver/topic/archive/load/hash/source/typed-outcome assertions. Publication must require the new typed security facts; synthetic parser/publisher controls retain negative coverage. No validator or startup gate is bypassed; only the existing asserted startup index-writer alias is removed by this no-sidecar metadata fixture.

Scope: src/hosts/Hexalith.Builds.Module.EventStoreHost/Program.cs; Tools/PackagedSeedProbe/PackagedSeedProbe.cs; Tools/G4PackageQualification.functions.ps1; Tools/test-publish-g4-tool-packages.ps1; Tools/test-g4-tool-package-artifact-validator.ps1; Tools/README.md; this spec and compact verification summary. No redundant81 fixture/race changes or historical receipts. No nested dependencies; original Root/SDK outputs remain untouched. Verification and final exact manifest will be recorded before owning push.


Verification: host Release and reconciled probe Debug builds pass with zero warnings/errors. The strict artifact matrix passes all 71 cases; the publisher matrix passes including refreshed-hash security-observation negative controls. The full nonpublishing CI package-contract command passes with required controls, source/package CLI parity, two actual archives and the actual archive seed. Its explicit `-SkipSourceValidation` and dirty checkout prohibit release qualification credit.

Concurrent PR147 at e2b2c34 independently implemented the same host enrollment plus missing-channel discovery. Preserve its complete branch and observation: configured untokened discovery401 and declarative404 remain required. Reconciled actual-archive probe passes those original checks plus receiver missing/invalid401 in both modes, authenticated configured200/exact configured topic and declarative404/absent topic metadata. Only the fixture signing/channel credentials become ephemeral. Main rules require ordinary squash PR with Codacy, SonarCloud and commitlint checks; zero human approvals. Do not bypass automated checks.

The compact report next to this spec records exact commands, counts, nonsecret source/log/archive/loaded-seed hashes, two-mode observations and cleanup limits. Report SHA-256: `2ca486aef093d46188da5ea5b6a288144a97559c392cdcd4f17800d62803a512`. Original Root/SDK outputs were untouched. Temporary workspace baseline is excluded from product scope. No redundant81 fixtures, historical operator receipts, package pins, runtime-lock tuples or dictionaries changed.

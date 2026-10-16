# Historical Story 1.17 local preparation

This directory retains the exact dirty-source diagnostic before authorized Git
reconciliation. Its records and archives remain preparation, not acceptance or
publication evidence. The original diagnostic stays release-ineligible.

`verify-evidence.py` binds that historical source snapshot, including exact host
and audit bytes. A later reconciled checkout is expected to fail those source
bindings; do not regenerate this receipt or weaken its verifier to conceal drift.
The archives retain their exact original payloads and source patches.

The authorized follow-up commits/rebases the prepared source, preserves upstream
Dapr/security/startup work, refreshes audit metadata without changing dependency
selections, and reruns the complete G-4 workflow on clean committed source.
Folders records the resulting commit, qualification, artifact hashes and exact-head
push CI separately in:

`_bmad-output/implementation-artifacts/evidence/story-1-17-builds-clean-source-qualification-2026-10-16.json`

Use that follow-up record and its verifier for the current committed tuple.
Publication, Platform acceptance, staging/deployment and production activation
remain separate decisions. Story 1.17 remains open with V1Only and unset T0.

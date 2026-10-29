# Story 2.6 evidence notes

Attach admission under `hexalith.platform.candidate-and-attachment-policy.v1`.

- Records: `<state>/diagnostics/<owner>/attachments.json` (schema `hexalith.g4-run-attachments.v1`), written under `attachments.lock`.
  The owner run state and first-outcome records are never rewritten by an attach or an attachment end.
- Diagnostics: `HXR032` usage (hold/request), `HXR033` ineligible target, `HXR034` incompatible, `HXR035` owner state, `HXR036` retry lineage. The stable reason is the diagnostic field.
- Owner success-cleanup: `ModuleCommandExecutionService` waits (`CompositionAttachmentStore.WaitForNoLiveHoldsAsync`) after the first outcome is recorded and its result published, then runs the existing bounded cleanup with a fresh stopping allowance.
  If the wait is cancelled or attachment metadata cannot be verified, cleanup stays deferred and `down` retries it.
- Results and descriptors publish the populated `holds`; a hold past its expiry is reported as ended `expired` at its expiry.
- Verified with `AttachAdmissionTests`, `TestResultWriterTests` and `CompositionRunListingTests` (run directly, not through the slnx).
- Not covered (Stories 2.7-2.9): withdrawal ordering and `withdrawn` outcomes, failure retention interplay, CI job bounds and workflow changes, fixture migration.

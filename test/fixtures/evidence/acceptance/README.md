# G-4 P0 acceptance validator contract corpus

These files are synthetic validator controls for `hexalith.g4-p0-acceptance.v1`.
They are **not** acceptance evidence: the revision, package bytes, run evidence,
native reports, cleanup/rollback artifacts, and "Contract Sample Approver"
approvals are fabricated so `hexalith-evidence validate` can be exercised
positively and negatively. A real acceptance record must bind the actual
delivery revision, published packages, packaged live runs, and named approvals.

- `positive/` passes and prints `HXI210`.
- `negative/` each fails with exit `6` and the rule in its `.expected.json`.
- `bound/` holds the artifacts the records hash-bind by repository-relative path.
  Its bytes are pinned with `-text` in `.gitattributes`, because a line-ending
  conversion would break every bound SHA-256.

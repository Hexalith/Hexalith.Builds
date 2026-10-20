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

Refresh this corpus from the evaluated authoritative catalog with:

```bash
python3 Tools/prepare-g4-synthetic-acceptance.py --write
python3 Tools/prepare-g4-synthetic-acceptance.py
```

Run from the Builds repository. The second command checks consistency without
writing and must report zero changes. It checks pin/hash consistency and missing
tracked inputs; interrupted or unexpected fixture drift fails before writes. Run
`python3 Tools/test-prepare-g4-synthetic-acceptance.py` for preparation controls
and the Evidence test project for each exact public-validator expectation. Preparation edits only this synthetic
acceptance corpus, retains off-catalog negative mutations and exact expectation
files, and rebinds hashes only where they matched the original artifact bytes.
Malformed/duplicate-key controls retain their original formatting. Sample approval
identities, times and revisions remain fabricated validator inputs.

This acceptance contract uses all four current `SupportedPlatformPins`. The approved 2026-10-20 v1 compatibility repair aligns the module-manifest
loader, schema, executable/generic fixtures and package evidence assertions with
the same embedded catalog. This preparation command still edits only the
synthetic acceptance corpus. Historical qualification records remain unchanged;
the bound acceptance manifest is synthetic and grants no runtime acceptance.

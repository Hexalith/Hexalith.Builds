#!/usr/bin/env bash
set -euo pipefail

if [[ "$HEXALITH_ATTEST_CONTAINERS" != "true" ]]; then
  exec npm exec --no -- semantic-release
fi

# Load the caller's installed dependency from its workspace, as npm exec does.
node --input-type=module <<'JS'
import { readFileSync } from 'node:fs'
import semanticRelease from 'semantic-release'

try {
  const release = await semanticRelease()
  if (release && !readFileSync(process.env.GITHUB_OUTPUT, 'utf8').split(/\r?\n/)
    .includes(`oci-release-version=${release.nextRelease.version}`)) {
    console.error('Semantic Release completed a release without a validated OCI publication receipt.')
    process.exitCode = 1
  }
} catch {
  console.error('Semantic Release or its OCI publication receipt check failed.')
  process.exitCode = 1
}
JS

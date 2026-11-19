const { readFileSync } = require('node:fs');
const { join } = require('node:path');
const { TextDecoder } = require('node:util');
const { bundleFromJSON } = require('@sigstore/bundle');
const { ASN1Obj } = require('@sigstore/core');
const { TrustedRoot } = require('@sigstore/protobuf-specs');
const { Verifier, toSignedEntity, toTrustMaterial } = require('@sigstore/verify');

const fulcio = '1.3.6.1.4.1.57264.1.';
const identityOids = new Set([8, 9, 10, 11, 12, 13, 14, 15, 17, 21].map(id => fulcio + id));

function verifyBundle(bundle, trustedRoot) {
  if (bundle.mediaType !== 'application/vnd.dev.sigstore.bundle.v0.3+json' ||
      bundle.dsseEnvelope?.payloadType !== 'application/vnd.in-toto+json' ||
      bundle.dsseEnvelope.signatures?.length !== 1) {
    throw new Error('expected one in-toto DSSE signature in a Sigstore v0.3 bundle');
  }

  const entity = toSignedEntity(bundleFromJSON(bundle));
  if (entity.key.$case !== 'certificate') {
    throw new Error('expected a Fulcio signing certificate');
  }
  const verifier = new Verifier(toTrustMaterial(TrustedRoot.fromJSON(trustedRoot)), {
    ctlogThreshold: 1,
    tlogThreshold: 1,
    timestampThreshold: 1,
  });
  const { identity } = verifier.verify(entity);
  const extensions = Object.create(null);
  const decoder = new TextDecoder('utf-8', { fatal: true, ignoreBOM: true });
  for (const extension of identity.oids) {
    const oid = extension.oid.id.join('.');
    if (!identityOids.has(oid)) continue;
    const value = ASN1Obj.parseBuffer(Buffer.from(extension.value));
    if (value.tag.toDER() !== 0x0c || Object.hasOwn(extensions, oid)) {
      throw new Error('malformed Fulcio identity extension');
    }
    extensions[oid] = decoder.decode(value.value);
  }
  if (!identity.subjectAlternativeName || identity.subjectAlternativeName !== extensions[fulcio + '9']) {
    throw new Error('signing certificate URI does not match the Fulcio build signer URI');
  }
  return extensions;
}

try {
  const bundle = JSON.parse(readFileSync(0, 'utf8'));
  const root = JSON.parse(readFileSync(join(__dirname, 'trusted-root.json'), 'utf8'));
  process.stdout.write(JSON.stringify(verifyBundle(bundle, root)));
} catch (error) {
  // Error messages from the verifier may contain untrusted bundle data.
  process.stderr.write(`Sigstore bundle verification failed (${error.code || 'INVALID_BUNDLE'})\n`);
  process.exitCode = 1;
}

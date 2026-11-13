import { createRequire } from 'node:module';
import { join } from 'node:path';
import { policy } from './commitlint_bridge.mjs';

if (!process.env.HEXALITH_GUARD_TOOLS) throw new Error('trusted guard tool directory is required');
const require = createRequire(join(process.env.HEXALITH_GUARD_TOOLS, 'package.json'));
const preset = require('@commitlint/config-conventional').default;
const { resolveFrom, loadParserPreset } = require('@commitlint/resolve-extends');
const parser = await loadParserPreset(resolveFrom(preset.parserPreset, join(process.env.HEXALITH_GUARD_TOOLS, 'package.json')));
export default policy(preset, { name: preset.parserPreset, ...parser });

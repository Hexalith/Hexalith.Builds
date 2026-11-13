import { createRequire } from 'node:module';
import { policy } from './Github/guard/commitlint_bridge.mjs';

const require = createRequire(import.meta.url);
const preset = require('@commitlint/config-conventional').default;
const { resolveFrom, loadParserPreset } = require('@commitlint/resolve-extends');
const parser = await loadParserPreset(resolveFrom(preset.parserPreset, import.meta.filename));
export default policy(preset, { name: preset.parserPreset, ...parser });

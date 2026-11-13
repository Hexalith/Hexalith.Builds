// Tool configuration/bridge only: raw-message policy lives in stdlib Python.
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

export function policy(preset, parserPreset) {
  return {
    ...preset,
    parserPreset,
    defaultIgnores: false,
    plugins: [{ rules: {
      'no-skip-ci': (parsed) => {
        const result = spawnSync(process.env.HEXALITH_GUARD_PYTHON || 'python3',
          ['-I', fileURLToPath(new URL('../lib/commit_policy.py', import.meta.url))],
          { input: parsed.raw, encoding: 'utf8', timeout: 15000, maxBuffer: 1024 * 1024 });
        if (result.status !== 0 || !['true', 'false'].includes(result.stdout.trim())) {
          throw new Error('trusted raw-message policy failed');
        }
        return [result.stdout.trim() === 'true', 'GitHub workflow skip markers and skip-checks trailers are forbidden'];
      },
    } }],
    rules: {
      ...preset.rules,
      'body-max-line-length': [2, 'always', 200],
      'header-max-length': [2, 'always', 200],
      'type-enum': [2, 'always', ['build', 'ci', 'docs', 'feat', 'fix', 'perf', 'refactor', 'revert', 'style', 'test']],
      'no-skip-ci': [2, 'always'],
    },
  };
}

import { expect, test } from 'bun:test';
import { copyFileSync, mkdirSync, mkdtempSync, rmSync, symlinkSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';

test('the screenshot judge loads this clone .env from any working directory', () => {
  const fixture = mkdtempSync(join(tmpdir(), 'task-stats-judge-'));
  try {
    const repo = join(fixture, 'clone');
    const e2e = join(repo, 'tests', 'e2e');
    const artifacts = join(e2e, 'artifacts');
    mkdirSync(artifacts, { recursive: true });
    copyFileSync(join(import.meta.dirname, 'evaluate-screenshots.ts'), join(e2e, 'evaluate-screenshots.ts'));
    // Junctions work on Windows without enabling developer-mode symlinks.
    symlinkSync(join(import.meta.dirname, 'node_modules'), join(e2e, 'node_modules'), 'junction');
    writeFileSync(join(repo, '.env'), 'OPENROUTER_API_KEY=fixture-only\nTASK_STATS_VISION_MODEL=fixture-model\n');
    writeFileSync(join(artifacts, 'aggregate.png'), 'fixture image');
    writeFileSync(join(artifacts, 'aggregate.json'), JSON.stringify({
      scenario: 'aggregate', image: join(artifacts, 'aggregate.png'), expectation: 'fixture',
    }));

    // Intercept the whole request so this regression never spends money or sends data.
    const preload = join(fixture, 'mock-fetch.ts');
    writeFileSync(preload, `
      globalThis.fetch = async (url, options) => {
        const body = JSON.parse(options.body);
        if (url !== 'https://openrouter.ai/api/v1/chat/completions'
            || options.headers.Authorization !== 'Bearer fixture-only'
            || options.headers['HTTP-Referer'] !== 'https://github.com/mikecann/task-stats'
            || options.headers['X-Title'] !== 'task-stats-visual-tests'
            || body.model !== 'fixture-model') throw new Error('Wrong standalone configuration');
        return Response.json({ choices: [{ message: { content: JSON.stringify({ passed: true, summary: 'fixture' }) } }] });
      };
    `);
    const env = { ...process.env };
    delete env.OPENROUTER_API_KEY;
    delete env.TASK_STATS_VISION_MODEL;
    const result = Bun.spawnSync([process.execPath, '--preload', preload, join(e2e, 'evaluate-screenshots.ts'), artifacts], {
      cwd: fixture, env,
    });
    expect(result.stderr.toString()).toBe('');
    expect(result.exitCode).toBe(0);
    expect(result.stdout.toString()).toContain('PASS aggregate: fixture');
  } finally {
    rmSync(fixture, { recursive: true, force: true });
  }
});

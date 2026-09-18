import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { VERSION } from '../src/version.js';

// Proves the runner discovers tests and can import from src/. Delete once the
// first real module test lands (W11).
test('the source version matches the manifest', async () => {
  const manifest = JSON.parse(
    await readFile(new URL('../manifest.json', import.meta.url), 'utf8'),
  );
  assert.equal(VERSION, manifest.version);
});

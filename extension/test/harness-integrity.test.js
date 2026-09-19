// The harness page's files (W16): the service serves /dev/harness/<name>
// from extension/harness and /dev/src/<name> from extension/src, one path
// segment each, and nothing else. So every import in the harness must be
// `./<name>.js` or `../src/<name>.js`, and must exist; and the harness must
// never become part of what the extension ships to YouTube.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (path) => readFileSync(join(ROOT, path), 'utf8');
const harnessFiles = readdirSync(join(ROOT, 'harness'));
const harnessModules = harnessFiles.filter((f) => f.endsWith('.js'));
const manifest = JSON.parse(read('manifest.json'));

/** Relative module specifiers a file imports. */
function importsOf(path) {
  const source = read(path);
  return [
    ...source.matchAll(/^\s*(?:import|export)\s[^'"]*?from\s*['"]([^'"]+)['"]/gm),
    ...source.matchAll(/^\s*import\s*['"]([^'"]+)['"]/gm),
    ...source.matchAll(/import\(\s*['"]([^'"]+)['"]\s*\)/g),
  ].map((m) => m[1]);
}

/** The name the service would serve a specifier under, or null if it cannot. */
function served(specifier) {
  const own = /^\.\/([A-Za-z0-9_][A-Za-z0-9._-]*\.js)$/.exec(specifier);
  if (own) return join('harness', own[1]);
  const src = /^\.\.\/src\/([A-Za-z0-9_][A-Za-z0-9._-]*\.js)$/.exec(specifier);
  if (src) return join('src', src[1]);
  return null;
}

describe('harness files', () => {
  it('has the page, its script and its styles', () => {
    for (const name of ['index.html', 'harness.js', 'harness.css']) {
      assert.ok(harnessFiles.includes(name), name);
    }
  });

  it('only has file types the service serves from it', () => {
    for (const name of harnessFiles) {
      assert.match(name, /^[A-Za-z0-9_][A-Za-z0-9._-]*\.(html|js|css|json)$/, name);
    }
  });

  for (const module of harnessModules) {
    it(`${module} imports only what the service serves`, () => {
      for (const specifier of importsOf(join('harness', module))) {
        assert.notEqual(served(specifier), null, `${module} imports ${specifier}`);
      }
    });

    it(`${module} imports only files that exist`, () => {
      for (const specifier of importsOf(join('harness', module))) {
        const path = served(specifier);
        if (path !== null) assert.ok(existsSync(join(ROOT, path)), `${module} imports missing ${specifier}`);
      }
    });
  }

  it('loads harness.js from the page as a module', () => {
    assert.match(read('harness/index.html'), /<script type="module" src="harness\.js"><\/script>/);
  });

  it('links the style sheet from the page', () => {
    assert.match(read('harness/index.html'), /<link rel="stylesheet" href="harness\.css" \/>/);
  });

  it('gives page.js the player and the element it looks for', () => {
    const html = read('harness/index.html');
    assert.match(html, /<div id="movie_player">/);
    assert.match(html, /<video class="html5-main-video"/);
  });

  it('runs the real wiring, not a copy', () => {
    const imports = importsOf('harness/harness.js');
    assert.ok(imports.includes('../src/content-app.js'));
    assert.ok(imports.includes('../src/audio-graph.js'));
    assert.ok(imports.includes('../src/relay.js'));
    assert.ok(imports.includes('../src/protocol.js'));
  });

  it('is never touched by the extension itself', () => {
    const shipped = JSON.stringify(manifest);
    assert.equal(shipped.includes('harness'), false);
  });

  it('is not imported by any extension module', () => {
    for (const name of readdirSync(join(ROOT, 'src')).filter((f) => f.endsWith('.js'))) {
      for (const specifier of importsOf(join('src', name))) {
        assert.equal(specifier.includes('harness'), false, `src/${name} imports ${specifier}`);
      }
    }
  });
});

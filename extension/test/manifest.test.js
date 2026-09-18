// Integrity of manifest.json and the files it points at: Chrome refuses to
// load an unpacked extension over a missing file, and a content script module
// missing from web_accessible_resources fails only at run time, on YouTube.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import { dirname, join, posix, relative, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const read = (path) => readFileSync(join(ROOT, path), 'utf8');
const exists = (path) => existsSync(join(ROOT, path)) && statSync(join(ROOT, path)).isFile();
const manifest = JSON.parse(read('manifest.json'));

/** Every file under a directory, as extension-relative posix paths. */
function filesUnder(dir) {
  return readdirSync(join(ROOT, dir), { recursive: true })
    .map((p) => posix.join(dir, String(p).replaceAll('\\', '/')))
    .filter(exists);
}

/** A web_accessible_resources pattern as a regular expression (`*` matches anything). */
function globToRegExp(glob) {
  const escaped = glob.replace(/[.+?^${}()|[\]\\]/g, '\\$&').replaceAll('*', '.*');
  return new RegExp(`^${escaped}$`);
}

/** Relative module specifiers imported (statically or with a literal dynamic import) by a source file. */
function importsOf(path) {
  const source = read(path);
  const specifiers = [
    ...source.matchAll(/^\s*(?:import|export)\s[^'"]*?from\s*['"]([^'"]+)['"]/gm),
    ...source.matchAll(/^\s*import\s*['"]([^'"]+)['"]/gm),
    ...source.matchAll(/import\(\s*['"]([^'"]+)['"]\s*\)/g),
  ].map((m) => m[1]);
  return specifiers.filter((s) => s.startsWith('.')).map((s) => posix.join(posix.dirname(path), s));
}

/** Every module reachable from an entry point through relative imports. */
function reachableFrom(entry) {
  const seen = new Set();
  const queue = [entry];
  while (queue.length > 0) {
    const path = queue.pop();
    if (seen.has(path)) continue;
    seen.add(path);
    if (exists(path)) queue.push(...importsOf(path));
  }
  return [...seen];
}

const warPatterns = (manifest.web_accessible_resources ?? []).flatMap((entry) => entry.resources.map(globToRegExp));
const isWebAccessible = (path) => warPatterns.some((pattern) => pattern.test(path));
const contentScripts = manifest.content_scripts.flatMap((entry) => entry.js);
const LOADER_TARGET = 'src/content.js';

describe('manifest.json', () => {
  it('is Manifest V3', () => {
    assert.equal(manifest.manifest_version, 3);
  });

  it('is named FoulFilter', () => {
    assert.equal(manifest.name, 'FoulFilter');
  });

  it('has a dotted numeric version', () => {
    assert.match(manifest.version, /^\d+(\.\d+){0,3}$/);
  });

  it('has the same version as package.json', () => {
    assert.equal(manifest.version, JSON.parse(read('package.json')).version);
  });

  it('asks for storage and nothing else', () => {
    assert.deepEqual(manifest.permissions, ['storage']);
  });

  it('has host access to YouTube', () => {
    assert.ok(manifest.host_permissions.includes('*://www.youtube.com/*'));
  });

  it('has host access to the default server', () => {
    assert.ok(manifest.host_permissions.includes('http://localhost:8000/*'));
  });

  it('has no other host access at install', () => {
    assert.equal(manifest.host_permissions.length, 2);
  });

  it('can ask for any http(s) origin at run time, for a custom server URL', () => {
    assert.deepEqual(manifest.optional_host_permissions, ['http://*/*', 'https://*/*']);
  });

  it('is not reachable from web pages', () => {
    assert.equal(manifest.externally_connectable, undefined);
  });

  it('declares no remote or inline code allowances', () => {
    assert.equal(manifest.content_security_policy, undefined);
  });

  it('has a toolbar action', () => {
    assert.equal(typeof manifest.action, 'object');
  });
});

describe('service worker', () => {
  it('exists', () => {
    assert.ok(exists(manifest.background.service_worker));
  });

  it('is an ES module', () => {
    assert.equal(manifest.background.type, 'module');
  });

  it('imports only files that exist', () => {
    for (const path of reachableFrom(manifest.background.service_worker)) {
      assert.ok(exists(path), `${path} is imported but missing`);
    }
  });

  it('registers its message listener at the top level', () => {
    assert.match(read(manifest.background.service_worker), /^chrome\.runtime\.onMessage\.addListener\(/m);
  });
});

describe('options page', () => {
  const page = manifest.options_ui.page;
  const html = read(page);
  const scripts = [...html.matchAll(/<script\b([^>]*)>([\s\S]*?)<\/script>/g)];

  it('exists', () => {
    assert.ok(exists(page));
  });

  it('has at least one script', () => {
    assert.ok(scripts.length > 0);
  });

  it('has no inline script (MV3 forbids it)', () => {
    for (const [, , body] of scripts) assert.equal(body.trim(), '');
  });

  it('has no inline event handlers (MV3 forbids them)', () => {
    assert.doesNotMatch(html, /\son[a-z]+\s*=/i);
  });

  it('loads its scripts as modules from files that exist', () => {
    for (const [, attributes] of scripts) {
      assert.match(attributes, /type="module"/);
      const src = /src="([^"]+)"/.exec(attributes)[1];
      assert.ok(exists(posix.join(posix.dirname(page), src)), `${src} is missing`);
    }
  });

  it('loads no remote script', () => {
    assert.doesNotMatch(html, /<script[^>]+src="(https?:)?\/\//);
  });

  it('imports only files that exist', () => {
    for (const [, attributes] of scripts) {
      const src = posix.join(posix.dirname(page), /src="([^"]+)"/.exec(attributes)[1]);
      for (const path of reachableFrom(src)) assert.ok(exists(path), `${path} is imported but missing`);
    }
  });

  it('has a field for every setting', async () => {
    const { DEFAULT_SETTINGS } = await import('../src/settings.js');
    for (const field of Object.keys(DEFAULT_SETTINGS)) {
      assert.match(html, new RegExp(`name="${field}"`), field);
    }
  });

  it('offers every censor method and fail policy the settings accept', async () => {
    const { CENSOR_METHODS, FAIL_POLICIES } = await import('../src/settings.js');
    for (const value of [...CENSOR_METHODS, ...FAIL_POLICIES]) {
      assert.match(html, new RegExp(`<option value="${value}"`), value);
    }
  });
});

describe('content scripts', () => {
  it('run on YouTube only', () => {
    for (const entry of manifest.content_scripts) {
      assert.deepEqual(entry.matches, ['https://www.youtube.com/*']);
    }
  });

  it('exist', () => {
    for (const path of contentScripts) assert.ok(exists(path), `${path} is missing`);
  });

  it('are classic scripts, with no static import or export', () => {
    for (const path of contentScripts) {
      assert.doesNotMatch(read(path), /^\s*(import|export)\s/m, path);
    }
  });

  it('load the module entry point through runtime.getURL', () => {
    for (const path of contentScripts) {
      assert.ok(read(path).includes(`chrome.runtime.getURL('${LOADER_TARGET}')`), path);
    }
  });

  it('load an entry point that exists', () => {
    assert.ok(exists(LOADER_TARGET));
  });
});

describe('web_accessible_resources', () => {
  it('are exposed to YouTube only', () => {
    for (const entry of manifest.web_accessible_resources) {
      assert.deepEqual(entry.matches, ['https://www.youtube.com/*']);
    }
  });

  it('match at least one file each', () => {
    const files = filesUnder('.');
    for (const entry of manifest.web_accessible_resources) {
      for (const resource of entry.resources) {
        const pattern = globToRegExp(resource);
        assert.ok(files.some((f) => pattern.test(relative('.', f).replaceAll('\\', '/'))), resource);
      }
    }
  });

  it('include every module the content script can reach', () => {
    const modules = reachableFrom(LOADER_TARGET);
    assert.ok(modules.includes(LOADER_TARGET));
    for (const path of modules) {
      assert.ok(exists(path), `${path} is imported but missing`);
      assert.ok(isWebAccessible(path), `${path} is imported by the content script but not web accessible`);
    }
  });

  it('do not expose the options page', () => {
    assert.equal(isWebAccessible(manifest.options_ui.page), false);
  });
});

describe('sources', () => {
  it('every relative import in src/ resolves to a file', () => {
    for (const path of filesUnder('src').filter((p) => p.endsWith('.js'))) {
      for (const target of importsOf(path)) assert.ok(exists(target), `${path} imports missing ${target}`);
    }
  });

  it('no source module fetches except the api-client (the service worker relays)', () => {
    for (const path of filesUnder('src').filter((p) => p.endsWith('.js'))) {
      if (path === 'src/api-client.js' || path === 'src/background.js') continue;
      assert.doesNotMatch(read(path), /\bfetch\(/, path);
    }
  });
});

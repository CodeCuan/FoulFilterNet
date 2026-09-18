import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  CENSOR_METHODS,
  DEFAULT_SERVER_URL,
  DEFAULT_SETTINGS,
  FAIL_POLICIES,
  MAX_OFFSET_MS,
  STORAGE_KEY,
  createSettingsStore,
  mergeSettings,
  needsOptionalPermission,
  normaliseServerUrl,
  normaliseSettings,
  originPattern,
  validateSettings,
} from '../src/settings.js';

describe('DEFAULT_SETTINGS', () => {
  it('points at the service on localhost:8000', () => {
    assert.equal(DEFAULT_SETTINGS.serverUrl, 'http://localhost:8000');
  });

  it('is enabled', () => {
    assert.equal(DEFAULT_SETTINGS.enabled, true);
  });

  it('silences', () => {
    assert.equal(DEFAULT_SETTINGS.censorMethod, 'silence');
  });

  it('fails closed', () => {
    assert.equal(DEFAULT_SETTINGS.failPolicy, 'closed');
  });

  it('has no offset', () => {
    assert.equal(DEFAULT_SETTINGS.offsetMs, 0);
  });

  it('has exactly the five fields', () => {
    assert.deepEqual(Object.keys(DEFAULT_SETTINGS).sort(), [
      'censorMethod',
      'enabled',
      'failPolicy',
      'offsetMs',
      'serverUrl',
    ]);
  });

  it('is frozen', () => {
    assert.ok(Object.isFrozen(DEFAULT_SETTINGS));
  });

  it('uses the exported default server URL', () => {
    assert.equal(DEFAULT_SETTINGS.serverUrl, DEFAULT_SERVER_URL);
  });

  it('is itself valid', () => {
    assert.equal(validateSettings(DEFAULT_SETTINGS).ok, true);
  });
});

describe('constants', () => {
  it('offers silence and bleep only', () => {
    assert.deepEqual([...CENSOR_METHODS], ['silence', 'bleep']);
  });

  it('offers fail-closed only in V1', () => {
    assert.deepEqual([...FAIL_POLICIES], ['closed']);
  });

  it('limits the offset to 500 ms', () => {
    assert.equal(MAX_OFFSET_MS, 500);
  });
});

describe('normaliseServerUrl', () => {
  it('accepts the default unchanged', () => {
    assert.deepEqual(normaliseServerUrl('http://localhost:8000'), {
      ok: true,
      url: 'http://localhost:8000',
      warning: null,
    });
  });

  it('drops one trailing slash', () => {
    assert.equal(normaliseServerUrl('http://localhost:8000/').url, 'http://localhost:8000');
  });

  it('drops several trailing slashes', () => {
    assert.equal(normaliseServerUrl('http://localhost:8000///').url, 'http://localhost:8000');
  });

  it('trims surrounding whitespace', () => {
    assert.equal(normaliseServerUrl('  http://localhost:8000/ \n').url, 'http://localhost:8000');
  });

  it('lower-cases the scheme and host', () => {
    assert.equal(normaliseServerUrl('HTTP://LocalHost:8000').url, 'http://localhost:8000');
  });

  it('keeps a path prefix without its trailing slash', () => {
    assert.equal(normaliseServerUrl('http://localhost:8000/ff/').url, 'http://localhost:8000/ff');
  });

  it('drops the default http port', () => {
    assert.equal(normaliseServerUrl('http://localhost:80').url, 'http://localhost');
  });

  it('accepts https', () => {
    assert.equal(normaliseServerUrl('https://localhost:8443').url, 'https://localhost:8443');
  });

  it('accepts 127.0.0.1 without a warning', () => {
    assert.equal(normaliseServerUrl('http://127.0.0.1:9000').warning, null);
  });

  it('accepts [::1] without a warning', () => {
    const result = normaliseServerUrl('http://[::1]:8000');
    assert.deepEqual(result, { ok: true, url: 'http://[::1]:8000', warning: null });
  });

  it('accepts a LAN address but warns', () => {
    const result = normaliseServerUrl('http://192.168.1.20:8000');
    assert.equal(result.ok, true);
    assert.match(result.warning, /192\.168\.1\.20/);
  });

  it('accepts a host name but warns', () => {
    assert.match(normaliseServerUrl('http://media-box:8000').warning, /only answers on localhost/);
  });

  it('refuses an empty string', () => {
    assert.equal(normaliseServerUrl('').ok, false);
  });

  it('refuses whitespace only', () => {
    assert.equal(normaliseServerUrl('   ').ok, false);
  });

  it('refuses a non-string', () => {
    assert.equal(normaliseServerUrl(8000).ok, false);
  });

  it('refuses null', () => {
    assert.equal(normaliseServerUrl(null).ok, false);
  });

  it('refuses a bare host with no scheme', () => {
    assert.equal(normaliseServerUrl('localhost:8000').ok, false);
  });

  it('refuses nonsense', () => {
    assert.equal(normaliseServerUrl('not a url').ok, false);
  });

  it('refuses ftp', () => {
    assert.match(normaliseServerUrl('ftp://localhost').error, /http/);
  });

  it('refuses javascript:', () => {
    assert.equal(normaliseServerUrl('javascript:alert(1)').ok, false);
  });

  it('refuses a file URL', () => {
    assert.equal(normaliseServerUrl('file:///C:/x').ok, false);
  });

  it('refuses credentials', () => {
    assert.match(normaliseServerUrl('http://user:pw@localhost:8000').error, /password/);
  });

  it('refuses a query', () => {
    assert.match(normaliseServerUrl('http://localhost:8000/?a=1').error, /query/);
  });

  it('refuses an empty query', () => {
    assert.equal(normaliseServerUrl('http://localhost:8000/?').ok, false);
  });

  it('refuses a fragment', () => {
    assert.equal(normaliseServerUrl('http://localhost:8000/#top').ok, false);
  });

  it('gives a sentence with every refusal', () => {
    for (const bad of ['', 'x', 'ftp://a', 'http://u@a', 'http://a/?q']) {
      const result = normaliseServerUrl(bad);
      assert.equal(typeof result.error, 'string', bad);
      assert.ok(result.error.length > 10, bad);
    }
  });
});

describe('originPattern', () => {
  it('gives the default server origin with its port', () => {
    assert.equal(originPattern('http://localhost:8000'), 'http://localhost:8000/*');
  });

  it('ignores a path prefix', () => {
    assert.equal(originPattern('http://127.0.0.1:9000/ff'), 'http://127.0.0.1:9000/*');
  });

  it('keeps https', () => {
    assert.equal(originPattern('https://localhost:8443/'), 'https://localhost:8443/*');
  });

  it('keeps IPv6 brackets', () => {
    assert.equal(originPattern('http://[::1]:8000'), 'http://[::1]:8000/*');
  });

  it('omits a default port', () => {
    assert.equal(originPattern('http://localhost:80'), 'http://localhost/*');
  });

  it('is null for an unusable URL', () => {
    assert.equal(originPattern('nope'), null);
  });
});

describe('needsOptionalPermission', () => {
  it('is false for the default server', () => {
    assert.equal(needsOptionalPermission('http://localhost:8000'), false);
  });

  it('is false for the default server with a trailing slash', () => {
    assert.equal(needsOptionalPermission('http://localhost:8000/'), false);
  });

  it('is true for another port', () => {
    assert.equal(needsOptionalPermission('http://localhost:9000'), true);
  });

  it('is true for 127.0.0.1 on the default port', () => {
    assert.equal(needsOptionalPermission('http://127.0.0.1:8000'), true);
  });

  it('is true for https on localhost', () => {
    assert.equal(needsOptionalPermission('https://localhost:8000'), true);
  });

  it('is false for an unusable URL (nothing to ask for)', () => {
    assert.equal(needsOptionalPermission('nope'), false);
  });
});

describe('validateSettings', () => {
  it('gives the defaults for undefined', () => {
    assert.deepEqual(validateSettings(undefined), {
      ok: true,
      settings: { ...DEFAULT_SETTINGS },
      errors: {},
      warnings: {},
    });
  });

  it('gives the defaults for an empty object', () => {
    assert.deepEqual(validateSettings({}).settings, { ...DEFAULT_SETTINGS });
  });

  it('refuses a non-object', () => {
    assert.equal(validateSettings('settings').ok, false);
  });

  it('refuses an array', () => {
    assert.equal(validateSettings([]).ok, false);
  });

  it('drops unknown fields', () => {
    assert.equal('colour' in validateSettings({ colour: 'red' }).settings, false);
  });

  it('does not return the defaults object itself', () => {
    assert.notEqual(validateSettings({}).settings, DEFAULT_SETTINGS);
  });

  it('takes a valid full object as-is', () => {
    const input = {
      serverUrl: 'http://127.0.0.1:9000',
      enabled: false,
      censorMethod: 'bleep',
      failPolicy: 'closed',
      offsetMs: -120,
    };
    assert.deepEqual(validateSettings(input).settings, input);
  });

  it('normalises the server URL', () => {
    assert.equal(validateSettings({ serverUrl: 'http://localhost:9000/' }).settings.serverUrl, 'http://localhost:9000');
  });

  it('reports a bad server URL as an error on that field', () => {
    assert.ok(validateSettings({ serverUrl: 'ftp://x' }).errors.serverUrl);
  });

  it('keeps the fallback server URL when it refuses one', () => {
    assert.equal(validateSettings({ serverUrl: 'ftp://x' }).settings.serverUrl, DEFAULT_SERVER_URL);
  });

  it('passes a server URL warning through', () => {
    const result = validateSettings({ serverUrl: 'http://10.0.0.5:8000' });
    assert.equal(result.ok, true);
    assert.ok(result.warnings.serverUrl);
  });

  it('accepts enabled false', () => {
    assert.equal(validateSettings({ enabled: false }).settings.enabled, false);
  });

  it('refuses enabled as a string', () => {
    assert.ok(validateSettings({ enabled: 'false' }).errors.enabled);
  });

  it('refuses enabled as a number', () => {
    assert.ok(validateSettings({ enabled: 0 }).errors.enabled);
  });

  it('accepts bleep', () => {
    assert.equal(validateSettings({ censorMethod: 'bleep' }).settings.censorMethod, 'bleep');
  });

  it('accepts a censor method in any case', () => {
    assert.equal(validateSettings({ censorMethod: ' Bleep ' }).settings.censorMethod, 'bleep');
  });

  it('turns remove into silence (ADR-0004)', () => {
    assert.equal(validateSettings({ censorMethod: 'remove' }).settings.censorMethod, 'silence');
  });

  it('warns when it turns remove into silence', () => {
    const result = validateSettings({ censorMethod: 'remove' });
    assert.equal(result.ok, true);
    assert.ok(result.warnings.censorMethod);
  });

  it('refuses an unknown censor method', () => {
    assert.ok(validateSettings({ censorMethod: 'mute' }).errors.censorMethod);
  });

  it('refuses a non-string censor method', () => {
    assert.ok(validateSettings({ censorMethod: 1 }).errors.censorMethod);
  });

  it('accepts fail policy closed', () => {
    assert.equal(validateSettings({ failPolicy: 'closed' }).ok, true);
  });

  it('refuses fail policy open (not in V1)', () => {
    assert.ok(validateSettings({ failPolicy: 'open' }).errors.failPolicy);
  });

  it('accepts a positive offset', () => {
    assert.equal(validateSettings({ offsetMs: 120 }).settings.offsetMs, 120);
  });

  it('accepts a negative offset', () => {
    assert.equal(validateSettings({ offsetMs: -80 }).settings.offsetMs, -80);
  });

  it('rounds a fractional offset', () => {
    assert.equal(validateSettings({ offsetMs: 12.6 }).settings.offsetMs, 13);
  });

  it('reads a numeric string offset (form inputs give strings)', () => {
    assert.equal(validateSettings({ offsetMs: ' -40 ' }).settings.offsetMs, -40);
  });

  it('accepts exactly +500', () => {
    const result = validateSettings({ offsetMs: 500 });
    assert.equal(result.settings.offsetMs, 500);
    assert.deepEqual(result.warnings, {});
  });

  it('accepts exactly -500', () => {
    assert.equal(validateSettings({ offsetMs: -500 }).settings.offsetMs, -500);
  });

  it('clamps an offset above +500', () => {
    assert.equal(validateSettings({ offsetMs: 501 }).settings.offsetMs, 500);
  });

  it('clamps an offset below -500', () => {
    assert.equal(validateSettings({ offsetMs: -9000 }).settings.offsetMs, -500);
  });

  it('warns but stays ok when it clamps', () => {
    const result = validateSettings({ offsetMs: 2000 });
    assert.equal(result.ok, true);
    assert.ok(result.warnings.offsetMs);
  });

  it('never gives negative zero', () => {
    assert.ok(Object.is(validateSettings({ offsetMs: -0.2 }).settings.offsetMs, 0));
  });

  it('refuses NaN', () => {
    assert.ok(validateSettings({ offsetMs: Number.NaN }).errors.offsetMs);
  });

  it('refuses infinity', () => {
    assert.ok(validateSettings({ offsetMs: Number.POSITIVE_INFINITY }).errors.offsetMs);
  });

  it('refuses an empty string offset', () => {
    assert.ok(validateSettings({ offsetMs: '' }).errors.offsetMs);
  });

  it('refuses a non-numeric string offset', () => {
    assert.ok(validateSettings({ offsetMs: 'soon' }).errors.offsetMs);
  });

  it('refuses a null offset', () => {
    assert.ok(validateSettings({ offsetMs: null }).errors.offsetMs);
  });

  it('reports every bad field at once', () => {
    const result = validateSettings({ serverUrl: 'x', enabled: 'y', censorMethod: 'z', failPolicy: 'w', offsetMs: 'v' });
    assert.deepEqual(Object.keys(result.errors).sort(), [
      'censorMethod',
      'enabled',
      'failPolicy',
      'offsetMs',
      'serverUrl',
    ]);
  });

  it('keeps the good fields when another is bad', () => {
    const result = validateSettings({ censorMethod: 'bleep', offsetMs: 'x' });
    assert.equal(result.settings.censorMethod, 'bleep');
  });

  it('is not ok when any field is refused', () => {
    assert.equal(validateSettings({ offsetMs: 'x' }).ok, false);
  });

  it('takes missing fields from the fallback it is given', () => {
    const fallback = { ...DEFAULT_SETTINGS, censorMethod: 'bleep' };
    assert.equal(validateSettings({}, fallback).settings.censorMethod, 'bleep');
  });
});

describe('normaliseSettings', () => {
  it('gives the defaults for nothing stored', () => {
    assert.deepEqual(normaliseSettings(undefined), { ...DEFAULT_SETTINGS });
  });

  it('gives the defaults for garbage', () => {
    assert.deepEqual(normaliseSettings(42), { ...DEFAULT_SETTINGS });
  });

  it('keeps the good stored fields and defaults the bad ones', () => {
    assert.deepEqual(normaliseSettings({ censorMethod: 'bleep', serverUrl: 'gopher://x' }), {
      ...DEFAULT_SETTINGS,
      censorMethod: 'bleep',
    });
  });

  it('fills in a field added after the settings were saved', () => {
    assert.equal(normaliseSettings({ enabled: false }).offsetMs, 0);
  });
});

describe('mergeSettings', () => {
  const current = { ...DEFAULT_SETTINGS, censorMethod: 'bleep', offsetMs: 40 };

  it('changes only the patched field', () => {
    assert.deepEqual(mergeSettings(current, { enabled: false }).settings, { ...current, enabled: false });
  });

  it('keeps everything for an empty patch', () => {
    assert.deepEqual(mergeSettings(current, {}).settings, current);
  });

  it('treats a non-object patch as empty', () => {
    assert.deepEqual(mergeSettings(current, null).settings, current);
  });

  it('keeps the current value of a refused field', () => {
    assert.equal(mergeSettings(current, { offsetMs: 'x' }).settings.offsetMs, 40);
  });

  it('reports the refused field', () => {
    assert.ok(mergeSettings(current, { offsetMs: 'x' }).errors.offsetMs);
  });

  it('repairs a bad current value from the defaults', () => {
    assert.equal(mergeSettings({ ...current, serverUrl: 'bad' }, {}).settings.serverUrl, DEFAULT_SERVER_URL);
  });

  it('does not mutate the current settings', () => {
    const before = { ...current };
    mergeSettings(current, { enabled: false, offsetMs: 9 });
    assert.deepEqual(current, before);
  });
});

/** A fake chrome.storage.sync plus chrome.storage.onChanged. */
function fakeStorage(initial = {}) {
  const items = { ...initial };
  const listeners = new Set();
  const calls = { get: [], set: [] };
  return {
    items,
    calls,
    listeners,
    area: {
      async get(keys) {
        calls.get.push(keys);
        const wanted = Array.isArray(keys) ? keys : [keys];
        return Object.fromEntries(wanted.filter((k) => k in items).map((k) => [k, structuredClone(items[k])]));
      },
      async set(values) {
        calls.set.push(structuredClone(values));
        const changes = {};
        for (const [k, v] of Object.entries(values)) {
          changes[k] = { oldValue: items[k], newValue: structuredClone(v) };
          items[k] = structuredClone(v);
        }
        for (const listener of listeners) listener(changes, 'sync');
      },
    },
    onChanged: {
      addListener: (l) => listeners.add(l),
      removeListener: (l) => listeners.delete(l),
    },
    emit(changes, areaName) {
      for (const listener of listeners) listener(changes, areaName);
    },
  };
}

describe('createSettingsStore.load', () => {
  it('gives the defaults when nothing is stored', async () => {
    const storage = fakeStorage();
    assert.deepEqual(await createSettingsStore(storage).load(), { ...DEFAULT_SETTINGS });
  });

  it('reads the one settings key', async () => {
    const storage = fakeStorage();
    await createSettingsStore(storage).load();
    assert.deepEqual(storage.calls.get, [STORAGE_KEY]);
  });

  it('gives what is stored', async () => {
    const stored = { ...DEFAULT_SETTINGS, enabled: false, censorMethod: 'bleep' };
    const storage = fakeStorage({ [STORAGE_KEY]: stored });
    assert.deepEqual(await createSettingsStore(storage).load(), stored);
  });

  it('normalises what is stored', async () => {
    const storage = fakeStorage({ [STORAGE_KEY]: { serverUrl: 'http://localhost:9000/', offsetMs: 9999 } });
    const settings = await createSettingsStore(storage).load();
    assert.equal(settings.serverUrl, 'http://localhost:9000');
    assert.equal(settings.offsetMs, 500);
  });

  it('gives the defaults when the storage rejects', async () => {
    const area = { get: async () => { throw new Error('quota'); }, set: async () => {} };
    assert.deepEqual(await createSettingsStore({ area }).load(), { ...DEFAULT_SETTINGS });
  });

  it('gives the defaults when the storage returns nothing at all', async () => {
    const area = { get: async () => undefined, set: async () => {} };
    assert.deepEqual(await createSettingsStore({ area }).load(), { ...DEFAULT_SETTINGS });
  });
});

describe('createSettingsStore.save', () => {
  it('writes the merged settings under the one key', async () => {
    const storage = fakeStorage();
    await createSettingsStore(storage).save({ censorMethod: 'bleep' });
    assert.deepEqual(storage.calls.set, [{ [STORAGE_KEY]: { ...DEFAULT_SETTINGS, censorMethod: 'bleep' } }]);
  });

  it('keeps fields saved earlier', async () => {
    const storage = fakeStorage();
    const store = createSettingsStore(storage);
    await store.save({ censorMethod: 'bleep' });
    await store.save({ offsetMs: 30 });
    assert.deepEqual(await store.load(), { ...DEFAULT_SETTINGS, censorMethod: 'bleep', offsetMs: 30 });
  });

  it('returns ok with the saved settings', async () => {
    const result = await createSettingsStore(fakeStorage()).save({ enabled: false });
    assert.equal(result.ok, true);
    assert.equal(result.settings.enabled, false);
  });

  it('saves the normalised form', async () => {
    const storage = fakeStorage();
    await createSettingsStore(storage).save({ serverUrl: ' http://127.0.0.1:9000/ ' });
    assert.equal(storage.items[STORAGE_KEY].serverUrl, 'http://127.0.0.1:9000');
  });

  it('writes nothing when a field is refused', async () => {
    const storage = fakeStorage();
    await createSettingsStore(storage).save({ enabled: true, offsetMs: 'x' });
    assert.equal(storage.calls.set.length, 0);
  });

  it('returns the errors when a field is refused', async () => {
    const result = await createSettingsStore(fakeStorage()).save({ serverUrl: 'ftp://x' });
    assert.equal(result.ok, false);
    assert.ok(result.errors.serverUrl);
  });

  it('saves and returns warnings for an accepted-with-warning value', async () => {
    const storage = fakeStorage();
    const result = await createSettingsStore(storage).save({ serverUrl: 'http://nas:8000' });
    assert.equal(storage.calls.set.length, 1);
    assert.ok(result.warnings.serverUrl);
  });

  it('does not store unknown fields', async () => {
    const storage = fakeStorage();
    await createSettingsStore(storage).save({ extra: 1 });
    assert.equal('extra' in storage.items[STORAGE_KEY], false);
  });

  it('propagates a storage write failure', async () => {
    const area = { get: async () => ({}), set: async () => { throw new Error('quota'); } };
    await assert.rejects(createSettingsStore({ area }).save({ enabled: false }), /quota/);
  });
});

describe('createSettingsStore.subscribe', () => {
  it('tells the listener about a save, with next and previous settings', async () => {
    const storage = fakeStorage();
    const store = createSettingsStore(storage);
    const seen = [];
    store.subscribe((next, previous) => seen.push([next.censorMethod, previous.censorMethod]));
    await store.save({ censorMethod: 'bleep' });
    assert.deepEqual(seen, [['bleep', 'silence']]);
  });

  it('normalises the values it passes on', () => {
    const storage = fakeStorage();
    const seen = [];
    createSettingsStore(storage).subscribe((next) => seen.push(next));
    storage.emit({ [STORAGE_KEY]: { newValue: { offsetMs: 7777 } } }, 'sync');
    assert.equal(seen[0].offsetMs, 500);
  });

  it('gives the defaults when the settings key is removed', () => {
    const storage = fakeStorage();
    const seen = [];
    createSettingsStore(storage).subscribe((next) => seen.push(next));
    storage.emit({ [STORAGE_KEY]: { oldValue: { enabled: false } } }, 'sync');
    assert.deepEqual(seen, [{ ...DEFAULT_SETTINGS }]);
  });

  it('ignores changes to other keys', () => {
    const storage = fakeStorage();
    let calls = 0;
    createSettingsStore(storage).subscribe(() => calls++);
    storage.emit({ other: { newValue: 1 } }, 'sync');
    assert.equal(calls, 0);
  });

  it('ignores changes in another storage area', () => {
    const storage = fakeStorage();
    let calls = 0;
    createSettingsStore(storage).subscribe(() => calls++);
    storage.emit({ [STORAGE_KEY]: { newValue: {} } }, 'local');
    assert.equal(calls, 0);
  });

  it('listens to the area name it is given', () => {
    const storage = fakeStorage();
    let calls = 0;
    createSettingsStore({ ...storage, areaName: 'local' }).subscribe(() => calls++);
    storage.emit({ [STORAGE_KEY]: { newValue: {} } }, 'local');
    assert.equal(calls, 1);
  });

  it('stops after unsubscribing', async () => {
    const storage = fakeStorage();
    const store = createSettingsStore(storage);
    let calls = 0;
    const unsubscribe = store.subscribe(() => calls++);
    unsubscribe();
    await store.save({ enabled: false });
    assert.equal(calls, 0);
  });

  it('removes its own listener on unsubscribe', () => {
    const storage = fakeStorage();
    createSettingsStore(storage).subscribe(() => {})();
    assert.equal(storage.listeners.size, 0);
  });

  it('keeps other subscribers when one unsubscribes', async () => {
    const storage = fakeStorage();
    const store = createSettingsStore(storage);
    let kept = 0;
    store.subscribe(() => {})();
    store.subscribe(() => kept++);
    await store.save({ enabled: false });
    assert.equal(kept, 1);
  });

  it('is a harmless no-op without a change event', () => {
    const store = createSettingsStore({ area: fakeStorage().area });
    const unsubscribe = store.subscribe(() => assert.fail('no events expected'));
    assert.equal(typeof unsubscribe, 'function');
    unsubscribe();
  });
});

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createRelay } from '../src/relay.js';
import { cancelMessage, configMessage, getMessage, heartbeatMessage } from '../src/protocol.js';
import { DEFAULT_SETTINGS } from '../src/settings.js';

const VIDEO = { provider: 'youtube', videoId: 'YwARwww5aFo' };

const VIEW = {
  key: 'youtube-YwARwww5aFo',
  provider: 'youtube',
  video_id: 'YwARwww5aFo',
  session: 's1',
  state: 'complete',
  reason: null,
  failure_kind: null,
  title: null,
  duration: 649.2,
  revision: 1,
  unchanged: false,
  coverage: [[0, 646.3]],
  hits: [],
  windows_done: 1,
  windows_total: 1,
  progress: 1,
  realtime_factor: null,
  keeping_up: true,
  from_cache: true,
};

const CONFIG = {
  censor_methods: ['silence', 'bleep', 'remove'],
  ai_enhance: false,
  whisper_model: 'large-v3-turbo',
  max_upload_mb: 2048,
  web_video: { available: false, yt_dlp_version: null, deno_version: null, problem: 'yt-dlp was not found.' },
};

function json(status, body) {
  const text = body === undefined ? '' : JSON.stringify(body);
  return { ok: status >= 200 && status < 300, status, statusText: '', text: async () => text };
}

/** A relay over a recording fetch that answers by method and path. */
function relay({ settings = DEFAULT_SETTINGS, loadSettings } = {}) {
  const calls = [];
  const fetch = async (url, init) => {
    calls.push({ url, method: init.method, body: init.body });
    if (url.endsWith('/config')) return json(200, CONFIG);
    if (init.method === 'DELETE') return json(204);
    return json(200, VIEW);
  };
  const r = createRelay({
    loadSettings: loadSettings ?? (async () => settings),
    fetch,
    setTimeout: () => 0,
    clearTimeout: () => {},
  });
  return { handle: r.handle, calls };
}

describe('relay', () => {
  it('relays a heartbeat as POST /watch to the saved server', async () => {
    const { handle, calls } = relay();
    await handle(heartbeatMessage({ ...VIDEO, position: 3 }));
    assert.deepEqual(calls.map((c) => [c.method, c.url]), [['POST', 'http://localhost:8000/watch']]);
  });

  it('carries the playhead into the body', async () => {
    const { handle, calls } = relay();
    await handle(heartbeatMessage({ ...VIDEO, position: 3.25 }));
    assert.equal(JSON.parse(calls[0].body).position, 3.25);
  });

  it('carries since and session into the query', async () => {
    const { handle, calls } = relay();
    await handle(heartbeatMessage({ ...VIDEO, position: 3, since: 4, session: 's1' }));
    assert.equal(calls[0].url, 'http://localhost:8000/watch?since=4&session=s1');
  });

  it('replies to a heartbeat with the mapped view', async () => {
    const { handle } = relay();
    const reply = await handle(heartbeatMessage({ ...VIDEO, position: 3 }));
    assert.equal(reply.ok, true);
    assert.equal(reply.view.videoId, 'YwARwww5aFo');
    assert.equal(reply.view.fromCache, true);
  });

  it('relays a get as GET /watch/youtube/{id}', async () => {
    const { handle, calls } = relay();
    await handle(getMessage(VIDEO));
    assert.deepEqual(calls.map((c) => [c.method, c.url]), [['GET', 'http://localhost:8000/watch/youtube/YwARwww5aFo']]);
  });

  it('relays a cancel as DELETE', async () => {
    const { handle, calls } = relay();
    const reply = await handle(cancelMessage(VIDEO));
    assert.equal(calls[0].method, 'DELETE');
    assert.deepEqual(reply, { ok: true, cancelled: true });
  });

  it('relays a config to the saved server', async () => {
    const { handle, calls } = relay();
    const reply = await handle(configMessage());
    assert.equal(calls[0].url, 'http://localhost:8000/config');
    assert.equal(reply.config.webVideo.problem, 'yt-dlp was not found.');
  });

  it('uses the server URL in the settings', async () => {
    const { handle, calls } = relay({ settings: { ...DEFAULT_SETTINGS, serverUrl: 'http://127.0.0.1:9000' } });
    await handle(getMessage(VIDEO));
    assert.equal(calls[0].url, 'http://127.0.0.1:9000/watch/youtube/YwARwww5aFo');
  });

  it('reads the settings afresh for every message', async () => {
    let url = 'http://localhost:8000';
    const { handle, calls } = relay({ loadSettings: async () => ({ ...DEFAULT_SETTINGS, serverUrl: url }) });
    await handle(configMessage());
    url = 'http://localhost:9000';
    await handle(configMessage());
    assert.deepEqual(calls.map((c) => c.url), ['http://localhost:8000/config', 'http://localhost:9000/config']);
  });

  it('still relays when filtering is disabled (the options page tests the connection)', async () => {
    const { handle, calls } = relay({ settings: { ...DEFAULT_SETTINGS, enabled: false } });
    await handle(configMessage());
    assert.equal(calls.length, 1);
  });

  it('falls back to the default server when the settings cannot be read', async () => {
    const { handle, calls } = relay({
      loadSettings: async () => {
        throw new Error('storage gone');
      },
    });
    await handle(configMessage());
    assert.equal(calls[0].url, 'http://localhost:8000/config');
  });

  it('tests a config against a URL it is given, not the saved one', async () => {
    const { handle, calls } = relay({ settings: { ...DEFAULT_SETTINGS, serverUrl: 'http://localhost:8000' } });
    await handle(configMessage({ serverUrl: 'http://127.0.0.1:9000/' }));
    assert.equal(calls[0].url, 'http://127.0.0.1:9000/config');
  });

  it('refuses a config for an unusable URL without fetching', async () => {
    const { handle, calls } = relay();
    const reply = await handle(configMessage({ serverUrl: 'ftp://x' }));
    assert.equal(reply.error.kind, 'invalid_request');
    assert.equal(calls.length, 0);
  });

  it('refuses a malformed message without fetching', async () => {
    const { handle, calls } = relay();
    const reply = await handle({ type: 'ff/heartbeat', ...VIDEO });
    assert.equal(reply.error.kind, 'invalid_request');
    assert.equal(calls.length, 0);
  });

  it('refuses an unknown message type', async () => {
    const { handle } = relay();
    assert.equal((await handle({ type: 'ff/unknown' })).error.kind, 'invalid_request');
  });

  it('refuses a bad video ID without fetching', async () => {
    const { handle, calls } = relay();
    const reply = await handle(heartbeatMessage({ provider: 'youtube', videoId: '--exec=cal', position: 0 }));
    assert.equal(reply.error.kind, 'invalid_request');
    assert.equal(calls.length, 0);
  });

  it('passes an API failure through as the reply', async () => {
    const r = createRelay({
      loadSettings: async () => DEFAULT_SETTINGS,
      fetch: async () => Promise.reject(new TypeError('Failed to fetch')),
      setTimeout: () => 0,
      clearTimeout: () => {},
    });
    const reply = await r.handle(configMessage());
    assert.equal(reply.ok, false);
    assert.equal(reply.error.kind, 'unreachable');
  });

  it('replies with a structured-cloneable value', async () => {
    const { handle } = relay();
    const reply = await handle(heartbeatMessage({ ...VIDEO, position: 3 }));
    assert.deepEqual(structuredClone(reply), reply);
  });
});

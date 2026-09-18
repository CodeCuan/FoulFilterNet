import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  DEFAULT_TIMEOUT_MS,
  PROVIDERS,
  WATCH_STATES,
  createApiClient,
  parseConfig,
  parseWatchView,
} from '../src/api-client.js';

const SERVER = 'http://localhost:8000';
const VIDEO = { provider: 'youtube', videoId: 'YwARwww5aFo' };

/** A WatchView exactly as W10 writes it. */
function wireView(overrides = {}) {
  return {
    key: 'youtube-YwARwww5aFo',
    provider: 'youtube',
    video_id: 'YwARwww5aFo',
    session: '0f8fad5bd9cb469fa16570867728950e',
    state: 'transcribing',
    reason: null,
    failure_kind: null,
    title: 'Why Most Data Projects Fail',
    duration: 649.2,
    revision: 3,
    unchanged: false,
    coverage: [
      [0, 46],
      [90, 112.5],
    ],
    hits: [{ start: 12.35, end: 13.1, phrase: 'long' }],
    windows_done: 3,
    windows_total: 30,
    progress: 0.1,
    realtime_factor: 25.1,
    keeping_up: true,
    from_cache: false,
    ...overrides,
  };
}

/** The same view, camelCased, as the client returns it. */
function domainView(overrides = {}) {
  return {
    key: 'youtube-YwARwww5aFo',
    provider: 'youtube',
    videoId: 'YwARwww5aFo',
    session: '0f8fad5bd9cb469fa16570867728950e',
    state: 'transcribing',
    reason: null,
    failureKind: null,
    title: 'Why Most Data Projects Fail',
    duration: 649.2,
    revision: 3,
    unchanged: false,
    coverage: [
      [0, 46],
      [90, 112.5],
    ],
    hits: [{ start: 12.35, end: 13.1, phrase: 'long' }],
    windowsDone: 3,
    windowsTotal: 30,
    progress: 0.1,
    realtimeFactor: 25.1,
    keepingUp: true,
    fromCache: false,
    ...overrides,
  };
}

const CONFIG = {
  censor_methods: ['silence', 'bleep', 'remove'],
  ai_enhance: false,
  whisper_model: 'large-v3-turbo',
  max_upload_mb: 2048,
  web_video: { available: true, yt_dlp_version: '2026.08.19', deno_version: '2.9.6', problem: null },
};

function response(status, body, statusText = '') {
  const text = typeof body === 'string' ? body : body === undefined ? '' : JSON.stringify(body);
  return { ok: status >= 200 && status < 300, status, statusText, text: async () => text };
}

/** A fetch that records each call and answers from a script (a value, a function, or a thrown error). */
function fakeFetch(answer) {
  const calls = [];
  const fetch = async (url, init) => {
    calls.push({ url, init });
    const next = typeof answer === 'function' ? answer(url, init) : answer;
    return next instanceof Error ? Promise.reject(next) : next;
  };
  return { fetch, calls };
}

/** Timers that only fire when the test says so. */
function fakeTimers() {
  const timers = new Map();
  let nextId = 1;
  return {
    setTimeout: (fn, ms) => {
      const id = nextId++;
      timers.set(id, { fn, ms });
      return id;
    },
    clearTimeout: (id) => timers.delete(id),
    fireAll() {
      for (const [id, { fn }] of [...timers]) {
        timers.delete(id);
        fn();
      }
    },
    timers,
  };
}

function client(answer, extra = {}) {
  const f = fakeFetch(answer);
  const timers = fakeTimers();
  const api = createApiClient({ serverUrl: SERVER, fetch: f.fetch, ...timers, ...extra });
  return { api, calls: f.calls, timers };
}

describe('createApiClient', () => {
  it('defaults the timeout to 3 s', () => {
    assert.equal(DEFAULT_TIMEOUT_MS, 3000);
    assert.equal(createApiClient({ serverUrl: SERVER, fetch: async () => {} }).timeoutMs, 3000);
  });

  it('drops a trailing slash from the server URL', () => {
    assert.equal(createApiClient({ serverUrl: `${SERVER}/`, fetch: async () => {} }).serverUrl, SERVER);
  });

  it('knows youtube as the only provider', () => {
    assert.deepEqual([...PROVIDERS], ['youtube']);
  });

  it('knows the eight states W10 sends', () => {
    assert.deepEqual([...WATCH_STATES], [
      'queued',
      'fetching',
      'preparing',
      'transcribing',
      'complete',
      'failed',
      'unsupported',
      'cancelled',
    ]);
  });
});

describe('heartbeat request', () => {
  it('POSTs to /watch', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 12.5 });
    assert.equal(calls[0].url, `${SERVER}/watch`);
    assert.equal(calls[0].init.method, 'POST');
  });

  it('sends a snake_case JSON body', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 12.5 });
    assert.deepEqual(JSON.parse(calls[0].init.body), { provider: 'youtube', video_id: 'YwARwww5aFo', position: 12.5 });
  });

  it('sends only provider, video_id and position in the body', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1, since: 3, session: 'abc' });
    assert.deepEqual(Object.keys(JSON.parse(calls[0].init.body)), ['provider', 'video_id', 'position']);
  });

  it('says the body is JSON (the service answers 415 otherwise)', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 0 });
    assert.equal(calls[0].init.headers['Content-Type'], 'application/json');
  });

  it('asks for JSON back', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 0 });
    assert.equal(calls[0].init.headers.Accept, 'application/json');
  });

  it('passes an abort signal', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 0 });
    assert.ok(calls[0].init.signal instanceof AbortSignal);
  });

  it('adds since and session when both are known', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1, since: 3, session: 'abc' });
    assert.equal(calls[0].url, `${SERVER}/watch?since=3&session=abc`);
  });

  it('adds since 0', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1, since: 0, session: 'abc' });
    assert.equal(calls[0].url, `${SERVER}/watch?since=0&session=abc`);
  });

  it('leaves out the query when only since is known', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1, since: 3 });
    assert.equal(calls[0].url, `${SERVER}/watch`);
  });

  it('leaves out the query when only session is known', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1, session: 'abc' });
    assert.equal(calls[0].url, `${SERVER}/watch`);
  });

  it('leaves out the query when since is null', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1, since: null, session: 'abc' });
    assert.equal(calls[0].url, `${SERVER}/watch`);
  });

  it('leaves out the query when session is null', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1, since: 3, session: null });
    assert.equal(calls[0].url, `${SERVER}/watch`);
  });

  it('escapes the session in the query', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1, since: 3, session: 'a&b=c' });
    assert.equal(calls[0].url, `${SERVER}/watch?since=3&session=a%26b%3Dc`);
  });

  it('keeps a path prefix on the server URL', async () => {
    const { api, calls } = client(response(200, wireView()), { serverUrl: 'http://127.0.0.1:9000/ff/' });
    await api.heartbeat({ ...VIDEO, position: 1 });
    assert.equal(calls[0].url, 'http://127.0.0.1:9000/ff/watch');
  });

  it('accepts a video ID that starts with a dash', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.heartbeat({ provider: 'youtube', videoId: '-abcdefghij', position: 0 });
    assert.equal(JSON.parse(calls[0].init.body).video_id, '-abcdefghij');
  });

  it('accepts position 0', async () => {
    const { api } = client(response(200, wireView()));
    assert.equal((await api.heartbeat({ ...VIDEO, position: 0 })).ok, true);
  });
});

describe('heartbeat refuses a bad request without fetching', () => {
  const cases = [
    ['no arguments', undefined],
    ['a missing position', { ...VIDEO }],
    ['a negative position', { ...VIDEO, position: -0.1 }],
    ['a NaN position', { ...VIDEO, position: Number.NaN }],
    ['an infinite position', { ...VIDEO, position: Number.POSITIVE_INFINITY }],
    ['a string position', { ...VIDEO, position: '12' }],
    ['another provider', { provider: 'vimeo', videoId: 'YwARwww5aFo', position: 0 }],
    ['an upper-case provider', { provider: 'YouTube', videoId: 'YwARwww5aFo', position: 0 }],
    ['a short video ID', { provider: 'youtube', videoId: 'abc', position: 0 }],
    ['a long video ID', { provider: 'youtube', videoId: 'YwARwww5aFoX', position: 0 }],
    ['a video ID with a slash', { provider: 'youtube', videoId: 'YwARww/5aFo', position: 0 }],
    ['a URL as the video ID', { provider: 'youtube', videoId: 'https://youtu.be/x', position: 0 }],
    ['an option-looking video ID', { provider: 'youtube', videoId: '--exec=cal', position: 0 }],
    ['a fractional since', { ...VIDEO, position: 0, since: 1.5, session: 'a' }],
    ['a negative since', { ...VIDEO, position: 0, since: -1, session: 'a' }],
    ['a string since', { ...VIDEO, position: 0, since: '3', session: 'a' }],
    ['an empty session', { ...VIDEO, position: 0, since: 3, session: '' }],
    ['a numeric session', { ...VIDEO, position: 0, since: 3, session: 7 }],
  ];

  for (const [name, args] of cases) {
    it(`for ${name}`, async () => {
      const { api, calls } = client(response(200, wireView()));
      const result = await api.heartbeat(args);
      assert.equal(result.ok, false);
      assert.equal(result.error.kind, 'invalid_request');
      assert.equal(typeof result.error.detail, 'string');
      assert.equal(calls.length, 0);
    });
  }
});

describe('heartbeat success', () => {
  it('maps the view to camelCase', async () => {
    const { api } = client(response(200, wireView()));
    assert.deepEqual(await api.heartbeat({ ...VIDEO, position: 1 }), { ok: true, view: domainView() });
  });

  it('maps an unchanged answer with null hits', async () => {
    const { api } = client(response(200, wireView({ unchanged: true, hits: null })));
    const result = await api.heartbeat({ ...VIDEO, position: 1, since: 3, session: 'x' });
    assert.deepEqual(result.view.hits, null);
    assert.equal(result.view.unchanged, true);
  });

  it('stops its timer once answered', async () => {
    const { api, timers } = client(response(200, wireView()));
    await api.heartbeat({ ...VIDEO, position: 1 });
    assert.equal(timers.timers.size, 0);
  });

  it('sets its timer to the timeout it was given', async () => {
    let seen;
    const api = createApiClient({
      serverUrl: SERVER,
      fetch: async () => response(200, wireView()),
      timeoutMs: 1234,
      setTimeout: (fn, ms) => ((seen = ms), 1),
      clearTimeout: () => {},
    });
    await api.heartbeat({ ...VIDEO, position: 1 });
    assert.equal(seen, 1234);
  });
});

describe('errors as values', () => {
  it('reports a rejected fetch as unreachable', async () => {
    const { api } = client(new TypeError('Failed to fetch'));
    const result = await api.heartbeat({ ...VIDEO, position: 1 });
    assert.equal(result.error.kind, 'unreachable');
  });

  it('includes the fetch message in the unreachable detail', async () => {
    const { api } = client(new TypeError('Failed to fetch'));
    const result = await api.heartbeat({ ...VIDEO, position: 1 });
    assert.match(result.error.detail, /Failed to fetch/);
  });

  it('names the server in the unreachable detail', async () => {
    const { api } = client(new TypeError('x'));
    assert.match((await api.config()).error.detail, /localhost:8000/);
  });

  it('reports a fetch rejecting with a non-Error as unreachable', async () => {
    const { api } = client(() => Promise.reject('boom'));
    const result = await api.heartbeat({ ...VIDEO, position: 1 });
    assert.equal(result.error.kind, 'unreachable');
    assert.match(result.error.detail, /boom/);
  });

  it('reports a fetch that throws synchronously as unreachable', async () => {
    const api = createApiClient({
      serverUrl: SERVER,
      fetch: () => {
        throw new TypeError('bad URL');
      },
      ...fakeTimers(),
    });
    assert.equal((await api.config()).error.kind, 'unreachable');
  });

  it('reports an abort as timeout', async () => {
    const { api } = client(new DOMException('The operation was aborted.', 'AbortError'));
    assert.equal((await api.heartbeat({ ...VIDEO, position: 1 })).error.kind, 'timeout');
  });

  it('reports a TimeoutError as timeout', async () => {
    const { api } = client(new DOMException('timed out', 'TimeoutError'));
    assert.equal((await api.config()).error.kind, 'timeout');
  });

  it('aborts the request when its timer fires, and reports timeout', async () => {
    const timers = fakeTimers();
    let signal;
    const fetch = (url, init) =>
      new Promise((resolve, reject) => {
        signal = init.signal;
        init.signal.addEventListener('abort', () => reject(new TypeError('network error')));
      });
    const api = createApiClient({ serverUrl: SERVER, fetch, ...timers });
    const pending = api.heartbeat({ ...VIDEO, position: 1 });
    await Promise.resolve();
    timers.fireAll();
    const result = await pending;
    assert.equal(signal.aborted, true);
    assert.equal(result.error.kind, 'timeout');
  });

  it('names the timeout in the timeout detail', async () => {
    const { api } = client(new DOMException('x', 'AbortError'), { timeoutMs: 2500 });
    assert.match((await api.config()).error.detail, /2500 ms/);
  });

  it('reports a timeout while reading the body as timeout', async () => {
    const timers = fakeTimers();
    const fetch = async (url, init) => ({
      ok: true,
      status: 200,
      statusText: 'OK',
      text: () =>
        new Promise((resolve, reject) => init.signal.addEventListener('abort', () => reject(new DOMException('a', 'AbortError')))),
    });
    const api = createApiClient({ serverUrl: SERVER, fetch, ...timers });
    const pending = api.config();
    await new Promise((r) => setImmediate(r));
    timers.fireAll();
    assert.equal((await pending).error.kind, 'timeout');
  });

  it('reports a body that cannot be read as bad_response', async () => {
    const api = createApiClient({
      serverUrl: SERVER,
      fetch: async () => ({ ok: true, status: 200, statusText: 'OK', text: async () => { throw new Error('stream broke'); } }),
      ...fakeTimers(),
    });
    const result = await api.config();
    assert.equal(result.error.kind, 'bad_response');
    assert.match(result.error.detail, /stream broke/);
  });

  it('stops its timer after a failure', async () => {
    const { api, timers } = client(new TypeError('x'));
    await api.config();
    assert.equal(timers.timers.size, 0);
  });

  for (const status of [400, 404, 415, 500, 503]) {
    it(`reports ${status} as http with its status`, async () => {
      const { api } = client(response(status, { detail: 'no' }));
      const result = await api.heartbeat({ ...VIDEO, position: 1 });
      assert.deepEqual(result, { ok: false, error: { kind: 'http', status, detail: 'no' } });
    });
  }

  it('takes the detail from the service\'s {detail} body', async () => {
    const { api } = client(response(400, { detail: 'position is required: the playhead in seconds.' }));
    const result = await api.heartbeat({ ...VIDEO, position: 1 });
    assert.equal(result.error.detail, 'position is required: the playhead in seconds.');
  });

  it('falls back to the status text when the error body is not JSON', async () => {
    const { api } = client(response(502, '<html>Bad gateway</html>', 'Bad Gateway'));
    assert.equal((await api.config()).error.detail, '502 Bad Gateway');
  });

  it('falls back to the status when there is no status text', async () => {
    const { api } = client(response(500, ''));
    assert.equal((await api.config()).error.detail, 'HTTP 500');
  });

  it('falls back when detail is not a string', async () => {
    const { api } = client(response(400, { detail: 42 }, 'Bad Request'));
    assert.equal((await api.config()).error.detail, '400 Bad Request');
  });

  it('falls back when detail is empty', async () => {
    const { api } = client(response(400, { detail: '' }, 'Bad Request'));
    assert.equal((await api.config()).error.detail, '400 Bad Request');
  });

  it('reports a 200 that is not JSON as bad_response', async () => {
    const { api } = client(response(200, '<html>Hello</html>'));
    const result = await api.heartbeat({ ...VIDEO, position: 1 });
    assert.equal(result.error.kind, 'bad_response');
    assert.equal(result.error.status, 200);
  });

  it('reports an empty 200 as bad_response', async () => {
    const { api } = client(response(200, ''));
    assert.equal((await api.heartbeat({ ...VIDEO, position: 1 })).error.kind, 'bad_response');
  });

  it('reports a 200 with the wrong shape as bad_response', async () => {
    const { api } = client(response(200, { hello: 'world' }));
    const result = await api.heartbeat({ ...VIDEO, position: 1 });
    assert.equal(result.error.kind, 'bad_response');
    assert.match(result.error.detail, /shape/);
  });

  it('never rejects, whatever fetch does', async () => {
    const answers = [new TypeError('x'), new DOMException('x', 'AbortError'), response(500, ''), response(200, 'x')];
    for (const answer of answers) {
      const { api } = client(answer);
      await api.heartbeat({ ...VIDEO, position: 1 });
      await api.get(VIDEO);
      await api.cancel(VIDEO);
      await api.config();
    }
  });
});

describe('get', () => {
  it('GETs /watch/{provider}/{id}', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.get(VIDEO);
    assert.equal(calls[0].url, `${SERVER}/watch/youtube/YwARwww5aFo`);
    assert.equal(calls[0].init.method, 'GET');
  });

  it('sends no body', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.get(VIDEO);
    assert.equal(calls[0].init.body, undefined);
  });

  it('sends no Content-Type', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.get(VIDEO);
    assert.equal(calls[0].init.headers['Content-Type'], undefined);
  });

  it('adds since and session when both are known', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.get({ ...VIDEO, since: 7, session: 'abc' });
    assert.equal(calls[0].url, `${SERVER}/watch/youtube/YwARwww5aFo?since=7&session=abc`);
  });

  it('leaves out the query when only one is known', async () => {
    const { api, calls } = client(response(200, wireView()));
    await api.get({ ...VIDEO, since: 7 });
    assert.equal(calls[0].url, `${SERVER}/watch/youtube/YwARwww5aFo`);
  });

  it('does not need a position', async () => {
    const { api } = client(response(200, wireView()));
    assert.equal((await api.get(VIDEO)).ok, true);
  });

  it('maps the view', async () => {
    const { api } = client(response(200, wireView()));
    assert.deepEqual(await api.get(VIDEO), { ok: true, view: domainView() });
  });

  it('reports no session as http 404', async () => {
    const { api } = client(response(404, { detail: 'No Watch Session for this video.' }));
    assert.deepEqual(await api.get(VIDEO), {
      ok: false,
      error: { kind: 'http', status: 404, detail: 'No Watch Session for this video.' },
    });
  });

  it('refuses a bad video ID without fetching', async () => {
    const { api, calls } = client(response(200, wireView()));
    assert.equal((await api.get({ provider: 'youtube', videoId: '../config' })).error.kind, 'invalid_request');
    assert.equal(calls.length, 0);
  });
});

describe('cancel', () => {
  it('DELETEs /watch/{provider}/{id}', async () => {
    const { api, calls } = client(response(204, ''));
    await api.cancel(VIDEO);
    assert.equal(calls[0].url, `${SERVER}/watch/youtube/YwARwww5aFo`);
    assert.equal(calls[0].init.method, 'DELETE');
  });

  it('sends no body', async () => {
    const { api, calls } = client(response(204, ''));
    await api.cancel(VIDEO);
    assert.equal(calls[0].init.body, undefined);
  });

  it('reports 204 as cancelled', async () => {
    const { api } = client(response(204, ''));
    assert.deepEqual(await api.cancel(VIDEO), { ok: true, cancelled: true });
  });

  it('reports 404 as nothing to cancel, not an error', async () => {
    const { api } = client(response(404, { detail: 'No Watch Session for this video.' }));
    assert.deepEqual(await api.cancel(VIDEO), { ok: true, cancelled: false });
  });

  it('reports a 500 as http', async () => {
    const { api } = client(response(500, { detail: 'boom' }));
    assert.deepEqual(await api.cancel(VIDEO), { ok: false, error: { kind: 'http', status: 500, detail: 'boom' } });
  });

  it('reports a rejected fetch as unreachable', async () => {
    const { api } = client(new TypeError('x'));
    assert.equal((await api.cancel(VIDEO)).error.kind, 'unreachable');
  });

  it('refuses a bad provider without fetching', async () => {
    const { api, calls } = client(response(204, ''));
    assert.equal((await api.cancel({ provider: 'file', videoId: 'YwARwww5aFo' })).error.kind, 'invalid_request');
    assert.equal(calls.length, 0);
  });
});

describe('config', () => {
  it('GETs /config', async () => {
    const { api, calls } = client(response(200, CONFIG));
    await api.config();
    assert.equal(calls[0].url, `${SERVER}/config`);
    assert.equal(calls[0].init.method, 'GET');
  });

  it('maps the config to camelCase', async () => {
    const { api } = client(response(200, CONFIG));
    assert.deepEqual(await api.config(), {
      ok: true,
      config: {
        censorMethods: ['silence', 'bleep', 'remove'],
        aiEnhance: false,
        whisperModel: 'large-v3-turbo',
        maxUploadMb: 2048,
        webVideo: { available: true, ytDlpVersion: '2026.08.19', denoVersion: '2.9.6', problem: null },
      },
    });
  });

  it('reports a config with no web_video (an older service) as bad_response', async () => {
    const { web_video: _, ...old } = CONFIG;
    const { api } = client(response(200, old));
    assert.equal((await api.config()).error.kind, 'bad_response');
  });
});

describe('parseConfig', () => {
  it('keeps the problem when web video is unavailable', () => {
    const config = parseConfig({
      ...CONFIG,
      web_video: { available: false, yt_dlp_version: null, deno_version: null, problem: 'yt-dlp was not found.' },
    });
    assert.deepEqual(config.webVideo, { available: false, ytDlpVersion: null, denoVersion: null, problem: 'yt-dlp was not found.' });
  });

  const bad = [
    ['null', null],
    ['an array', []],
    ['censor_methods not an array', { ...CONFIG, censor_methods: 'silence' }],
    ['a non-string censor method', { ...CONFIG, censor_methods: [1] }],
    ['ai_enhance not boolean', { ...CONFIG, ai_enhance: 'no' }],
    ['whisper_model not a string', { ...CONFIG, whisper_model: null }],
    ['max_upload_mb fractional', { ...CONFIG, max_upload_mb: 1.5 }],
    ['web_video null', { ...CONFIG, web_video: null }],
    ['available missing', { ...CONFIG, web_video: { yt_dlp_version: null, deno_version: null, problem: null } }],
    ['yt_dlp_version a number', { ...CONFIG, web_video: { ...CONFIG.web_video, yt_dlp_version: 2026 } }],
    ['problem missing', { ...CONFIG, web_video: { available: true, yt_dlp_version: 'x', deno_version: 'y' } }],
  ];
  for (const [name, json] of bad) {
    it(`refuses ${name}`, () => {
      assert.equal(parseConfig(json), null);
    });
  }
});

describe('parseWatchView', () => {
  it('maps every field', () => {
    assert.deepEqual(parseWatchView(wireView()), domainView());
  });

  it('ignores extra fields', () => {
    assert.deepEqual(parseWatchView(wireView({ transcript: [] })), domainView());
  });

  for (const state of WATCH_STATES) {
    it(`accepts state ${state}`, () => {
      assert.equal(parseWatchView(wireView({ state })).state, state);
    });
  }

  it('accepts a failed session with its reason and failure kind', () => {
    const view = parseWatchView(
      wireView({ state: 'failed', reason: 'Video unavailable', failure_kind: 'unavailable', coverage: [], hits: [] }),
    );
    assert.equal(view.reason, 'Video unavailable');
    assert.equal(view.failureKind, 'unavailable');
  });

  it('accepts a failure kind it does not know yet (advice falls back to generic)', () => {
    assert.equal(parseWatchView(wireView({ failure_kind: 'rate_limited' })).failureKind, 'rate_limited');
  });

  it('accepts a queued session with nothing known', () => {
    const view = parseWatchView(
      wireView({
        state: 'queued',
        title: null,
        duration: null,
        revision: 0,
        coverage: [],
        hits: [],
        windows_done: 0,
        windows_total: 0,
        progress: 0,
        realtime_factor: null,
        keeping_up: true,
      }),
    );
    assert.equal(view.duration, null);
    assert.equal(view.realtimeFactor, null);
    assert.deepEqual(view.coverage, []);
  });

  it('accepts a cached, complete session with a null title', () => {
    const view = parseWatchView(
      wireView({ state: 'complete', title: null, from_cache: true, windows_done: 1, windows_total: 1, progress: 1, revision: 1 }),
    );
    assert.equal(view.fromCache, true);
    assert.equal(view.title, null);
  });

  it('accepts an empty session id (a hand-built snapshot)', () => {
    assert.equal(parseWatchView(wireView({ session: '' })).session, '');
  });

  it('accepts touching coverage intervals', () => {
    assert.deepEqual(parseWatchView(wireView({ coverage: [[0, 10], [10, 20]] })).coverage, [
      [0, 10],
      [10, 20],
    ]);
  });

  it('accepts a zero-length coverage interval', () => {
    assert.equal(parseWatchView(wireView({ coverage: [[5, 5]] })).coverage.length, 1);
  });

  it('accepts a hit that starts before 0 (padding at the very start)', () => {
    assert.equal(parseWatchView(wireView({ hits: [{ start: -0.15, end: 0.4, phrase: 'x' }] })).hits[0].start, -0.15);
  });

  it('accepts a zero-length hit', () => {
    assert.equal(parseWatchView(wireView({ hits: [{ start: 3, end: 3, phrase: '' }] })).hits.length, 1);
  });

  it('accepts hits and unchanged together', () => {
    assert.equal(parseWatchView(wireView({ unchanged: true })).hits.length, 1);
  });

  it('accepts windows_done equal to windows_total', () => {
    assert.equal(parseWatchView(wireView({ windows_done: 30, windows_total: 30, progress: 1 })).windowsDone, 30);
  });

  it('accepts progress exactly 1', () => {
    assert.equal(parseWatchView(wireView({ progress: 1 })).progress, 1);
  });

  it('drops extra fields on hits', () => {
    assert.deepEqual(parseWatchView(wireView({ hits: [{ start: 1, end: 2, phrase: 'p', extra: true }] })).hits, [
      { start: 1, end: 2, phrase: 'p' },
    ]);
  });

  it('does not share arrays with the wire object', () => {
    const wire = wireView();
    const view = parseWatchView(wire);
    assert.notEqual(view.coverage, wire.coverage);
    assert.notEqual(view.hits, wire.hits);
  });

  const bad = [
    ['null', null],
    ['a string', 'view'],
    ['an array', []],
    ['a missing key', wireView({ key: undefined })],
    ['an empty key', wireView({ key: '' })],
    ['a missing provider', wireView({ provider: undefined })],
    ['a missing video_id', wireView({ video_id: undefined })],
    ['a null session', wireView({ session: null })],
    ['an unknown state', wireView({ state: 'resolving' })],
    ['an upper-case state', wireView({ state: 'Complete' })],
    ['a missing state', wireView({ state: undefined })],
    ['a numeric reason', wireView({ reason: 1 })],
    ['a missing reason (the service writes nulls)', wireView({ reason: undefined })],
    ['a numeric failure_kind', wireView({ failure_kind: 2 })],
    ['a numeric title', wireView({ title: 3 })],
    ['a string duration', wireView({ duration: '649' })],
    ['a negative duration', wireView({ duration: -1 })],
    ['a missing duration', wireView({ duration: undefined })],
    ['a fractional revision', wireView({ revision: 1.5 })],
    ['a negative revision', wireView({ revision: -1 })],
    ['a null revision', wireView({ revision: null })],
    ['a string unchanged', wireView({ unchanged: 'false' })],
    ['null coverage', wireView({ coverage: null })],
    ['coverage as an object', wireView({ coverage: { from: 0, to: 1 } })],
    ['a coverage pair of one number', wireView({ coverage: [[0]] })],
    ['a coverage pair of three numbers', wireView({ coverage: [[0, 1, 2]] })],
    ['a coverage pair as an object', wireView({ coverage: [{ from: 0, to: 1 }] })],
    ['a coverage string', wireView({ coverage: [['0', '1']] })],
    ['a backwards coverage interval', wireView({ coverage: [[10, 5]] })],
    ['a negative coverage start', wireView({ coverage: [[-1, 5]] })],
    ['a NaN in coverage', wireView({ coverage: [[0, Number.NaN]] })],
    ['an infinite coverage end', wireView({ coverage: [[0, Number.POSITIVE_INFINITY]] })],
    ['overlapping coverage intervals', wireView({ coverage: [[0, 10], [5, 20]] })],
    ['coverage out of order', wireView({ coverage: [[20, 30], [0, 10]] })],
    ['null hits when not unchanged', wireView({ hits: null })],
    ['missing hits', wireView({ hits: undefined })],
    ['hits as an object', wireView({ hits: {} })],
    ['a null hit', wireView({ hits: [null] })],
    ['a hit without an end', wireView({ hits: [{ start: 1, phrase: 'x' }] })],
    ['a hit with a string start', wireView({ hits: [{ start: '1', end: 2, phrase: 'x' }] })],
    ['a backwards hit', wireView({ hits: [{ start: 2, end: 1, phrase: 'x' }] })],
    ['a hit with a NaN end', wireView({ hits: [{ start: 1, end: Number.NaN, phrase: 'x' }] })],
    ['a hit with no phrase', wireView({ hits: [{ start: 1, end: 2 }] })],
    ['a hit with a null phrase', wireView({ hits: [{ start: 1, end: 2, phrase: null }] })],
    ['a fractional windows_done', wireView({ windows_done: 1.5 })],
    ['a negative windows_total', wireView({ windows_total: -1 })],
    ['more windows done than there are', wireView({ windows_done: 31, windows_total: 30 })],
    ['progress above 1', wireView({ progress: 1.01 })],
    ['negative progress', wireView({ progress: -0.01 })],
    ['a null progress', wireView({ progress: null })],
    ['a negative realtime_factor', wireView({ realtime_factor: -1 })],
    ['a string realtime_factor', wireView({ realtime_factor: 'fast' })],
    ['a null keeping_up', wireView({ keeping_up: null })],
    ['a missing from_cache', wireView({ from_cache: undefined })],
  ];
  for (const [name, json] of bad) {
    it(`refuses ${name}`, () => {
      assert.equal(parseWatchView(json), null);
    });
  }
});

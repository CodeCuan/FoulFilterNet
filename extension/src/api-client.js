// Talks to the FoulFilterNet service's Watch endpoints and /config (W10).
//
// Pure apart from the fetch and timers it is handed. Every call resolves to a
// value - `{ ok: true, ... }` or `{ ok: false, error }` - and never rejects,
// so the service worker can pass the answer straight back to the page.

import { isVideoId } from './video-id.js';

/**
 * @typedef {'queued' | 'fetching' | 'preparing' | 'transcribing' | 'complete' | 'failed' | 'unsupported' | 'cancelled'} WatchState
 *
 * @typedef {object} Hit
 * @property {number} start  Seconds on the video's timeline, padding included.
 * @property {number} end
 * @property {string} phrase  For a debug overlay; censoring goes by time alone.
 *
 * @typedef {object} WatchView  A Watch Session as the service reports it, camelCased.
 * @property {string} key  `youtube-<id>`.
 * @property {string} provider
 * @property {string} videoId
 * @property {string} session  Changes when the service replaces the session; echo it back with `since`.
 * @property {WatchState} state
 * @property {string | null} reason
 * @property {string | null} failureKind  failed, not_installed, js_runtime_missing, unavailable, sign_in_required, unsupported, network - or a newer one.
 * @property {string | null} title  Null while fetching, and always for a cached video.
 * @property {number | null} duration  Seconds; null until known.
 * @property {number} revision
 * @property {boolean} unchanged  The caller already holds this session's revision; `hits` is null.
 * @property {Array<[number, number]>} coverage  Ascending, non-overlapping `[from, to]` seconds.
 * @property {Hit[] | null} hits  Null only when `unchanged`.
 * @property {number} windowsDone
 * @property {number} windowsTotal
 * @property {number} progress  0 to 1.
 * @property {number | null} realtimeFactor
 * @property {boolean} keepingUp
 * @property {boolean} fromCache
 *
 * @typedef {object} WebVideoConfig
 * @property {boolean} available
 * @property {string | null} ytDlpVersion
 * @property {string | null} denoVersion
 * @property {string | null} problem  What to tell the user when not available.
 *
 * @typedef {object} ServerConfig  `GET /config`, camelCased.
 * @property {string[]} censorMethods
 * @property {boolean} aiEnhance
 * @property {string} whisperModel
 * @property {number} maxUploadMb
 * @property {WebVideoConfig} webVideo
 *
 * @typedef {'unreachable' | 'timeout' | 'http' | 'bad_response' | 'invalid_request'} ErrorKind
 *
 * @typedef {object} ApiError
 * @property {ErrorKind} kind
 * @property {number} [status]  The HTTP status, for `http` (and `bad_response` when there was one).
 * @property {string} [detail]  A sentence: the service's `detail`, or what went wrong.
 *
 * @typedef {{ ok: false, error: ApiError }} Failure
 * @typedef {{ ok: true, view: WatchView } | Failure} ViewResult
 * @typedef {{ ok: true, cancelled: boolean } | Failure} CancelResult
 * @typedef {{ ok: true, config: ServerConfig } | Failure} ConfigResult
 *
 * @typedef {object} VideoArgs
 * @property {string} provider  `youtube`.
 * @property {string} videoId  11 characters from `[A-Za-z0-9_-]`.
 *
 * @typedef {VideoArgs & { since?: number | null, session?: string | null }} ReadArgs
 * @typedef {ReadArgs & { position: number }} HeartbeatArgs
 *
 * @typedef {(input: string, init?: RequestInit) => Promise<Response>} FetchLike
 * @typedef {Pick<Response, 'ok' | 'status' | 'statusText' | 'text'>} ResponseLike
 */

export const DEFAULT_TIMEOUT_MS = 3000;

/** @type {readonly WatchState[]} */
export const WATCH_STATES = Object.freeze([
  'queued',
  'fetching',
  'preparing',
  'transcribing',
  'complete',
  'failed',
  'unsupported',
  'cancelled',
]);

/** Providers the service accepts (VideoRef). */
export const PROVIDERS = Object.freeze(['youtube']);

/**
 * @param {object} options
 * @param {string} options.serverUrl  Normalised, no trailing slash (see settings.js).
 * @param {FetchLike} options.fetch
 * @param {number} [options.timeoutMs]  Per request, reading the body included.
 * @param {typeof setTimeout} [options.setTimeout]
 * @param {typeof clearTimeout} [options.clearTimeout]
 */
export function createApiClient({
  serverUrl,
  fetch,
  timeoutMs = DEFAULT_TIMEOUT_MS,
  setTimeout: startTimer = globalThis.setTimeout,
  clearTimeout: stopTimer = globalThis.clearTimeout,
}) {
  const base = String(serverUrl).replace(/\/+$/, '');

  /**
   * One request, with the body read inside the timeout.
   *
   * @param {string} method
   * @param {string} path
   * @param {unknown} [body]
   * @returns {Promise<{ ok: true, status: number, text: string } | Failure>}
   */
  async function send(method, path, body) {
    const controller = new AbortController();
    let timedOut = false;
    const timer = startTimer(() => {
      timedOut = true;
      controller.abort();
    }, timeoutMs);

    /** @type {RequestInit} */
    const init = { method, headers: { Accept: 'application/json' }, signal: controller.signal };
    if (body !== undefined) {
      init.headers = { ...init.headers, 'Content-Type': 'application/json' };
      init.body = JSON.stringify(body);
    }

    try {
      /** @type {ResponseLike} */
      let response;
      try {
        response = await fetch(`${base}${path}`, init);
      } catch (error) {
        return timedOut || isAbort(error)
          ? failure('timeout', { detail: `No answer from ${base} within ${timeoutMs} ms.` })
          : failure('unreachable', { detail: `Could not reach ${base}: ${messageOf(error)}` });
      }

      let text;
      try {
        text = await response.text();
      } catch (error) {
        return timedOut || isAbort(error)
          ? failure('timeout', { detail: `No answer from ${base} within ${timeoutMs} ms.` })
          : failure('bad_response', { status: response.status, detail: `Could not read the answer: ${messageOf(error)}` });
      }

      if (!response.ok) {
        return failure('http', { status: response.status, detail: detailOf(text, response) });
      }
      return { ok: true, status: response.status, text };
    } finally {
      stopTimer(timer);
    }
  }

  /**
   * @template T
   * @param {string} method
   * @param {string} path
   * @param {unknown} body
   * @param {(json: unknown) => T | null} parse
   * @returns {Promise<{ ok: true, value: T } | Failure>}
   */
  async function sendForJson(method, path, body, parse) {
    const answer = await send(method, path, body);
    if (!answer.ok) {
      return answer;
    }
    let json;
    try {
      json = JSON.parse(answer.text);
    } catch {
      return failure('bad_response', { status: answer.status, detail: 'The answer was not JSON.' });
    }
    const value = parse(json);
    return value === null
      ? failure('bad_response', { status: answer.status, detail: 'The answer did not have the expected shape.' })
      : { ok: true, value };
  }

  /**
   * `POST /watch`: start the session if there is none, record the playhead.
   *
   * @param {HeartbeatArgs} args
   * @returns {Promise<ViewResult>}
   */
  async function heartbeat(args) {
    const problem = checkVideo(args) ?? checkPosition(args?.position) ?? checkSince(args);
    if (problem) {
      return failure('invalid_request', { detail: problem });
    }
    const body = { provider: args.provider, video_id: args.videoId, position: args.position };
    return asView(await sendForJson('POST', `/watch${query(args)}`, body, parseWatchView));
  }

  /**
   * `GET /watch/{provider}/{id}`: the session as it stands, with no side
   * effects. A 404 (no session) is an `http` error with status 404.
   *
   * @param {ReadArgs} args
   * @returns {Promise<ViewResult>}
   */
  async function get(args) {
    const problem = checkVideo(args) ?? checkSince(args);
    if (problem) {
      return failure('invalid_request', { detail: problem });
    }
    return asView(await sendForJson('GET', `${videoPath(args)}${query(args)}`, undefined, parseWatchView));
  }

  /**
   * `DELETE /watch/{provider}/{id}`. A 404 is not an error: there was nothing
   * to cancel, so `cancelled` is false.
   *
   * @param {VideoArgs} args
   * @returns {Promise<CancelResult>}
   */
  async function cancel(args) {
    const problem = checkVideo(args);
    if (problem) {
      return failure('invalid_request', { detail: problem });
    }
    const answer = await send('DELETE', videoPath(args));
    if (answer.ok) {
      return { ok: true, cancelled: true };
    }
    if (answer.error.kind === 'http' && answer.error.status === 404) {
      return { ok: true, cancelled: false };
    }
    return answer;
  }

  /**
   * `GET /config`, including whether web video can work on the server.
   *
   * @returns {Promise<ConfigResult>}
   */
  async function config() {
    const result = await sendForJson('GET', '/config', undefined, parseConfig);
    return result.ok ? { ok: true, config: result.value } : result;
  }

  return { heartbeat, get, cancel, config, serverUrl: base, timeoutMs };
}

/**
 * Map the service's snake_case WatchView onto the extension's shape, or null
 * when anything the extension relies on is missing or of the wrong kind.
 * Nulls are accepted exactly where the service writes them.
 *
 * @param {unknown} json
 * @returns {WatchView | null}
 */
export function parseWatchView(json) {
  if (!isObject(json)) return null;
  const j = json;

  if (!isNonEmptyString(j.key) || !isNonEmptyString(j.provider) || !isNonEmptyString(j.video_id)) return null;
  if (typeof j.session !== 'string') return null;
  if (!WATCH_STATES.includes(/** @type {WatchState} */ (j.state))) return null;
  if (!isStringOrNull(j.reason) || !isStringOrNull(j.failure_kind) || !isStringOrNull(j.title)) return null;
  if (!(j.duration === null || isNonNegative(j.duration))) return null;
  if (!isCount(j.revision)) return null;
  if (typeof j.unchanged !== 'boolean') return null;

  const coverage = parseCoverage(j.coverage);
  if (coverage === null) return null;

  /** @type {Hit[] | null} */
  let hits = null;
  if (j.hits === null) {
    if (!j.unchanged) return null;
  } else {
    hits = parseHits(j.hits);
    if (hits === null) return null;
  }

  if (!isCount(j.windows_done) || !isCount(j.windows_total) || j.windows_done > j.windows_total) return null;
  if (!isNonNegative(j.progress) || j.progress > 1) return null;
  if (!(j.realtime_factor === null || isNonNegative(j.realtime_factor))) return null;
  if (typeof j.keeping_up !== 'boolean' || typeof j.from_cache !== 'boolean') return null;

  return {
    key: j.key,
    provider: j.provider,
    videoId: j.video_id,
    session: j.session,
    state: /** @type {WatchState} */ (j.state),
    reason: j.reason,
    failureKind: j.failure_kind,
    title: j.title,
    duration: j.duration,
    revision: j.revision,
    unchanged: j.unchanged,
    coverage,
    hits,
    windowsDone: j.windows_done,
    windowsTotal: j.windows_total,
    progress: j.progress,
    realtimeFactor: j.realtime_factor,
    keepingUp: j.keeping_up,
    fromCache: j.from_cache,
  };
}

/**
 * @param {unknown} json
 * @returns {ServerConfig | null}
 */
export function parseConfig(json) {
  if (!isObject(json)) return null;
  const j = json;
  if (!Array.isArray(j.censor_methods) || !j.censor_methods.every((m) => typeof m === 'string')) return null;
  if (typeof j.ai_enhance !== 'boolean' || typeof j.whisper_model !== 'string' || !isCount(j.max_upload_mb)) return null;

  const w = j.web_video;
  if (!isObject(w) || typeof w.available !== 'boolean') return null;
  if (!isStringOrNull(w.yt_dlp_version) || !isStringOrNull(w.deno_version) || !isStringOrNull(w.problem)) return null;

  return {
    censorMethods: [...j.censor_methods],
    aiEnhance: j.ai_enhance,
    whisperModel: j.whisper_model,
    maxUploadMb: j.max_upload_mb,
    webVideo: {
      available: w.available,
      ytDlpVersion: w.yt_dlp_version,
      denoVersion: w.deno_version,
      problem: w.problem,
    },
  };
}

/**
 * @param {unknown} value
 * @returns {Array<[number, number]> | null}
 */
function parseCoverage(value) {
  if (!Array.isArray(value)) return null;
  /** @type {Array<[number, number]>} */
  const intervals = [];
  let previousEnd = -Infinity;
  for (const pair of value) {
    if (!Array.isArray(pair) || pair.length !== 2) return null;
    const [from, to] = pair;
    if (!isNonNegative(from) || !isFiniteNumber(to) || to < from || from < previousEnd) return null;
    intervals.push([from, to]);
    previousEnd = to;
  }
  return intervals;
}

/**
 * @param {unknown} value
 * @returns {Hit[] | null}
 */
function parseHits(value) {
  if (!Array.isArray(value)) return null;
  /** @type {Hit[]} */
  const hits = [];
  for (const hit of value) {
    if (!isObject(hit)) return null;
    const { start, end, phrase } = hit;
    if (!isFiniteNumber(start) || !isFiniteNumber(end) || end < start || typeof phrase !== 'string') return null;
    hits.push({ start, end, phrase });
  }
  return hits;
}

/**
 * @param {{ ok: true, value: WatchView } | Failure} result
 * @returns {ViewResult}
 */
function asView(result) {
  return result.ok ? { ok: true, view: result.value } : result;
}

/** @param {VideoArgs} args */
function videoPath(args) {
  return `/watch/${encodeURIComponent(args.provider)}/${encodeURIComponent(args.videoId)}`;
}

/**
 * `?since=&session=` only when both are known: the service ignores a bare
 * `since`, and a bare revision could belong to a replaced session anyway.
 *
 * @param {ReadArgs} args
 */
function query(args) {
  if (!hasValue(args.since) || !hasValue(args.session)) {
    return '';
  }
  return `?since=${args.since}&session=${encodeURIComponent(/** @type {string} */ (args.session))}`;
}

/**
 * @param {unknown} args
 * @returns {string | null}
 */
function checkVideo(args) {
  if (!isObject(args)) return 'Expected {provider, videoId}.';
  if (!PROVIDERS.includes(/** @type {string} */ (args.provider))) {
    return `provider must be one of: ${PROVIDERS.join(', ')}.`;
  }
  if (!isVideoId(args.videoId)) {
    return 'videoId must be 11 characters from A-Z, a-z, 0-9, _ and -.';
  }
  return null;
}

/**
 * @param {unknown} position
 * @returns {string | null}
 */
function checkPosition(position) {
  return isNonNegative(position) ? null : 'position must be a finite number of seconds, 0 or more.';
}

/**
 * @param {ReadArgs} args
 * @returns {string | null}
 */
function checkSince(args) {
  if (hasValue(args.since) && !isCount(args.since)) {
    return 'since must be a revision the service sent: a whole number, 0 or more.';
  }
  if (hasValue(args.session) && (typeof args.session !== 'string' || args.session === '')) {
    return 'session must be a session id the service sent.';
  }
  return null;
}

/**
 * The service's `{ "detail": ... }`, else the status text, else a fallback.
 *
 * @param {string} text
 * @param {ResponseLike} response
 */
function detailOf(text, response) {
  try {
    const json = JSON.parse(text);
    if (isObject(json) && typeof json.detail === 'string' && json.detail !== '') {
      return json.detail;
    }
  } catch {
    // Not JSON: fall through.
  }
  return response.statusText ? `${response.status} ${response.statusText}` : `HTTP ${response.status}`;
}

/**
 * @param {ErrorKind} kind
 * @param {{ status?: number, detail?: string }} [extra]
 * @returns {Failure}
 */
function failure(kind, extra = {}) {
  return { ok: false, error: { kind, ...extra } };
}

/** @param {unknown} error */
function isAbort(error) {
  return isObject(error) && (error.name === 'AbortError' || error.name === 'TimeoutError');
}

/** @param {unknown} error */
function messageOf(error) {
  return isObject(error) && typeof error.message === 'string' && error.message !== '' ? error.message : String(error);
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, any>}
 */
function isObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

/** @param {unknown} value */
function hasValue(value) {
  return value !== undefined && value !== null;
}

/**
 * @param {unknown} value
 * @returns {value is string}
 */
function isNonEmptyString(value) {
  return typeof value === 'string' && value !== '';
}

/**
 * @param {unknown} value
 * @returns {value is string | null}
 */
function isStringOrNull(value) {
  return value === null || typeof value === 'string';
}

/**
 * @param {unknown} value
 * @returns {value is number}
 */
function isFiniteNumber(value) {
  return typeof value === 'number' && Number.isFinite(value);
}

/**
 * @param {unknown} value
 * @returns {value is number}
 */
function isNonNegative(value) {
  return isFiniteNumber(value) && value >= 0;
}

/**
 * @param {unknown} value
 * @returns {value is number}
 */
function isCount(value) {
  return Number.isSafeInteger(value) && /** @type {number} */ (value) >= 0;
}

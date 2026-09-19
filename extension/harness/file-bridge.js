// The harness's stand-ins for the parts of Chrome the content script needs
// (W16). Pure apart from what is handed in.
//
// The extension only knows YouTube: page.js reads a watch URL, session.js
// heartbeats `youtube` + an 11-character ID, and api-client.js refuses any
// other provider. The harness plays a file served by the dev file provider
// (`file` + its name), so it gives the content script a made-up YouTube ID
// and translates at the one seam where the request leaves: the `fetch` the
// relay's api-client calls. Requests for the fake ID go to `file/<name>`;
// views of `file/<name>` come back as the fake ID. Everything else passes
// through untouched, so a mistake shows up as the extension's own
// `bad_response` rather than being papered over.

/** The made-up YouTube ID the harness's page claims to be watching (11 characters). */
export const FAKE_VIDEO_ID = 'ffHarness01';

/** The watch URL page.js reads for it. */
export const FAKE_WATCH_URL = `https://www.youtube.com/watch?v=${FAKE_VIDEO_ID}`;

/** The dev file provider's name on the service. */
export const FILE_PROVIDER = 'file';

/** HTTP statuses whose response may not carry a body. */
const NULL_BODY_STATUSES = new Set([101, 103, 204, 205, 304]);

/**
 * @typedef {(input: string, init?: RequestInit) => Promise<Response>} FetchLike
 */

/**
 * A `fetch` that turns the fake YouTube video into the dev file and back.
 *
 * @param {object} options
 * @param {FetchLike} options.fetch  The real fetch.
 * @param {string} options.fileName  The file in the dev directory.
 * @param {string} [options.fakeId]
 * @param {typeof Response} [options.Response]
 * @returns {FetchLike}
 */
export function createFileBridge({ fetch, fileName, fakeId = FAKE_VIDEO_ID, Response: ResponseCtor = Response }) {
  const fakePath = `/watch/youtube/${fakeId}`;
  const filePath = `/watch/${FILE_PROVIDER}/${encodeURIComponent(fileName)}`;

  /** @param {string} input */
  function requestUrl(input) {
    const url = new URL(String(input));
    if (url.pathname === fakePath) url.pathname = filePath;
    return url.toString();
  }

  /** @param {BodyInit | null | undefined} body */
  function requestBody(body) {
    if (typeof body !== 'string') return body;
    let parsed;
    try {
      parsed = JSON.parse(body);
    } catch {
      return body;
    }
    if (parsed && typeof parsed === 'object' && parsed.provider === 'youtube' && parsed.video_id === fakeId) {
      return JSON.stringify({ ...parsed, provider: FILE_PROVIDER, video_id: fileName });
    }
    return body;
  }

  /** @param {string} text */
  function responseText(text) {
    let parsed;
    try {
      parsed = JSON.parse(text);
    } catch {
      return text;
    }
    if (parsed && typeof parsed === 'object' && parsed.provider === FILE_PROVIDER && parsed.video_id === fileName) {
      return JSON.stringify({ ...parsed, key: `youtube-${fakeId}`, provider: 'youtube', video_id: fakeId });
    }
    return text;
  }

  return async function bridgedFetch(input, init = {}) {
    const response = await fetch(requestUrl(input), { ...init, body: requestBody(init.body) });
    const text = await response.text();
    const body = NULL_BODY_STATUSES.has(response.status) ? null : responseText(text);
    return new ResponseCtor(body, { status: response.status, statusText: response.statusText, headers: response.headers });
  };
}

/**
 * The `window` page.js reads: the fake watch URL as `location`, and the real
 * window's events and MutationObserver.
 *
 * @param {Window} win
 * @param {string} [href]
 * @returns {Window}
 */
export function harnessWindow(win, href = FAKE_WATCH_URL) {
  return /** @type {any} */ ({
    location: Object.freeze({ href }),
    addEventListener: (/** @type {any[]} */ ...args) => win.addEventListener(.../** @type {[any, any, any]} */ (args)),
    removeEventListener: (/** @type {any[]} */ ...args) => win.removeEventListener(.../** @type {[any, any, any]} */ (args)),
    MutationObserver: /** @type {any} */ (win).MutationObserver,
  });
}

/**
 * A settings store over a plain object, shaped like settings.js's (`load`,
 * `subscribe`), with `update` for the harness's own controls.
 *
 * @param {import('../src/settings.js').Settings} initial
 */
export function createMemorySettings(initial) {
  let current = Object.freeze({ ...initial });
  /** @type {Set<(next: any) => void>} */
  const listeners = new Set();
  return {
    async load() {
      return current;
    },
    /** @param {(next: any) => void} listener */
    subscribe(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    /** @param {Partial<import('../src/settings.js').Settings>} patch */
    update(patch) {
      current = Object.freeze({ ...current, ...patch });
      for (const listener of [...listeners]) listener(current);
      return current;
    },
    get current() {
      return current;
    },
  };
}

/**
 * A `chrome.action` stand-in that keeps the (one tab's) badge and tells `onChange`
 * when it has changed.
 *
 * @param {(badge: { text: string, color: string, title: string }) => void} [onChange]
 */
export function createBadgeRecorder(onChange) {
  const badge = { text: '', color: '', title: '' };
  /** @type {Array<{ text: string, color: string, title: string }>} */
  const history = [];
  let pending = false;
  // The relay sets text, colour and title one after another; report the
  // badge once they are all set, not each half-changed state.
  const changed = () => {
    if (pending) return;
    pending = true;
    queueMicrotask(report);
  };
  const report = () => {
    pending = false;
    const copy = { ...badge };
    const last = history.at(-1);
    if (!last || last.text !== copy.text || last.color !== copy.color || last.title !== copy.title) {
      history.push(copy);
      onChange?.(copy);
    }
  };
  return {
    /** @param {{ text: string }} d */
    setBadgeText(d) {
      badge.text = d.text;
      changed();
    },
    /** @param {{ color: string }} d */
    setBadgeBackgroundColor(d) {
      badge.color = d.color;
      changed();
    },
    /** @param {{ title: string }} d */
    setTitle(d) {
      badge.title = d.title;
      changed();
    },
    get badge() {
      return { ...badge };
    },
    history,
  };
}

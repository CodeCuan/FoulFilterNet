// The extension's settings: what they are, which values are acceptable, and
// how they are kept in chrome.storage.sync.
//
// Pure except for createSettingsStore, which only talks to the storage area it
// is handed, so the tests drive it with a fake.

/**
 * @typedef {'silence' | 'bleep'} CensorMethod
 * @typedef {'closed'} FailPolicy
 *
 * @typedef {object} Settings
 * @property {string} serverUrl  Base URL of the FoulFilterNet service, no trailing slash.
 * @property {boolean} enabled  Filter YouTube videos at all.
 * @property {CensorMethod} censorMethod  How a Hit is rendered live.
 * @property {FailPolicy} failPolicy  What happens when a video cannot be filtered. V1 has one answer.
 * @property {number} offsetMs  Shifts every Hit by this many milliseconds (positive = later), an integer in ±MAX_OFFSET_MS.
 *
 * @typedef {Partial<Record<keyof Settings, string>>} FieldMessages
 *
 * @typedef {object} Validation
 * @property {boolean} ok  No errors: `settings` is exactly what the input asked for, normalised.
 * @property {Settings} settings  The normalised settings; a field in error keeps its fallback value.
 * @property {FieldMessages} errors  Fields whose value was refused, with a sentence for the user.
 * @property {FieldMessages} warnings  Fields that were accepted but deserve a word.
 */

export const DEFAULT_SERVER_URL = 'http://localhost:8000';

/** The largest offset, either way, in milliseconds. The padding is 150/250 ms; this is an escape hatch. */
export const MAX_OFFSET_MS = 500;

/** @type {readonly CensorMethod[]} */
export const CENSOR_METHODS = Object.freeze(['silence', 'bleep']);

/** @type {readonly FailPolicy[]} */
export const FAIL_POLICIES = Object.freeze(['closed']);

/** The one chrome.storage key everything lives under. */
export const STORAGE_KEY = 'settings';

/** Host names the service answers on: its AllowedHosts is `localhost;127.0.0.1;[::1]`. */
const LOCAL_HOSTS = new Set(['localhost', '127.0.0.1', '[::1]']);

/** @type {Readonly<Settings>} */
export const DEFAULT_SETTINGS = Object.freeze({
  serverUrl: DEFAULT_SERVER_URL,
  enabled: true,
  censorMethod: 'silence',
  failPolicy: 'closed',
  offsetMs: 0,
});

/**
 * Normalise a server URL typed by the user.
 *
 * Accepts any http(s) URL (trimmed, trailing slashes dropped, host lower-cased
 * by the URL parser), and warns when the host is not one the service answers
 * on, since it refuses every other Host header (W10). Refuses other schemes,
 * credentials, a query or a fragment: the extension appends paths to it.
 *
 * @param {unknown} value
 * @returns {{ ok: true, url: string, warning: string | null } | { ok: false, error: string }}
 */
export function normaliseServerUrl(value) {
  if (typeof value !== 'string' || value.trim() === '') {
    return { ok: false, error: 'Enter the server URL, for example http://localhost:8000.' };
  }

  /** @type {URL} */
  let url;
  try {
    url = new URL(value.trim());
  } catch {
    return { ok: false, error: 'Not a URL. Enter it in full, for example http://localhost:8000.' };
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return { ok: false, error: 'The server URL must start with http:// or https://.' };
  }
  if (url.username !== '' || url.password !== '') {
    return { ok: false, error: 'The server URL must not contain a user name or password.' };
  }
  if (url.search !== '' || url.hash !== '' || /[?#]/.test(value)) {
    return { ok: false, error: 'The server URL must not contain a query (?) or fragment (#).' };
  }

  const path = url.pathname.replace(/\/+$/, '');
  const normalised = `${url.protocol}//${url.host}${path}`;
  const warning = LOCAL_HOSTS.has(url.hostname)
    ? null
    : `FoulFilterNet only answers on localhost, 127.0.0.1 or [::1]; ${url.hostname} will be refused unless you changed AllowedHosts.`;

  return { ok: true, url: normalised, warning };
}

/**
 * The match pattern that grants the extension access to a server URL, for
 * `chrome.permissions.request({ origins: [...] })`.
 *
 * @param {string} serverUrl  A URL accepted by {@link normaliseServerUrl}.
 * @returns {string | null}  e.g. `http://127.0.0.1:9000/*`, or null for an unusable URL.
 */
export function originPattern(serverUrl) {
  const result = normaliseServerUrl(serverUrl);
  if (!result.ok) {
    return null;
  }
  const url = new URL(result.url);
  return `${url.protocol}//${url.host}/*`;
}

/**
 * Whether reaching this server URL needs a runtime permission: true for any
 * origin other than the default, which the manifest grants at install.
 *
 * @param {string} serverUrl
 * @returns {boolean}
 */
export function needsOptionalPermission(serverUrl) {
  const pattern = originPattern(serverUrl);
  return pattern !== null && pattern !== originPattern(DEFAULT_SERVER_URL);
}

/**
 * Check and normalise a complete or partial settings object. Missing fields
 * take their defaults; unknown fields are dropped.
 *
 * @param {unknown} input
 * @param {Readonly<Settings>} [fallback]  Where a missing or refused field comes from.
 * @returns {Validation}
 */
export function validateSettings(input, fallback = DEFAULT_SETTINGS) {
  const source = isPlainObject(input) ? input : {};
  /** @type {FieldMessages} */
  const errors = {};
  /** @type {FieldMessages} */
  const warnings = {};
  /** @type {Settings} */
  const settings = { ...fallback };

  if (!isPlainObject(input) && input !== undefined && input !== null) {
    errors.serverUrl = 'Settings must be an object.';
  }

  if ('serverUrl' in source) {
    const result = normaliseServerUrl(source.serverUrl);
    if (result.ok) {
      settings.serverUrl = result.url;
      if (result.warning) {
        warnings.serverUrl = result.warning;
      }
    } else {
      errors.serverUrl = result.error;
    }
  }

  if ('enabled' in source) {
    if (typeof source.enabled === 'boolean') {
      settings.enabled = source.enabled;
    } else {
      errors.enabled = 'enabled must be true or false.';
    }
  }

  if ('censorMethod' in source) {
    const method = typeof source.censorMethod === 'string' ? source.censorMethod.trim().toLowerCase() : null;
    if (method === 'silence' || method === 'bleep') {
      settings.censorMethod = method;
    } else if (method === 'remove') {
      // ADR-0004: remove falls back to silence for anything with a picture.
      settings.censorMethod = 'silence';
      warnings.censorMethod = 'Remove cannot be done while a video plays; silence is used instead.';
    } else {
      errors.censorMethod = `Censor method must be one of: ${CENSOR_METHODS.join(', ')}.`;
    }
  }

  if ('failPolicy' in source) {
    if (FAIL_POLICIES.includes(/** @type {FailPolicy} */ (source.failPolicy))) {
      settings.failPolicy = /** @type {FailPolicy} */ (source.failPolicy);
    } else {
      errors.failPolicy = `Fail policy must be one of: ${FAIL_POLICIES.join(', ')}.`;
    }
  }

  if ('offsetMs' in source) {
    const raw = source.offsetMs;
    const offset = typeof raw === 'string' && raw.trim() !== '' ? Number(raw) : raw;
    if (typeof offset === 'number' && Number.isFinite(offset)) {
      const rounded = Math.round(offset);
      const clamped = Math.min(MAX_OFFSET_MS, Math.max(-MAX_OFFSET_MS, rounded));
      settings.offsetMs = clamped === 0 ? 0 : clamped; // never -0
      if (clamped !== rounded) {
        warnings.offsetMs = `The offset is limited to ±${MAX_OFFSET_MS} ms; ${clamped} ms is used.`;
      }
    } else {
      errors.offsetMs = 'The offset must be a number of milliseconds.';
    }
  }

  return { ok: Object.keys(errors).length === 0, settings, errors, warnings };
}

/**
 * Lenient normalisation for reading stored settings: anything unusable falls
 * back to its default rather than failing, so a bad stored value can never
 * stop the extension loading.
 *
 * @param {unknown} stored
 * @returns {Settings}
 */
export function normaliseSettings(stored) {
  return validateSettings(stored).settings;
}

/**
 * Apply a patch to settings already held, validating the result.
 *
 * @param {Readonly<Settings>} current
 * @param {unknown} patch
 * @returns {Validation}
 */
export function mergeSettings(current, patch) {
  return validateSettings(isPlainObject(patch) ? patch : {}, normaliseSettings(current));
}

/**
 * @typedef {object} StorageArea  The part of `chrome.storage.sync` used here.
 * @property {(keys: string | string[]) => Promise<Record<string, unknown>>} get
 * @property {(items: Record<string, unknown>) => Promise<void>} set
 *
 * @typedef {(changes: Record<string, { oldValue?: unknown, newValue?: unknown }>, areaName: string) => void} ChangeListener
 *
 * @typedef {object} ChangeEvent  The part of `chrome.storage.onChanged` used here.
 * @property {(listener: ChangeListener) => void} addListener
 * @property {(listener: ChangeListener) => void} removeListener
 *
 * @typedef {object} SettingsStore
 * @property {() => Promise<Settings>} load  Never rejects: an unreadable store reads as the defaults.
 * @property {(patch: Partial<Settings>) => Promise<Validation>} save  Saves only when the merged result has no errors.
 * @property {(listener: (next: Settings, previous: Settings) => void) => () => void} subscribe  Returns an unsubscribe function.
 */

/**
 * @param {{ area: StorageArea, onChanged?: ChangeEvent, areaName?: string }} dependencies
 *   `areaName` is the name `onChanged` reports for `area` (`sync` by default).
 * @returns {SettingsStore}
 */
export function createSettingsStore({ area, onChanged, areaName = 'sync' }) {
  async function load() {
    try {
      const items = await area.get(STORAGE_KEY);
      return normaliseSettings(items?.[STORAGE_KEY]);
    } catch {
      return normaliseSettings(undefined);
    }
  }

  /** @param {Partial<Settings>} patch */
  async function save(patch) {
    const result = mergeSettings(await load(), patch);
    if (result.ok) {
      await area.set({ [STORAGE_KEY]: { ...result.settings } });
    }
    return result;
  }

  /** @param {(next: Settings, previous: Settings) => void} listener */
  function subscribe(listener) {
    if (!onChanged) {
      return () => {};
    }
    /** @type {ChangeListener} */
    const handler = (changes, name) => {
      if (name !== areaName || !changes || !(STORAGE_KEY in changes)) {
        return;
      }
      const change = changes[STORAGE_KEY];
      listener(normaliseSettings(change?.newValue), normaliseSettings(change?.oldValue));
    };
    onChanged.addListener(handler);
    return () => onChanged.removeListener(handler);
  }

  return { load, save, subscribe };
}

/**
 * @param {unknown} value
 * @returns {value is Record<string, unknown>}
 */
function isPlainObject(value) {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}

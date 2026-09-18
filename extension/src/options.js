// The options page: a form bound to the settings, and "Test connection".
// A thin adapter over settings.js, protocol.js and connection.js.

import { describeConnection } from './connection.js';
import { configMessage, createMessenger } from './protocol.js';
import { createSettingsStore, needsOptionalPermission, originPattern, validateSettings } from './settings.js';

const store = createSettingsStore({ area: chrome.storage.sync, onChanged: chrome.storage.onChanged });
const send = createMessenger((message) => chrome.runtime.sendMessage(message));

const form = /** @type {HTMLFormElement} */ (document.getElementById('settings'));
const report = /** @type {HTMLElement} */ (document.getElementById('report'));
const testButton = /** @type {HTMLButtonElement} */ (document.getElementById('test'));
const fields = /** @type {HTMLFormControlsCollection & Record<string, any>} */ (form.elements);

/** @param {import('./settings.js').Settings} settings */
function fill(settings) {
  fields.enabled.checked = settings.enabled;
  fields.serverUrl.value = settings.serverUrl;
  fields.censorMethod.value = settings.censorMethod;
  fields.failPolicy.value = settings.failPolicy;
  fields.offsetMs.value = String(settings.offsetMs);
}

function read() {
  return {
    enabled: fields.enabled.checked,
    serverUrl: fields.serverUrl.value,
    censorMethod: fields.censorMethod.value,
    failPolicy: fields.failPolicy.value,
    offsetMs: fields.offsetMs.value,
  };
}

/** @param {{ errors: Record<string, string>, warnings: Record<string, string> }} messages */
function showFieldMessages({ errors, warnings }) {
  for (const element of document.querySelectorAll('.field-message')) {
    const field = /** @type {HTMLElement} */ (element).dataset.for ?? '';
    element.textContent = errors[field] ?? warnings[field] ?? '';
    element.className = `field-message ${errors[field] ? 'error' : warnings[field] ? 'warning' : ''}`;
  }
}

/**
 * @param {'ok' | 'warning' | 'error' | ''} level
 * @param {string[]} lines
 */
function showReport(level, lines) {
  report.className = `report ${level}`;
  report.textContent = lines.join('\n');
}

/**
 * Ask for access to a non-default server. Must be the first asynchronous step
 * of a click handler: chrome.permissions.request needs the user gesture.
 *
 * @param {string} serverUrl  Already validated.
 * @returns {Promise<boolean>}
 */
async function ensureAccess(serverUrl) {
  if (!needsOptionalPermission(serverUrl)) {
    return true;
  }
  try {
    return await chrome.permissions.request({ origins: [/** @type {string} */ (originPattern(serverUrl))] });
  } catch {
    return false;
  }
}

form.addEventListener('submit', async (event) => {
  event.preventDefault();
  const checked = validateSettings(read());
  showFieldMessages(checked);
  if (!checked.ok) {
    showReport('error', ['Not saved: fix the fields marked above.']);
    return;
  }
  if (!(await ensureAccess(checked.settings.serverUrl))) {
    showReport('error', [`Not saved: Chrome was not allowed to reach ${checked.settings.serverUrl}.`]);
    return;
  }
  const saved = await store.save(checked.settings);
  showFieldMessages(saved);
  fill(saved.settings);
  showReport(saved.ok ? 'ok' : 'error', [saved.ok ? 'Saved.' : 'Not saved.']);
});

testButton.addEventListener('click', async () => {
  const checked = validateSettings({ serverUrl: fields.serverUrl.value });
  showFieldMessages(checked);
  if (!checked.ok) {
    showReport('error', [checked.errors.serverUrl ?? 'Enter a server URL.']);
    return;
  }
  const serverUrl = checked.settings.serverUrl;
  if (!(await ensureAccess(serverUrl))) {
    showReport('error', [`Chrome was not allowed to reach ${serverUrl}.`]);
    return;
  }
  testButton.disabled = true;
  showReport('', ['Testing…']);
  try {
    const result = await send(configMessage({ serverUrl }));
    const { level, summary, details } = describeConnection(/** @type {any} */ (result), serverUrl);
    showReport(level, [summary, ...details]);
  } finally {
    testButton.disabled = false;
  }
});

// Another window (or sync from another device) changed the settings.
store.subscribe((next) => fill(next));

store.load().then(fill);

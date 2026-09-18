// ff/badge (W15): the toolbar badge a content script asks the service worker
// to show on its tab. The message, its validation, and the relay applying it.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  BADGE_TEXT_MAX,
  BADGE_TITLE_MAX,
  MessageType,
  badgeMessage,
  isProtocolMessage,
  parseMessage,
} from '../src/protocol.js';
import { createRelay } from '../src/relay.js';
import { DEFAULT_SETTINGS } from '../src/settings.js';

const BADGE = Object.freeze({ text: '✓', color: '#188038', title: 'FoulFilter is filtering this video.' });

const parse = (fields) => parseMessage({ type: 'ff/badge', ...BADGE, ...fields });

describe('ff/badge: constructor', () => {
  it('has the type ff/badge', () => {
    assert.equal(badgeMessage(BADGE).type, 'ff/badge');
  });

  it('is named in MessageType', () => {
    assert.equal(MessageType.BADGE, 'ff/badge');
  });

  it('carries text, color and title', () => {
    assert.deepEqual(badgeMessage(BADGE), { type: 'ff/badge', ...BADGE });
  });

  it('drops unknown fields', () => {
    assert.deepEqual(Object.keys(badgeMessage({ ...BADGE, tabId: 3 })).sort(), ['color', 'text', 'title', 'type']);
  });

  it('is a protocol message', () => {
    assert.ok(isProtocolMessage(badgeMessage(BADGE)));
  });

  it('survives structured cloning', () => {
    assert.deepEqual(structuredClone(badgeMessage(BADGE)), badgeMessage(BADGE));
  });
});

describe('ff/badge: limits', () => {
  it('allows four characters of text', () => {
    assert.equal(BADGE_TEXT_MAX, 4);
  });

  it('allows a 300-character title', () => {
    assert.equal(BADGE_TITLE_MAX, 300);
  });
});

describe('ff/badge: parseMessage accepts', () => {
  it('a well-formed badge', () => {
    assert.deepEqual(parse({}), { ok: true, message: { type: 'ff/badge', ...BADGE } });
  });

  it('empty text (no badge)', () => {
    assert.equal(parse({ text: '' }).ok, true);
  });

  it('four-letter text', () => {
    assert.equal(parse({ text: 'MUTE' }).ok, true);
  });

  it('a single ellipsis character', () => {
    assert.equal(parse({ text: '…' }).ok, true);
  });

  it('text counted by characters, not UTF-16 units', () => {
    assert.equal(parse({ text: '\u{1F600}\u{1F600}\u{1F600}\u{1F600}' }).ok, true);
  });

  it('an upper-case colour', () => {
    assert.equal(parse({ color: '#D93025' }).ok, true);
  });

  it('an empty title', () => {
    assert.equal(parse({ title: '' }).ok, true);
  });

  it('a title of exactly the limit', () => {
    assert.equal(parse({ title: 'x'.repeat(BADGE_TITLE_MAX) }).ok, true);
  });

  it('and copies out only the known fields', () => {
    assert.deepEqual(Object.keys(parse({ extra: 1 }).message).sort(), ['color', 'text', 'title', 'type']);
  });
});

describe('ff/badge: parseMessage refuses', () => {
  for (const [name, fields] of [
    ['five characters of text', { text: 'ERROR' }],
    ['text that is not a string', { text: 1 }],
    ['missing text', { text: undefined }],
    ['null text', { text: null }],
    ['a colour name', { color: 'red' }],
    ['a three-digit colour', { color: '#f00' }],
    ['a colour with alpha', { color: '#18803880' }],
    ['a colour without #', { color: '188038' }],
    ['a colour with non-hex digits', { color: '#18803g' }],
    ['a colour that is an array', { color: [24, 128, 56] }],
    ['a missing colour', { color: undefined }],
    ['a title over the limit', { title: 'x'.repeat(BADGE_TITLE_MAX + 1) }],
    ['a title that is not a string', { title: 5 }],
    ['a missing title', { title: undefined }],
  ]) {
    it(name, () => {
      const result = parse(fields);
      assert.deepEqual([result.ok, result.error?.kind], [false, 'invalid_request']);
    });
  }
});

/** A fake chrome.action recording its calls. */
function fakeAction({ fail = false } = {}) {
  const calls = [];
  const record = (name) => async (details) => {
    calls.push([name, details]);
    if (fail) throw new Error('No tab with id: 7.');
  };
  return {
    calls,
    setBadgeText: record('text'),
    setBadgeBackgroundColor: record('color'),
    setTitle: record('title'),
  };
}

function relay(action) {
  const fetched = [];
  const r = createRelay({
    loadSettings: async () => DEFAULT_SETTINGS,
    fetch: async (url) => {
      fetched.push(url);
      throw new Error('no fetch expected');
    },
    setTimeout: () => 0,
    clearTimeout: () => {},
    action,
  });
  return { handle: r.handle, fetched };
}

const TAB = { id: 'ext', tab: { id: 7 } };

describe('relay: ff/badge', () => {
  it('sets the text on the sender tab', async () => {
    const action = fakeAction();
    await relay(action).handle(badgeMessage(BADGE), TAB);
    assert.deepEqual(action.calls.find((c) => c[0] === 'text')[1], { tabId: 7, text: '✓' });
  });

  it('sets the colour on the sender tab', async () => {
    const action = fakeAction();
    await relay(action).handle(badgeMessage(BADGE), TAB);
    assert.deepEqual(action.calls.find((c) => c[0] === 'color')[1], { tabId: 7, color: '#188038' });
  });

  it('sets the tooltip on the sender tab', async () => {
    const action = fakeAction();
    await relay(action).handle(badgeMessage(BADGE), TAB);
    assert.deepEqual(action.calls.find((c) => c[0] === 'title')[1], { tabId: 7, title: BADGE.title });
  });

  it('answers ok', async () => {
    assert.deepEqual(await relay(fakeAction()).handle(badgeMessage(BADGE), TAB), { ok: true });
  });

  it('fetches nothing', async () => {
    const r = relay(fakeAction());
    await r.handle(badgeMessage(BADGE), TAB);
    assert.deepEqual(r.fetched, []);
  });

  it('refuses a sender with no tab (the options page)', async () => {
    const reply = await relay(fakeAction()).handle(badgeMessage(BADGE), { id: 'ext' });
    assert.equal(reply.error.kind, 'invalid_request');
  });

  it('refuses when no sender is given', async () => {
    const reply = await relay(fakeAction()).handle(badgeMessage(BADGE));
    assert.equal(reply.error.kind, 'invalid_request');
  });

  it('refuses a tab id that is not an integer', async () => {
    const reply = await relay(fakeAction()).handle(badgeMessage(BADGE), { tab: { id: '7' } });
    assert.equal(reply.error.kind, 'invalid_request');
  });

  it('refuses a negative tab id (TAB_ID_NONE)', async () => {
    const reply = await relay(fakeAction()).handle(badgeMessage(BADGE), { tab: { id: -1 } });
    assert.equal(reply.error.kind, 'invalid_request');
  });

  it('touches nothing for a refused sender', async () => {
    const action = fakeAction();
    await relay(action).handle(badgeMessage(BADGE), {});
    assert.deepEqual(action.calls, []);
  });

  it('refuses a malformed badge before touching the action', async () => {
    const action = fakeAction();
    const reply = await relay(action).handle({ type: 'ff/badge', ...BADGE, color: 'green' }, TAB);
    assert.deepEqual([reply.error.kind, action.calls.length], ['invalid_request', 0]);
  });

  it('answers an extension failure without an action API', async () => {
    const reply = await relay(undefined).handle(badgeMessage(BADGE), TAB);
    assert.equal(reply.error.kind, 'extension');
  });

  it('answers an extension failure when the action API throws (tab closed)', async () => {
    const reply = await relay(fakeAction({ fail: true })).handle(badgeMessage(BADGE), TAB);
    assert.equal(reply.error.kind, 'extension');
  });

  it('works with a synchronous action API', async () => {
    const calls = [];
    const action = {
      setBadgeText: (d) => calls.push(d),
      setBadgeBackgroundColor: (d) => calls.push(d),
      setTitle: (d) => calls.push(d),
    };
    const reply = await relay(action).handle(badgeMessage(BADGE), TAB);
    assert.deepEqual([reply.ok, calls.length], [true, 3]);
  });
});

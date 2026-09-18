import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import {
  EXTENSION_ERROR,
  MessageType,
  cancelMessage,
  configMessage,
  createMessenger,
  failureReply,
  getMessage,
  heartbeatMessage,
  isFromExtension,
  isProtocolMessage,
  isReply,
  parseMessage,
} from '../src/protocol.js';

const VIDEO = { provider: 'youtube', videoId: 'YwARwww5aFo' };

describe('MessageType', () => {
  it('names the five messages', () => {
    assert.deepEqual({ ...MessageType }, {
      HEARTBEAT: 'ff/heartbeat',
      GET: 'ff/get',
      CANCEL: 'ff/cancel',
      CONFIG: 'ff/config',
      BADGE: 'ff/badge',
    });
  });

  it('is frozen', () => {
    assert.ok(Object.isFrozen(MessageType));
  });

  it('prefixes every type with ff/', () => {
    for (const type of Object.values(MessageType)) assert.ok(type.startsWith('ff/'));
  });
});

describe('constructors', () => {
  it('builds a heartbeat with null since and session by default', () => {
    assert.deepEqual(heartbeatMessage({ ...VIDEO, position: 4.2 }), {
      type: 'ff/heartbeat',
      ...VIDEO,
      position: 4.2,
      since: null,
      session: null,
    });
  });

  it('builds a heartbeat carrying since and session', () => {
    const message = heartbeatMessage({ ...VIDEO, position: 1, since: 3, session: 'abc' });
    assert.equal(message.since, 3);
    assert.equal(message.session, 'abc');
  });

  it('builds a get', () => {
    assert.deepEqual(getMessage(VIDEO), { type: 'ff/get', ...VIDEO, since: null, session: null });
  });

  it('builds a cancel with only the video', () => {
    assert.deepEqual(cancelMessage({ ...VIDEO, position: 9 }), { type: 'ff/cancel', ...VIDEO });
  });

  it('builds a config for the saved server', () => {
    assert.deepEqual(configMessage(), { type: 'ff/config', serverUrl: null });
  });

  it('builds a config for a URL being tried', () => {
    assert.deepEqual(configMessage({ serverUrl: 'http://127.0.0.1:9000' }), {
      type: 'ff/config',
      serverUrl: 'http://127.0.0.1:9000',
    });
  });

  it('builds messages that survive structured cloning unchanged', () => {
    const message = heartbeatMessage({ ...VIDEO, position: 1, since: 2, session: 's' });
    assert.deepEqual(structuredClone(message), message);
  });

  it('builds messages parseMessage accepts as-is', () => {
    const messages = [
      heartbeatMessage({ ...VIDEO, position: 1, since: 2, session: 's' }),
      getMessage({ ...VIDEO, since: 2, session: 's' }),
      cancelMessage(VIDEO),
      configMessage({ serverUrl: 'http://localhost:9000' }),
    ];
    for (const message of messages) {
      assert.deepEqual(parseMessage(message), { ok: true, message });
    }
  });
});

describe('isProtocolMessage', () => {
  it('accepts a known type', () => {
    assert.equal(isProtocolMessage({ type: 'ff/config' }), true);
  });

  it('accepts an unknown ff/ type (so it gets an error reply, not silence)', () => {
    assert.equal(isProtocolMessage({ type: 'ff/nope' }), true);
  });

  it('ignores another namespace', () => {
    assert.equal(isProtocolMessage({ type: 'other/config' }), false);
  });

  it('ignores a message without a type', () => {
    assert.equal(isProtocolMessage({}), false);
  });

  it('ignores a string', () => {
    assert.equal(isProtocolMessage('ff/config'), false);
  });

  it('ignores null', () => {
    assert.equal(isProtocolMessage(null), false);
  });

  it('ignores an array', () => {
    assert.equal(isProtocolMessage(['ff/config']), false);
  });
});

describe('isFromExtension', () => {
  it('accepts a sender with our id', () => {
    assert.equal(isFromExtension({ id: 'abc', tab: { id: 1 } }, 'abc'), true);
  });

  it('refuses another extension', () => {
    assert.equal(isFromExtension({ id: 'xyz' }, 'abc'), false);
  });

  it('refuses a sender with no id', () => {
    assert.equal(isFromExtension({ url: 'https://evil.example' }, 'abc'), false);
  });

  it('refuses a missing sender', () => {
    assert.equal(isFromExtension(undefined, 'abc'), false);
  });

  it('refuses when our own id is unknown', () => {
    assert.equal(isFromExtension({ id: '' }, ''), false);
  });
});

describe('parseMessage', () => {
  it('fills in null since and session on a heartbeat', () => {
    assert.deepEqual(parseMessage({ type: 'ff/heartbeat', ...VIDEO, position: 1 }).message, {
      type: 'ff/heartbeat',
      ...VIDEO,
      position: 1,
      since: null,
      session: null,
    });
  });

  it('drops unknown fields', () => {
    const { message } = parseMessage({ type: 'ff/cancel', ...VIDEO, url: 'https://x' });
    assert.equal('url' in message, false);
  });

  it('does not return the object it was given', () => {
    const raw = { type: 'ff/config', serverUrl: null };
    assert.notEqual(parseMessage(raw).message, raw);
  });

  it('leaves content checks to the api-client (a bad ID is still well-typed)', () => {
    assert.equal(parseMessage({ type: 'ff/cancel', provider: 'youtube', videoId: 'x' }).ok, true);
  });

  it('accepts a config without serverUrl', () => {
    assert.deepEqual(parseMessage({ type: 'ff/config' }).message, { type: 'ff/config', serverUrl: null });
  });

  const bad = [
    ['null', null],
    ['a string', 'ff/config'],
    ['an array', [{ type: 'ff/config' }]],
    ['no type', { ...VIDEO }],
    ['a numeric type', { type: 1 }],
    ['an unknown ff/ type', { type: 'ff/delete-everything' }],
    ['a type in the wrong case', { type: 'FF/CONFIG' }],
    ['a config with a numeric serverUrl', { type: 'ff/config', serverUrl: 8000 }],
    ['a cancel without a video ID', { type: 'ff/cancel', provider: 'youtube' }],
    ['a cancel with a numeric provider', { type: 'ff/cancel', provider: 1, videoId: 'YwARwww5aFo' }],
    ['a get with a string since', { type: 'ff/get', ...VIDEO, since: '3' }],
    ['a get with a numeric session', { type: 'ff/get', ...VIDEO, session: 3 }],
    ['a heartbeat without a position', { type: 'ff/heartbeat', ...VIDEO }],
    ['a heartbeat with a string position', { type: 'ff/heartbeat', ...VIDEO, position: '1' }],
    ['a heartbeat with a null position', { type: 'ff/heartbeat', ...VIDEO, position: null }],
  ];
  for (const [name, raw] of bad) {
    it(`refuses ${name} as invalid_request`, () => {
      const result = parseMessage(raw);
      assert.equal(result.ok, false);
      assert.equal(result.error.kind, 'invalid_request');
      assert.equal(typeof result.error.detail, 'string');
    });
  }
});

describe('failureReply', () => {
  it('has the api-client failure shape', () => {
    assert.deepEqual(failureReply('timeout', 'slow'), { ok: false, error: { kind: 'timeout', detail: 'slow' } });
  });
});

describe('isReply', () => {
  it('accepts a success', () => {
    assert.equal(isReply({ ok: true, view: {} }), true);
  });

  it('accepts a failure with a kind', () => {
    assert.equal(isReply({ ok: false, error: { kind: 'http', status: 500 } }), true);
  });

  it('refuses a failure without an error', () => {
    assert.equal(isReply({ ok: false }), false);
  });

  it('refuses a failure whose kind is not a string', () => {
    assert.equal(isReply({ ok: false, error: { kind: 1 } }), false);
  });

  it('refuses a truthy but non-boolean ok', () => {
    assert.equal(isReply({ ok: 1 }), false);
  });

  it('refuses undefined (no listener answered)', () => {
    assert.equal(isReply(undefined), false);
  });
});

describe('createMessenger', () => {
  it('sends the message it is given', async () => {
    const sent = [];
    await createMessenger(async (m) => (sent.push(m), { ok: true }))(configMessage());
    assert.deepEqual(sent, [configMessage()]);
  });

  it('passes a reply through', async () => {
    const reply = { ok: true, cancelled: false };
    assert.equal(await createMessenger(async () => reply)(cancelMessage(VIDEO)), reply);
  });

  it('passes a failure reply through', async () => {
    const reply = failureReply('unreachable', 'down');
    assert.deepEqual(await createMessenger(async () => reply)(configMessage()), reply);
  });

  it('turns a rejection into an extension failure', async () => {
    const send = async () => {
      throw new Error('Extension context invalidated.');
    };
    const reply = await createMessenger(send)(configMessage());
    assert.equal(reply.error.kind, EXTENSION_ERROR);
    assert.match(reply.error.detail, /context invalidated/);
  });

  it('turns a non-Error rejection into an extension failure', async () => {
    const reply = await createMessenger(() => Promise.reject('gone'))(configMessage());
    assert.match(reply.error.detail, /gone/);
  });

  it('turns no answer into an extension failure', async () => {
    const reply = await createMessenger(async () => undefined)(configMessage());
    assert.equal(reply.error.kind, 'extension');
  });

  it('turns a malformed answer into an extension failure', async () => {
    const reply = await createMessenger(async () => ({ hello: 1 }))(configMessage());
    assert.equal(reply.error.kind, 'extension');
  });

  it('names the extension kind "extension"', () => {
    assert.equal(EXTENSION_ERROR, 'extension');
  });
});

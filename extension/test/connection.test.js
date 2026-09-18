import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { describeConnection, describeError } from '../src/connection.js';

const URL_ = 'http://localhost:8000';

function configResult(webVideo) {
  return {
    ok: true,
    config: {
      censorMethods: ['silence', 'bleep', 'remove'],
      aiEnhance: false,
      whisperModel: 'large-v3-turbo',
      maxUploadMb: 2048,
      webVideo,
    },
  };
}

const READY = { available: true, ytDlpVersion: '2026.08.19', denoVersion: '2.9.6', problem: null };
const NO_DENO = { available: false, ytDlpVersion: '2026.08.19', denoVersion: null, problem: 'Deno was not found.' };

describe('describeError', () => {
  it('asks whether the service is running when unreachable', () => {
    assert.match(describeError({ kind: 'unreachable' }, URL_), /Is the service running\?/);
  });

  it('names the server when unreachable', () => {
    assert.match(describeError({ kind: 'unreachable' }, URL_), /localhost:8000/);
  });

  it('says it did not answer in time on timeout', () => {
    assert.match(describeError({ kind: 'timeout' }, URL_), /did not answer in time/);
  });

  it('gives the status and the service\'s detail on http', () => {
    assert.equal(
      describeError({ kind: 'http', status: 503, detail: 'The service is stopping.' }, URL_),
      'FoulFilterNet at http://localhost:8000 answered 503: The service is stopping.',
    );
  });

  it('explains a bare 400 as the localhost-only rule', () => {
    assert.match(describeError({ kind: 'http', status: 400 }, 'http://nas:8000'), /only answers on localhost/);
  });

  it('does not blame the host for a 400 with a detail', () => {
    assert.doesNotMatch(describeError({ kind: 'http', status: 400, detail: 'bad id' }, URL_), /only answers/);
  });

  it('copes with an http error with no status', () => {
    assert.match(describeError({ kind: 'http' }, URL_), /with an error/);
  });

  it('suggests the wrong address or an old service on bad_response', () => {
    assert.match(describeError({ kind: 'bad_response' }, URL_), /right address/);
  });

  it('gives the detail itself for invalid_request', () => {
    assert.equal(describeError({ kind: 'invalid_request', detail: 'Not a URL.' }, URL_), 'Not a URL.');
  });

  it('suggests reloading on an extension failure', () => {
    assert.match(describeError({ kind: 'extension' }, URL_), /Reload/);
  });

  it('falls back to the detail for an unknown kind', () => {
    assert.equal(describeError({ kind: 'mystery', detail: 'odd' }, URL_), 'odd');
  });

  it('falls back to a generic sentence for nothing at all', () => {
    assert.equal(describeError(undefined, URL_), 'Something went wrong.');
  });
});

describe('describeConnection', () => {
  it('is ok when web video is available', () => {
    assert.equal(describeConnection(configResult(READY), URL_).level, 'ok');
  });

  it('says web video is ready', () => {
    assert.match(describeConnection(configResult(READY), URL_).summary, /Web video is ready/);
  });

  it('lists the yt-dlp and Deno versions', () => {
    assert.ok(describeConnection(configResult(READY), URL_).details.includes('yt-dlp 2026.08.19, Deno 2.9.6'));
  });

  it('names the Whisper model', () => {
    assert.ok(describeConnection(configResult(READY), URL_).details.includes('Whisper model: large-v3-turbo'));
  });

  it('is a warning when the service answers but web video is unavailable', () => {
    assert.equal(describeConnection(configResult(NO_DENO), URL_).level, 'warning');
  });

  it('leads the details with the service\'s problem', () => {
    assert.equal(describeConnection(configResult(NO_DENO), URL_).details[0], 'Deno was not found.');
  });

  it('marks what was not found', () => {
    assert.ok(describeConnection(configResult(NO_DENO), URL_).details.includes('yt-dlp 2026.08.19, Deno not found'));
  });

  it('gives a problem even when the service names none', () => {
    const report = describeConnection(configResult({ ...NO_DENO, problem: null }), URL_);
    assert.equal(report.details[0], 'yt-dlp or Deno is missing.');
  });

  it('is an error when the call failed', () => {
    const report = describeConnection({ ok: false, error: { kind: 'unreachable', detail: 'Failed to fetch' } }, URL_);
    assert.equal(report.level, 'error');
  });

  it('keeps the technical detail of a failure as a detail line', () => {
    const report = describeConnection({ ok: false, error: { kind: 'unreachable', detail: 'Failed to fetch' } }, URL_);
    assert.deepEqual(report.details, ['Failed to fetch']);
  });

  it('does not repeat an invalid_request detail that is already the summary', () => {
    const report = describeConnection({ ok: false, error: { kind: 'invalid_request', detail: 'Not a URL.' } }, URL_);
    assert.deepEqual(report, { level: 'error', summary: 'Not a URL.', details: [] });
  });

  it('treats a missing reply as an extension failure', () => {
    assert.match(describeConnection(undefined, URL_).summary, /extension did not answer/);
  });
});

// Sentences for the user about the connection to the service: the options
// page's "Test connection", and later (W15) the overlay's failure reasons.
// Pure.

/**
 * @typedef {import('./api-client.js').ApiError} ApiError
 * @typedef {import('./api-client.js').ConfigResult} ConfigResult
 *
 * @typedef {object} ConnectionReport
 * @property {'ok' | 'warning' | 'error'} level  ok: web video works; warning: the service answers but
 *   web video cannot work; error: no usable service.
 * @property {string} summary  One sentence.
 * @property {string[]} details  Further lines, possibly none.
 */

/**
 * One sentence about a failed call, fit to show the user.
 *
 * @param {ApiError | { kind: string, status?: number, detail?: string }} error
 * @param {string} serverUrl  Where the call went.
 * @returns {string}
 */
export function describeError(error, serverUrl) {
  const detail = typeof error?.detail === 'string' && error.detail !== '' ? error.detail : null;
  switch (error?.kind) {
    case 'unreachable':
      return `Could not reach FoulFilterNet at ${serverUrl}. Is the service running?`;
    case 'timeout':
      return `FoulFilterNet at ${serverUrl} did not answer in time.`;
    case 'http':
      if (error.status === 400 && detail === null) {
        // HostFiltering answers a foreign Host header with a bare 400.
        return `FoulFilterNet at ${serverUrl} refused the request (400). It only answers on localhost, 127.0.0.1 or [::1].`;
      }
      return `FoulFilterNet at ${serverUrl} answered ${error.status ?? 'with an error'}${detail ? `: ${detail}` : '.'}`;
    case 'bad_response':
      return `Something answered at ${serverUrl}, but not as FoulFilterNet with web video support does. Is it the right address, and up to date?`;
    case 'invalid_request':
      return detail ?? 'The request was not valid.';
    case 'extension':
      return 'The extension did not answer. Reload the page, or the extension on chrome://extensions.';
    default:
      return detail ?? 'Something went wrong.';
  }
}

/**
 * What "Test connection" found.
 *
 * @param {ConfigResult} result  The reply to an `ff/config` message.
 * @param {string} serverUrl
 * @returns {ConnectionReport}
 */
export function describeConnection(result, serverUrl) {
  if (!result?.ok) {
    const error = result?.error ?? { kind: 'extension' };
    const details = error.detail && error.kind !== 'invalid_request' ? [error.detail] : [];
    return { level: 'error', summary: describeError(error, serverUrl), details };
  }

  const web = result.config.webVideo;
  const versions = [
    web.ytDlpVersion ? `yt-dlp ${web.ytDlpVersion}` : 'yt-dlp not found',
    web.denoVersion ? `Deno ${web.denoVersion}` : 'Deno not found',
  ];
  const model = `Whisper model: ${result.config.whisperModel}`;

  if (web.available) {
    return {
      level: 'ok',
      summary: `Connected to FoulFilterNet at ${serverUrl}. Web video is ready.`,
      details: [versions.join(', '), model],
    };
  }
  return {
    level: 'warning',
    summary: `Connected to FoulFilterNet at ${serverUrl}, but web video is not available.`,
    details: [web.problem ?? 'yt-dlp or Deno is missing.', versions.join(', '), model],
  };
}

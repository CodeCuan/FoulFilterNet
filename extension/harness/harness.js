// The end-to-end harness page (W16). Adapter: runs the extension's REAL
// content-script wiring (content-app.js with its session reducer, page
// watcher, gate, overlay, audio graph and censor controller) on a local
// <video>, against the real service, with only Chrome's glue replaced:
//
//   chrome.runtime.sendMessage -> the real relay + api-client, called in the
//                                 page, fetching same-origin through
//                                 file-bridge.js (fake YouTube ID <-> dev file)
//   chrome.action (badge)      -> a recorder shown on the page
//   chrome.storage             -> an in-memory settings store
//   the YouTube page           -> index.html's #movie_player and
//                                 video.html5-main-video, and a window whose
//                                 location is a fake watch URL
//
// While the video plays, tap.js measures what reaches the speakers; when it
// ends, self-check.js compares that with the fixture manifest and the page
// prints PASS/FAIL per span, logs it to the console, and sets
// window.__ffHarnessResult.
//
// Query parameters: file (default sample_video.mp4), method (silence|bleep),
// rate (playback rate, e.g. 2), seekAt + seekTo (once the playhead passes
// seekAt, jump to seekTo), offset (settings.offsetMs), debug (log every
// session event).

import { createAudioGraphs } from '../src/audio-graph.js';
import { startContent } from '../src/content-app.js';
import { createMessenger } from '../src/protocol.js';
import { createRelay } from '../src/relay.js';
import { DEFAULT_SETTINGS } from '../src/settings.js';
import {
  createBadgeRecorder,
  createFileBridge,
  createMemorySettings,
  FAKE_VIDEO_ID,
  harnessWindow,
} from './file-bridge.js';
import { evaluate, findFixture, formatReport } from './self-check.js';
import { createTap } from './tap.js';
import { createTimeline, summarise } from './timeline.js';

/** How often the levels are sampled (ms). */
const SAMPLE_MS = 10;

const params = new URL(location.href).searchParams;
const file = params.get('file') || 'sample_video.mp4';
const method = params.get('method') === 'bleep' ? 'bleep' : 'silence';
const rate = Number(params.get('rate') ?? '1');
const seekAt = params.has('seekAt') ? Number(params.get('seekAt')) : null;
const seekTo = params.has('seekTo') ? Number(params.get('seekTo')) : null;
const offsetMs = params.has('offset') ? Number(params.get('offset')) : 0;
const debug = params.has('debug');

const $ = (/** @type {string} */ id) => /** @type {HTMLElement} */ (document.getElementById(id));
const video = /** @type {HTMLVideoElement} */ (document.querySelector('video.html5-main-video'));
const report = $('report');
const status = $('status');
const badgeView = $('badge');

/** @type {ReturnType<typeof createTap> | null} */
let tap = null;
/** @type {Array<ReturnType<ReturnType<typeof createTap>['read']>>} */
let samples = [];
let timeline = createTimeline(() => performance.now());
let seeked = false;
/** @type {ReturnType<typeof findFixture>} */
let fixture = null;

window.__ffHarnessResult = { status: 'loading', file, method };

// --- the fixture and its ground truth ---------------------------------------

try {
  const response = await fetch('/dev/media/manifest.json', { cache: 'no-store' });
  fixture = response.ok ? findFixture(await response.text(), file) : null;
} catch {
  fixture = null;
}

$('title').textContent = `${file} · ${method}${rate !== 1 ? ` · ${rate}×` : ''}${seekAt !== null ? ` · seek ${seekAt}→${seekTo}` : ''}`;
$('truth').textContent = fixture
  ? fixture.spans.length === 0
    ? 'Ground truth: no spans (clean file).'
    : `Ground truth: ${fixture.spans.map((s) => `"${s.phrase}" ${s.start}–${s.end}s`).join(', ')}`
  : 'Ground truth: this file is not in /dev/media/manifest.json, so nothing can be checked.';

for (const link of document.querySelectorAll('a[data-query]')) {
  const target = new URL(location.href);
  target.search = /** @type {HTMLElement} */ (link).dataset.query ?? '';
  /** @type {HTMLAnchorElement} */ (link).href = target.toString();
}

// --- Chrome's glue, replaced ------------------------------------------------

const settings = createMemorySettings({ ...DEFAULT_SETTINGS, serverUrl: location.origin, censorMethod: method, offsetMs });
const action = createBadgeRecorder((badge) => {
  badgeView.textContent = badge.text || '(none)';
  badgeView.style.background = badge.color || 'transparent';
  badgeView.title = badge.title;
  timeline.note('badge', badge.text, video.currentTime);
});
const relay = createRelay({
  loadSettings: settings.load,
  fetch: createFileBridge({ fetch: (input, init) => fetch(input, init), fileName: file }),
  action,
});
const send = createMessenger((message) => relay.handle(message, { tab: { id: 1 } }));

const audio = createAudioGraphs({
  createContext: () => {
    tap = createTap(() => new AudioContext());
    return /** @type {any} */ (tap.context);
  },
});

video.playbackRate = Number.isFinite(rate) && rate > 0 ? rate : 1;
video.defaultPlaybackRate = video.playbackRate;
video.src = `/dev/media/${encodeURIComponent(file)}`;

const app = startContent({
  document,
  window: harnessWindow(window),
  send,
  settings,
  audio,
  log: debug ? (what, detail) => console.debug('FoulFilter', what, detail) : undefined,
});

// --- watching the run -------------------------------------------------------

for (const type of ['play', 'playing', 'pause', 'seeking', 'seeked', 'ratechange', 'waiting', 'ended']) {
  video.addEventListener(type, () => timeline.note(type, type === 'ratechange' ? String(video.playbackRate) : '', video.currentTime));
}

setInterval(() => {
  const d = app.derived;
  const view = app.state.view;
  if (d) timeline.note('gate', `${d.gate.action}:${d.gate.reason ?? ''}`, video.currentTime);
  if (view) timeline.note('state', `${view.state}${view.fromCache ? ' (cache)' : ''}`, video.currentTime);
  status.textContent = [
    `session: ${view ? `${view.state}, ${view.windowsDone}/${view.windowsTotal} windows, ${view.hits?.length ?? 0} hits${view.fromCache ? ', from cache' : ''}` : '(none yet)'}`,
    `gate: ${d ? `${d.gate.action} (${d.gate.reason ?? ''})` : '-'} · censor: ${d?.mode ?? '-'} · audio: ${app.state.audio.status}`,
    `playhead: ${video.currentTime.toFixed(2)}s of ${Number.isFinite(video.duration) ? video.duration.toFixed(2) : '?'}s at ${video.playbackRate}× · samples: ${samples.length}`,
  ].join('\n');
}, 100);

setInterval(() => {
  if (seekAt !== null && seekTo !== null && !seeked && video.currentTime >= seekAt) {
    seeked = true;
    timeline.note('note', `seek ${seekAt}→${seekTo}`, video.currentTime);
    video.currentTime = seekTo;
  }
  // Only while really playing: a hold, a pause or a seek has no media time to judge.
  if (tap === null || video.paused || video.seeking || video.readyState < 3) return;
  samples.push(tap.read(video));
}, SAMPLE_MS);

video.addEventListener('ended', finish);

function finish() {
  const spans = fixture?.spans ?? [];
  const evaluation = evaluate({ samples, spans, method });
  const lines = formatReport(evaluation, file);
  const summary = summarise(timeline.entries);
  const view = app.state.view;
  lines.push(
    `Waited ${summary.firstPlayMs ?? '?'} ms from play to playing; ${summary.holds} hold(s): ${summary.gateReasons.join(', ') || 'none'}.`,
    `Session: ${summary.states.join(' → ') || '?'}; badge: ${summary.badges.join(' → ') || '?'}.`,
  );
  if (!fixture) lines.push('No ground truth for this file: the verdict only covers extra silence.');
  report.textContent = lines.join('\n');
  report.dataset.verdict = evaluation.verdict;
  for (const line of lines) console.log(line);

  window.__ffHarnessResult = {
    status: 'done',
    file,
    method,
    rate: video.playbackRate,
    seek: seekAt !== null ? { at: seekAt, to: seekTo } : null,
    verdict: evaluation.verdict,
    pass: evaluation.pass,
    spans: evaluation.spans,
    extraSilence: evaluation.extraSilence,
    warnings: evaluation.warnings,
    sampleCount: evaluation.sampleCount,
    summary,
    session: view
      ? {
          state: view.state,
          fromCache: view.fromCache,
          windowsTotal: view.windowsTotal,
          hits: view.hits,
          realtimeFactor: view.realtimeFactor,
        }
      : null,
    timeline: timeline.entries,
    report: lines,
  };
}

/** Start another recording on the same page (e.g. after seeking back to 0). */
function reset() {
  samples = [];
  seeked = false;
  timeline = createTimeline(() => performance.now());
  window.__ffHarnessResult = { status: 'running', file, method };
  report.textContent = '';
  delete report.dataset.verdict;
}

$('play').addEventListener('click', () => {
  window.__ffHarnessResult = { ...window.__ffHarnessResult, status: 'running' };
  video.play().catch(() => {});
});
$('again').addEventListener('click', () => {
  reset();
  video.currentTime = 0;
  video.play().catch(() => {});
});
$('forget').addEventListener('click', async () => {
  // Cancel the server's session (the Transcript cache stays), so the next
  // load starts a new one: from the cache if the file was finished before.
  const response = await fetch(`/watch/file/${encodeURIComponent(file)}`, { method: 'DELETE' });
  $('forget').textContent = `Forget session (${response.status})`;
});
$('method').textContent = `Switch to ${method === 'bleep' ? 'silence' : 'bleep'}`;
$('method').addEventListener('click', () => {
  const next = new URL(location.href);
  next.searchParams.set('method', method === 'bleep' ? 'silence' : 'bleep');
  location.href = next.toString();
});

/** For driving the harness from DevTools or an automation tool. */
window.__ffHarness = {
  app,
  video,
  settings,
  fakeVideoId: FAKE_VIDEO_ID,
  get tap() {
    return tap;
  },
  get samples() {
    return samples;
  },
  get timeline() {
    return timeline.entries;
  },
  finish,
  reset,
  evaluateNow: () => evaluate({ samples, spans: fixture?.spans ?? [], method }),
};

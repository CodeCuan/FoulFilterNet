# FoulFilter extension

The Chrome (Manifest V3) half of web video filtering: it censors a YouTube
video's own audio as it plays, using Hits served by the local FoulFilterNet
service. The design is in
[docs/04-web-video-plan.md](../docs/04-web-video-plan.md) and
[ADR-0007](../docs/adr/0007-web-video-two-streams.md).

## Rules

- **Plain JavaScript ES modules.** No TypeScript, no bundler, no build step:
  the files in `src/` are what Chrome loads.
- **No npm dependencies.** `package.json` exists only to declare
  `"type": "module"` and the test script. Do not add `dependencies` or
  `devDependencies`.
- **Pure modules in `src/`, tested densely.** Anything that is a decision
  (video IDs, coverage, the Playback Gate, gain scheduling) is a pure module
  with no DOM or `chrome.*` access, and gets thorough tests in `test/` with
  `node:test` and `node:assert/strict`.
- **Adapters stay thin.** Code that touches the DOM, Web Audio or `chrome.*`
  only translates between the browser and the pure modules; it holds no logic
  worth testing on its own and is verified by hand.

## Installing (load unpacked)

1. Start FoulFilterNet (by default it listens on `http://localhost:8000`).
2. Open `chrome://extensions` and turn on **Developer mode** (top right).
3. Click **Load unpacked** and select this `extension/` folder.
4. Open the extension's **Details → Extension options** and click **Test
   connection**. It should say web video is ready; if not, it says what is
   missing (yt-dlp or Deno on the server's `PATH`).

After editing any file, click the reload arrow on the extension's card, then
**reload any open YouTube tabs**: Chrome does not re-inject content scripts
into pages that were already open, and the old ones lose their connection to
the extension.

### A server somewhere other than `localhost:8000`

The manifest grants `http://localhost:8000/*` at install. Any other origin is
in `optional_host_permissions` (`http://*/*`, `https://*/*`) and is requested
at run time: saving (or testing) another server URL in the options page asks
Chrome for that one origin, e.g. `http://127.0.0.1:9000/*`. Refuse and the URL
is not saved. The options page accepts any http(s) URL but warns about hosts
other than `localhost`, `127.0.0.1` and `[::1]`, because the service's
`AllowedHosts` turns every other `Host` header away with a 400. Permissions
granted for an old URL are not revoked when you change it; remove them on the
extension's Details page if you care.

## Layout

| File | Kind | Does |
|---|---|---|
| `manifest.json` | | MV3 manifest; `test/manifest.test.js` checks every file it names exists |
| `src/settings.js` | pure + store | Settings schema, defaults, validation, `chrome.storage.sync` store with change subscription |
| `src/api-client.js` | pure (injected `fetch`, timers) | The W10 HTTP contract: `heartbeat`, `get`, `cancel`, `config`; results as values, never throws |
| `src/protocol.js` | pure | Messages between pages and the service worker, their validators, and `createMessenger` |
| `src/relay.js` | pure (injected) | What the service worker does with one message |
| `src/connection.js` | pure | Sentences for the user about the connection |
| `src/background.js` | adapter | Service worker: `runtime.onMessage` → relay (including `ff/badge`, applied to the sender's tab through `chrome.action`) |
| `options.html`, `src/options.js` | adapter | Options page and "Test connection" |
| `src/content-loader.js` | adapter (classic) | Content script: loads `src/content.js` as a module |
| `src/content.js` | adapter (module) | Content script entry point: hands the real browser to `content-app.js`; `localStorage.ffDebug = '1'` on YouTube logs every session event |
| `src/content-app.js` | adapter (injected everything) | The wiring (W15): page watcher → session reducer → gate controller, censor controller, overlay, badge and the heartbeat timer; one gate + censor controller per `<video>`; attaches the audio graph on the first `play` |
| `src/session.js` | pure | The content script's reducer: page, heartbeat replies (generation- and sequence-tagged), settings, "Watch unfiltered", audio attach → state; derives the heartbeat cadence and request, the W13 gate input, censor mode, overlay and badge. Holds the failure policy (error tolerance, miniplayer mute) |
| `src/video-id.js` | pure | `https://www.youtube.com/watch?v=<id>` → the 11-character ID, anything else → null; `isVideoId` |
| `src/page-state.js` | pure | Page events → `{phase: idle/leaving/watching, videoId, adShowing, hasVideoElement, generation}`; `shouldMute`, `isFilterable` |
| `src/page.js` | adapter (injected DOM) | `watchPage({document, window, onChange})`: YouTube's navigation events, the `<video>`, `#movie_player.ad-showing`, pagehide → page-state events |
| `src/coverage.js` | pure | Coverage intervals: `normaliseCoverage` (sort, merge touching), `coveredAhead`, `coveredRunEnd`, `contains` |
| `src/gate.js` | pure | The Playback Gate: `decideGate(input)` → `{action: hold/release/none, reason, heldByUs, resume}` and `gateOverlay(input)` → what the overlay says |
| `src/gate-controller.js` | adapter (injected video, clock) | Applies gate decisions to the `<video>`: pauses for a hold, plays only its own holds, tells the user's pauses and play presses from its own |
| `src/overlay.js` | adapter (injected document) | The overlay in `#movie_player` (closed shadow root) with the "Watch unfiltered" button, and "Enable sound" for a suspended AudioContext |
| `src/schedule.js` | pure | Live Censoring's plan: Hits + playhead + rate + audio clock → `{closedNow, events: [{time, gain}]}` on the AudioContext clock for the next 2 s; `isInsideHit`, `samePlan` |
| `src/audio-graph.js` | adapter (injected AudioContext factory) | One AudioContext per page, never closed; one `MediaElementSource` per `<video>` (WeakMap); programme gain and a 1 kHz bleep gain; `InvalidStateError` → `{kind: 'audio'}` |
| `src/censor-controller.js` | adapter (injected video, graph, timers) | Re-plans every 100 ms and on media events while really playing (ADR-0007); modes `filter`/`mute`/`open`; skips unchanged plans |

### Talking to the service

Only the service worker fetches. A content script's requests carry
`youtube.com` as their origin and the service has no CORS policy (on purpose,
W10), so a page cannot reach it; the worker can, through `host_permissions`.
Content scripts and the options page send a message and get the api-client's
result back:

| Message (`protocol.js` constructor) | Service call | Reply |
|---|---|---|
| `heartbeatMessage({provider, videoId, position, since?, session?})` → `ff/heartbeat` | `POST /watch` | `{ok: true, view}` |
| `getMessage({provider, videoId, since?, session?})` → `ff/get` | `GET /watch/{provider}/{id}` | `{ok: true, view}`; no session is `http` 404 |
| `cancelMessage({provider, videoId})` → `ff/cancel` | `DELETE /watch/{provider}/{id}` | `{ok: true, cancelled}` (404 → `cancelled: false`) |
| `configMessage({serverUrl?})` → `ff/config` | `GET /config` (at `serverUrl` if given, else the saved one) | `{ok: true, config}` |
| `badgeMessage({text, color, title})` → `ff/badge` | none: `chrome.action` badge text (≤ 4 characters), `#rrggbb` colour and tooltip on the sender's tab | `{ok: true}`; from a page with no tab, `invalid_request` |

Every failure is `{ok: false, error: {kind, status?, detail?}}` with `kind`
one of `unreachable`, `timeout` (3 s), `http`, `bad_response`,
`invalid_request`, or `extension` (the page could not reach the worker; from
`createMessenger`). Send with `createMessenger((m) => chrome.runtime.sendMessage(m))`,
which never rejects. `since` and `session` go on the query only when both are
known; keep both from each view and send them back.

## Module loading (rule for every task)

- **Service worker and extension pages** are ES modules
  (`"type": "module"`, `<script type="module">`) and `import` normally.
- **Content scripts** declared in the manifest are classic scripts and cannot
  `import`. The only manifest content script is `src/content-loader.js`, which
  does `import(chrome.runtime.getURL('src/content.js'))`. `src/content.js` and
  everything it imports are ordinary ES modules.
- A module loaded into the page that way must be **web accessible**: the
  manifest lists `src/*.js` for `https://www.youtube.com/*` only. Keep content
  script modules directly in `src/` (the pattern does not promise
  subdirectories), and `test/manifest.test.js` fails if the content script can
  reach a module that is not covered. Web accessible files can be fetched by
  YouTube's pages, so never put anything secret in `src/`.
- No inline scripts or event handler attributes in HTML (MV3's CSP).

## Testing

Node 22 or later:

```bash
cd extension
npm test
```

The script runs `node --test "test/**/*.test.js"`. Name test files
`*.test.js`. A bare directory argument (`node --test test/`) is not used: on
Windows, Node 22 treats it as a module path and fails.

# 04 — Web Video Filtering (YouTube V1)

Status: **accepted**, in progress (see [STATUS.md](STATUS.md)). Tasks are numbered `W00`–`W18` so they
cannot be confused with the port's `T01`–`T34`.

## Goal

Watch a YouTube video in the browser, at full quality, with every word on the
Bad Words List silenced or bleeped as it plays.

It works by running two streams:

1. **Analysis stream (server).** The local FoulFilterNet service fetches the
   video's *audio only*, keyed by video ID, and transcribes it window by window
   into Hits.
2. **Viewing stream (browser).** The user watches YouTube's own HD stream as
   normal. A Chrome extension sends the `<video>` element's audio through a gain
   node that closes over each Hit, timed against `video.currentTime`.

The first time a video is watched, the extension holds playback for a few
seconds until the analysis is safely ahead of the playhead. A video that has
been watched before resumes from the Transcript cache and plays straight away.

### Non-goals for V1

- Sites other than YouTube, Shorts, embedded players, livestreams and premieres.
- Smart Cut and the Rescan Pass on web video. Both are possible later; neither
  is needed for it to be useful.
- Censoring ads. The filter and the gate stand aside while an ad plays.
- Firefox or Safari.
- Any re-encoding or proxying of the video. The picture never touches our
  process.

---

## Architecture

```
 Chrome                                        FoulFilterNet.Web (localhost:8000)
┌─────────────────────────────────────┐       ┌──────────────────────────────────────────┐
│ youtube.com tab                     │       │ Watch endpoints                          │
│  content script                     │       │   POST /watch  (start + heartbeat)       │
│   ├ page watcher (video id, ads,    │       │   GET  /watch/youtube/{id}               │
│   │   SPA navigation)               │       │                                          │
│   ├ playback gate (hold/resume)     │       │ WatchSessionManager                      │
│   └ audio filter (Web Audio gain,   │       │   one session per VideoRef, idle expiry  │
│       bleep oscillator, scheduler)  │       │   ├ cache hit?  → Transcript → complete  │
│            ▲   │ chrome.runtime     │       │   └ miss: resolve → download → convert → │
│            │   ▼ messages           │       │          windows in playhead order       │
│  service worker ── fetch ───────────┼──────►│                                          │
│   (API client, server URL option)   │ JSON  │ per window: Stitch → match → reconcile → │
└─────────────────────────────────────┘       │   merge → new snapshot (revision++)      │
                                              │                                          │
                                              │ yt-dlp (bestaudio) ─► ffmpeg ─► 16k WAV  │
                                              │ Whisper.net engine (shared with Jobs)    │
                                              └──────────────────────────────────────────┘
```

### Why this split

| Decision | Chosen | Rejected, and why |
|---|---|---|
| Where the audio for analysis comes from | Server fetches it with **yt-dlp** from the video ID | **`chrome.tabCapture`**: only gives audio as it plays, so there is nothing to look ahead with and every swear would already be audible. **Uploading media from the extension**: the page's MSE segments are hard to get at and the upload would be the whole file anyway. |
| Where censoring happens | **Client side, Web Audio** on the page's own `<video>` | **Server renders a censored stream**: we would have to proxy or remux HD video, which is heavy, fragile against YouTube's formats, and throws away adaptive bitrate. |
| Unit of progress | **The existing 28 s transcription windows** (`TranscriptionWindows`) | Whole-file transcription gives nothing to play until the end; a streaming decoder in whisper.cpp does not give DTW word times. |
| How the extension hears about progress | **Polling heartbeat, about once a second** | **SSE or WebSocket from the MV3 service worker**: the worker is shut down when idle and a long-lived stream from it is fragile. The heartbeat also carries the playhead, which the server needs for prioritising. Polling a local server once a second costs nothing. |
| Extension language | **Plain JavaScript ES modules, tested with `node --test`** | TypeScript and a bundler would add an npm toolchain to a .NET repo for a few hundred lines. The pure modules still get TDD. *(Decided.)* |

### Why windows make streaming cheap

`TranscriptionWindows.Plan` fixes every window's *share* of the timeline
(`KeepFrom`–`KeepTo`) before anything is transcribed, and `Stitch` keeps each
word from exactly one window. So:

- The words in a window's share are **final** as soon as that window is
  transcribed. Nothing later changes them.
- Windows can be transcribed **in any order**. That is what lets a seek jump the
  queue.
- **Coverage** is just the union of the finished windows' shares. The only
  exception is at an edge where the neighbouring window is not done yet: a phrase
  or its padding could run across that edge, so each run is trimmed by a
  **1 s guard** there. The start and end of the file are not trimmed.

Hits are recomputed from the whole transcript-so-far after every window (find
Candidates, reconcile, merge). That costs a few milliseconds and reuses
`PhraseMatcher`, `HitReconciler` and `HitMerger` unchanged. It also means an
edited Bad Words List takes effect on the next poll, without transcribing again.

---

## Vocabulary (to add to CONTEXT.md)

**Web Video**:
A video identified by a provider and that provider's ID (`youtube`,
`dQw4w9WgXcQ`), rather than by a file. Its canonical key is `youtube-<id>`, which
is also its Transcript cache key.
_Avoid_: URL (a URL is only one way to name it)

**Watch Session**:
The server's work to make one Web Video safe to watch: fetching its audio,
transcribing it window by window, and serving Hits as they are confirmed. It
lasts only while it is being watched, and ends after a period with no heartbeat.
The Transcript it builds outlives it (ADR-0002).
_Avoid_: job (a Job turns one file into another file), stream

**Coverage**:
The parts of a Web Video's timeline whose Hits are final. It is a set of
intervals, not one high-water mark, because a seek can have windows transcribed
out of order.

**Playback Gate**:
The extension's rule that holds playback while the stretch just ahead of the
playhead is not covered, and resumes once enough is.

**Live Censoring**:
Rendering the Censor Method at playback time by automating the page's audio, as
opposed to rendering a file. Only `silence` and `bleep` exist here. `remove`
falls back to `silence`, as it already does for video (ADR-0004).

---

## Server design

### Projects

| Project | Holds | References |
|---|---|---|
| `FoulFilterNet.Sources` *(new)* | `VideoRef` parsing and validation, the `IWebAudioSource` contract, the yt-dlp adapter | Domain, Media (process runner) |
| `FoulFilterNet.Watch` *(new)* | Coverage, the window scheduler, partial Hit snapshots, `WatchSession`, `WatchSessionManager`, the worker | Domain, Pipeline, Transcription, Sources |
| `FoulFilterNet.Web` | Watch endpoints and composition | adds Watch, Sources |

Each gets a matching `*.Tests` project, following the porting plan's rule.

### Engine contract change (W07)

`IWhisperEngine.TranscribeWavAsync` transcribes a whole WAV in a single call. A
Watch Session needs **one window at a time, in an order it chooses, from a WAV
it opened once**:

```csharp
public interface IWhisperEngine
{
    Task<IAnalysisAudio> OpenAsync(string wavPath, CancellationToken ct = default);
    ValueTask ReleaseAsync();
}

public interface IAnalysisAudio : IAsyncDisposable
{
    double DurationSeconds { get; }
    IReadOnlyList<TranscriptionWindow> Windows { get; }
    Task<TranscriptionResult> TranscribeWindowAsync(int index, CancellationToken ct = default);
}
```

The batch path becomes "open, transcribe every window in order, `Stitch`".
That is what `WhisperNetEngine` already does inside one method, so batch output
does not change.

**GPU sharing.** Batch Jobs and Watch Sessions share one engine. Each window
takes a **priority lane**: a gate that admits waiting Watch windows before
waiting Job windows. Priority only applies at window boundaries, which are
about a second apart on the GPU. So a batch job running in the background delays
a viewer by at most one window, not by the rest of an audiobook.

### Audio source (W08)

```csharp
public interface IWebAudioSource
{
    Task<WebVideoInfo> ResolveAsync(VideoRef video, CancellationToken ct);   // title, duration, is_live
    Task DownloadAudioAsync(VideoRef video, string outputPath, CancellationToken ct);
}
```

- Runs `yt-dlp -f bestaudio --no-playlist` with an argument list, never a shell
  string. The URL is **built by us** from a validated 11-character ID
  (`[A-Za-z0-9_-]{11}`), so nothing the browser sends ever reaches yt-dlp as a
  URL or an option.
- Audio-only is about 1 MB per minute, and yt-dlp usually fetches it much faster
  than real time. V1 downloads the whole track, then converts it with the
  existing `IAudioPreparer` into the 16 kHz WAV.
- **One yt-dlp call** prints the metadata and downloads, with
  `--match-filter "!is_live"`. W01 measured about 4 s lost to reading the page
  twice. `--download-sections` is **not** used: YouTube paced it to 63 s for
  2 minutes of a 60-minute video, against 8 s for the whole track (ADR-0007).
- Livestreams (`is_live`) are refused as **Unsupported**. Private, age-gated and
  removed videos are **Failed** with yt-dlp's reason.
- **Prerequisites:** `yt-dlp` on `PATH`, and a JavaScript runtime it can use
  (current yt-dlp needs Deno for YouTube). Without it yt-dlp only warns and
  formats may be missing, so availability checks for Deno explicitly. `GET /config` reports
  whether web video is available, like it does for Smart Cut.

### Watch Session (W09)

```
Queued ─► Resolving ─► Downloading ─► Preparing ─► Transcribing ─► Complete
   │           │             │            │              │
   └───────────┴─────────────┴────────────┴──────────────┴──► Failed | Unsupported
Cache hit: Queued ─► Complete
```

- **One session per VideoRef.** Two tabs on the same video share it.
- **Window order** comes from a pure scheduler. It picks the first unfinished
  window whose share starts at or after the latest reported playhead, then wraps
  round to the earlier ones. A seek therefore takes effect after the window in
  flight.
- **Snapshot**: `{ state, duration, coverage[], hits[], revision, progress }`.
  It is rebuilt after each window and served as-is. `hits[]` holds padded,
  merged `{start, end}` values (the phrase is included for a debug overlay).
- **Saved to the Transcript cache** under `youtube-<id>` when the last window
  finishes. `TranscriptStore` already accepts any digest string. V1 does not
  save partial transcripts. A session abandoned halfway is lost, and the next
  viewing starts again.
- **Idle expiry**: with no heartbeat for 2 minutes, remaining work is cancelled
  and the session dropped. A snapshot of a complete session stays in memory for
  30 minutes, and the cache covers it after that.
- **Throughput check**: if windows are finishing slower than real time (for
  example on a CPU-only host), the snapshot says so. The extension can then
  explain a long hold instead of spinning forever.

### HTTP surface (W10)

| Method | Path | Body / result |
|---|---|---|
| `POST` | `/watch` | `{provider:"youtube", video_id, position}` → snapshot. Starts the session if needed and records the heartbeat and playhead. |
| `GET` | `/watch/youtube/{id}` | Snapshot, with no side effects |
| `DELETE` | `/watch/youtube/{id}` | Cancel |

Snake_case, like the rest of the API.

**Exposure.** The service listens on localhost only. The extension's service
worker calls it using its `host_permissions`, which bypass CORS, so **no CORS
policy is added**. A web page cannot make a JSON POST to it without a preflight
that fails. `AllowedHosts` is narrowed from `*` to `localhost;127.0.0.1` so a
DNS-rebinding page cannot reach the API through a hostname it controls. This
also hardens the existing upload endpoints.

---

## Extension design (`extension/`)

Manifest V3. `host_permissions`: `*://www.youtube.com/*` and the server origin
(`http://localhost:8000/*` by default, which the options page can change).

### Modules

Pure modules, unit tested with `node --test`:

| Module | Does |
|---|---|
| `video-id.js` | Watch-page URL → video ID, or null |
| `coverage.js` | Is `[t, t + span)` inside the coverage intervals? |
| `gate.js` | `(covered, position, rate, state, heldByUs, userPaused)` → `hold` / `resume` / `none`. Holds when less than **8 s** of media ahead is covered, and resumes at **30 s** or at the end of the video. This hysteresis stops it stuttering on window boundaries. |
| `schedule.js` | `(hits, currentTime, rate, audioNow, horizon)` → gain automation events on the AudioContext clock |

Adapters over the DOM and browser APIs, kept thin:

| Module | Does |
|---|---|
| `page.js` | Finds `video.html5-main-video`, follows `yt-navigate-finish` SPA navigation, detects ads with `#movie_player.ad-showing` |
| `audio-graph.js` | Once per `<video>` (a `WeakMap`, since YouTube reuses the element): `MediaElementSource → programme gain → destination`, and `Oscillator(1 kHz) → bleep gain → destination` |
| `overlay.js` | "FoulFilter is preparing… 42%", "Server not reachable", "Watch unfiltered" button |
| `background.js` | Service worker: stateless API client that relays a content-script message into a fetch |
| `options.html` | Server URL, enabled, censor method (`silence` or `bleep`), fail policy |

### Timing

`timeupdate` fires only about four times a second, which is too coarse to catch
a single word. Instead:

- A tick about every 100 ms, plus the `seeking`, `seeked`, `ratechange`, `play`
  and `pause` events, asks `schedule.js` for every Hit starting within the next
  **2 s**. Each one is scheduled with `setValueAtTime` on the AudioContext clock:
  `audioNow + (hit.start - currentTime) / rate`.
- Every re-plan starts with `cancelScheduledValues`. Seek, pause and a rate
  change re-plan at once, and if the playhead is already inside a Hit the gain
  closes immediately.
- The audio clock keeps running even when JavaScript timers are throttled, and
  the 2 s horizon covers timer jitter. The existing padding (0.15 s before,
  0.25 s after) absorbs output latency. A per-user offset in options is the
  escape hatch.

### Playback Gate

- It only pauses what it has to. It records that *it* paused the video, so it
  resumes only its own holds. If the user pauses while a hold is on, the gate
  does not later start the video on them.
- A play press during a hold is paused again straight away, and the overlay
  explains why.
- **Ads**: while `.ad-showing` is set, the gate and the filter both stand aside
  (gain open, no hold).
- **Autoplay policy**: an `AudioContext` created without a user gesture starts
  suspended, and once `MediaElementSource` is attached, a suspended context means
  silence. The graph is built on the first `play` event and `resume()`d there.
  If it is still suspended, the overlay asks for a click. This needs to be
  proven in W01.

### Failure policy *(decided: fail-closed)*

While the extension is enabled on a video, and the server is unreachable, the
video is Unsupported or Failed, or the Watch Session cannot keep up, the default
is **fail-closed**. The overlay holds playback, shows the reason, and offers
**Watch unfiltered** for this video. The alternative is fail-open with a badge
warning.

---

## Risks and how the plan handles them

| Risk | Handling |
|---|---|
| `createMediaElementSource` on YouTube's MSE-backed `<video>` gives silence, or YouTube fights it | **W01 spike, before anything else.** It is the load-bearing assumption. |
| yt-dlp breaks when YouTube changes (signatures, PO tokens, JS runtime) | Kept behind `IWebAudioSource`. Failures are shown to the user, not swallowed. Keeping yt-dlp up to date is the user's job, and the README says so. |
| Time-to-first-play is much more than "a few seconds" | W01 measures it on the 3080 Ti with `large-v3-turbo`. Measured in W01 (ADR-0007): 11–24 s as planned, so W17 is required. Keeping the model warm (`UnloadAfterJob=false`) matters most. |
| A batch Job starves the viewer | Priority lane per window (W07) |
| Timer throttling in background tabs | Automation on the audio clock with a 2 s horizon. Tabs playing audio are not subject to Chrome's intensive throttling. |
| YouTube Terms of Service | Downloading with yt-dlp is against YouTube's terms. This is a personal, local tool and the audio never leaves the machine, but the README should say it plainly. |

---

## Task breakdown

Conventions as in [STATUS.md](STATUS.md): one task, one branch
(`task/W<nn>-<slug>`), one commit, TDD, one subagent at a time, and the ledger
updated in the task's commit.

### Phase 0 — Groundwork

#### W00 · Land windowed transcription
Commit the in-progress `TranscriptionWindows` work (engine, tests, the ADR-0006
note) as its own change. Everything below builds on it.
**Done when:** unit tests are green, and the opt-in
`HearsTheProfanityPastTheFirstThirtySeconds` passes on the GPU.
**Depends on:** —

#### W01 · Spike: prove the two load-bearing assumptions *(throwaway code)*
(a) A throwaway unpacked extension on a real YouTube watch page: route the
`<video>` through `MediaElementSource → GainNode`, mute a hard-coded
`[t, t+1]` span, and check seek, rate change, SPA navigation to a second video,
an ad break, and a fresh tab (autoplay policy).
(b) Measure from the command line on two real videos (10 min and 60 min):
yt-dlp resolve and download time, WAV conversion time, cold and warm model load,
and per-window transcription time with `large-v3-turbo`.
**Done when:** the numbers and findings are recorded in ADR-0007's context
section, and each assumption is confirmed or the design is revised. No
production code.
**Depends on:** W00

#### W02 · ADR-0007, vocabulary and scaffolding
Write ADR-0007 (two streams: server-side audio acquisition with client-side live
censoring). Add the vocabulary above to CONTEXT.md. Scaffold
`FoulFilterNet.Sources`, `FoulFilterNet.Watch` and their test projects in the
`slnx`, and the empty `extension/` with its `node --test` script. Add a Node test
step to CI. This task owns the shared files, as T01 did.
**Done when:** `dotnet test` and `node --test extension` both run green with
zero new tests, and CI passes.
**Depends on:** W01

### Phase 1 — Server pure core

#### W03 · VideoRef
Parse and validate a provider plus ID into a canonical key (`youtube-<id>`) and
a canonical watch URL. Reject anything that is not exactly 11 characters from
`[A-Za-z0-9_-]`.
**Tests:** valid IDs round-trip; option-looking strings (`-o`, `--exec`), URLs,
wrong lengths and other providers are rejected; the canonical URL is exact.
**Depends on:** W02

#### W04 · Coverage
Build coverage intervals from a window plan and a set of finished window
indices, with a 1 s guard at edges next to unfinished windows and none at the
start or end of the file. Includes `Contains(from, to)`.
**Tests:** a single-window file; out-of-order completion; adjacent shares merge
into one interval; guards only at unfinished edges; a fully finished plan covers
`[0, duration]`.
**Depends on:** W02

#### W05 · Window scheduler
Next window to transcribe, given the plan, the finished set and the playhead.
**Tests:** playhead at 0 goes in order; a seek to the middle continues from
there, then wraps; a playhead inside an overlap picks the window whose share
holds it; an empty result when all windows are done.
**Depends on:** W02

#### W06 · Partial Hit snapshot
Finished window results → `Stitch` → Candidates → `HitReconciler` →
`HitMerger`, producing a snapshot with a revision number. Reuses Pipeline types
and duplicates no rules.
**Tests:** the same Hits as the batch pipeline once every window is done; a
phrase spanning two shares is only reported once both are done; the revision
rises with every change.
**Depends on:** W04

### Phase 2 — Server adapters

#### W07 · Windowed engine contract and priority lane
Make the `IWhisperEngine` contract change described above, rebuild the batch path
on it, and add a two-priority window gate.
**Tests:** batch output unchanged (existing tests, plus a stub engine proving
windows run in order); the gate admits a waiting high-priority request before
waiting low-priority ones; cancellation while waiting.
**Depends on:** W00, W02

#### W08 · yt-dlp audio source
Argument construction (pure) and outcome mapping: missing yt-dlp, unavailable,
live, and age-restricted. Uses a recording process runner like the Media tests
do.
One yt-dlp call for metadata and download (`--print`/`--print-to-file` plus
`--match-filter "!is_live"`); an availability probe for yt-dlp and Deno.
**Tests:** the exact argument lists; the canonical URL is always the last
argument; metadata parsing from captured output; each failure mapped; Deno
missing reported as unavailable. One
opt-in live test downloads a known short video.
**Depends on:** W03

#### W09 · Watch Session and manager
The state machine, one session per VideoRef, the cache lookup and save, the loop
over the scheduler and the engine, heartbeat and playhead updates, and idle
expiry. It runs as a `BackgroundService`.
**Tests (stubs throughout):** a cache hit completes without the engine; a miss
walks through every state; a playhead update reorders the next window; two
starts share one session; idle expiry cancels; a failed download is Failed with
its reason; a live video is Unsupported; a complete Transcript is saved under
`youtube-<id>`.
**Depends on:** W05, W06, W07, W08

#### W10 · Watch endpoints and host hardening
`POST /watch`, `GET` and `DELETE /watch/youtube/{id}`; web video availability
in `/config`; `AllowedHosts` narrowed to localhost.
**Tests (`WebApplicationFactory`):** snapshot JSON shape in snake_case; an
invalid ID gives 400; a non-JSON POST gives 415; a foreign `Host` header gives
400; `/config` reflects yt-dlp's presence.
**Depends on:** W09

### Phase 3 — Extension

#### W11 · Extension skeleton
Manifest, service worker API client (server URL from storage, timeouts, errors
as values), options page, and "load unpacked" instructions.
**Tests:** the request and response mapping in the API client, with a fake
`fetch`.
**Depends on:** W02

#### W12 · Page watcher
`video-id.js` (pure) and `page.js`: the current video ID, the `<video>` element,
ad state and navigation events, emitted as one stream of page state changes.
Per ADR-0007: `location` shows the new `v=` before the old video stops, so on
`yt-navigate-start` emit "leaving" (filter mutes, snapshot dropped) and only
start the new Watch Session on `yt-navigate-finish`. Never poll `location`.
**Tests:** URL parsing (watch, extra parameters, `&t=`, non-watch pages give
null); the navigation state machine (start → muted, finish → new id). The DOM
adapter is verified by hand against the W01 checklist.
**Depends on:** W11

#### W13 · Playback Gate
`coverage.js` and `gate.js` (pure), plus the overlay.
**Tests:** holds when coverage ahead is under 8 s; resumes at 30 s or at the end
of the video; never resumes a user pause; re-pauses a play press during a hold;
stands aside during an ad; the thresholds scale with the playback rate.
**Depends on:** W12

#### W14 · Live Censoring
`schedule.js` (pure) and `audio-graph.js`, for silence and bleep. Per ADR-0007:
schedule only while really playing (`!paused && readyState >= 3`); cancel on
`waiting`, `emptied`, seek and pause, re-plan on `playing` (after a seek
`currentTime` freezes about 1.5 s while the audio clock runs); one
`AudioContext` for the page's life; guard against double injection.
**Tests:** only Hits within the horizon are scheduled; times scale by rate; a
playhead inside a Hit closes now; nothing is scheduled while paused; a bleep
opens the tone exactly as the programme closes; overlapping ticks produce no
duplicate events.
**Depends on:** W12

#### W15 · Wiring and failure policy
The heartbeat loop (1 s while the tab is on a watch page, stopped otherwise),
snapshot to gate and scheduler, the Watch unfiltered button, a toolbar badge,
and the fail-closed policy. `createMediaElementSource` can only ever succeed
once per element (ADR-0007), so an `InvalidStateError` (extension reloaded, or
another extension got there first) is a fail-closed reason: "reload the page".
**Tests:** a pure reducer from (snapshot or error, page state, options) to UI
state and gate inputs.
**Depends on:** W10, W13, W14

### Phase 4 — Hardening

#### W16 · End-to-end harness and checklist
A development-only `file` provider (behind configuration, off by default) that
serves a fixture video, plus a local test page. That exercises the whole path
without YouTube. Add a written manual checklist for real YouTube: first view,
second view (cached), seek ahead, 2× speed, ad break, SPA navigation, server
stopped halfway, and a batch Job running alongside. In **stock Chrome**, also
the three things the W01 browser could not show: a fresh tab autoplaying with
no click (suspended `AudioContext`), timing in a background tab, and an ad.
**Done when:** the harness plays `tests/fixtures/media` video with every
manifest span inaudible, and the checklist has been run once and its results
recorded in STATUS.
**Depends on:** W15

#### W17 · Time-to-first-play
W01 measured 11–24 s as planned (growing with length), and about 7–9 s with the
steps reordered. Do: load the model while the download runs, and convert the
first stretch (about 2 minutes, 0.3 s) before the rest of the file, so the first
windows do not wait for a full-file conversion (8.6 s for 60 minutes).
**Done when:** a first view of the 60-minute video reaches 30 s of Coverage
in under 10 s with a cold model.
**Depends on:** W16

#### W18 · Documentation
README (prerequisites: yt-dlp and its JS runtime; installing the extension;
configuration; the Terms of Service note), STATUS rows, and the docs index.
**Depends on:** W16

### Dependency graph

```
W00 ─► W01 ─► W02 ─┬─► W03 ─► W08 ──────────────┐
  │                ├─► W04 ─► W06 ──────────────┤
  │                ├─► W05 ─────────────────────┼─► W09 ─► W10 ─┐
  └────────────────┴─► W07 ─────────────────────┘               │
                   └─► W11 ─► W12 ─┬─► W13 ─┐                   │
                                   └─► W14 ─┴───────────────────┴─► W15 ─► W16 ─┬─► W17
                                                                                └─► W18
```

The server track (W03–W10) and the extension track (W11–W14) do not meet until
W15, which makes that the natural place to check the whole thing end to end.

### Later, not V1

- Other yt-dlp sites (Vimeo, Twitch VODs): a new `VideoRef` provider and a
  matching content script.
- Shorts and embedded players.
- Saving partial transcripts, so an abandoned session resumes.
- Smart Cut *rejections* (not widening) for web video.
- An optional Rescan Pass after the first viewing, in the background.
- Firefox (MV3 with `browser.*` and a background page).

---

## Decisions (2026-09-17)

1. **Fail-closed.** While enabled, a video that cannot be filtered is held with
   a reason and a one-click **Watch unfiltered**.
2. **Plain JavaScript ES modules, tested with `node --test`**, no npm toolchain.
   Test densely: every pure module gets thorough coverage.
3. **yt-dlp is a user-installed prerequisite** on `PATH`, like FFmpeg.

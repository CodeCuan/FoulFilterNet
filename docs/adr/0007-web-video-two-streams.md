# ADR-0007: Web video runs as two streams

Status: **Accepted** (W01 spike, 2026-09-17; both assumptions hold, with the
corrections below)

## Context

FoulFilterNet censors files. Watching a YouTube video filtered means censoring
something the tool never holds: the picture and sound arrive in the browser from
YouTube's own adaptive stream, and a swear has to be silent the first time it
plays. [04-web-video-plan.md](../04-web-video-plan.md) proposes splitting the
work into an analysis stream on the local server and a viewing stream in the
browser, and names two assumptions the whole design rests on:

1. The page's `<video>` can be routed through Web Audio on a real YouTube watch
   page, and automated precisely enough to close over a word (W01a).
2. The server can fetch and transcribe a video's audio fast enough that a first
   viewing is held for "a few seconds", not minutes (W01b).

W00 made the second one plausible: ADR-0006's DTW truncation forced whisper.cpp
to be fed 28 s windows (`TranscriptionWindows`), and windows are exactly the
unit a viewer needs — each window's share of the timeline is final once it is
transcribed, and windows can run in any order (the plan's "Why windows make
streaming cheap"). What remained was to put numbers on it.

### Measurements (W01)

Measured 2026-09-17 from the command line with throwaway scripts and a scratch
console harness outside the repository. The harness referenced
`FoulFilterNet.Transcription` and `FoulFilterNet.Media`, built a
`WhisperNetEngine` exactly as production does (`Device=Auto`, blank `Language`
meaning detect, DTW heads from the model), timed the engine's own model load on
its own, then cut each planned 28 s window out of the analysis WAV and timed
`TranscribeWavAsync` on it — one window per call, so each call is a single
whisper.cpp pass, as a Watch Session will make.

**Hardware.** AMD Ryzen 9 3900X, 64 GB RAM, NVMe SSDs, NVIDIA RTX 3080 Ti
(12 GB, driver 595.79, CUDA 13 runtime installed). The card was also driving the
desktop (31 % utilisation, 2.4 GB in use before the runs), which is the normal
case for this tool.

**Software.** yt-dlp 2026.08.19 (Windows exe) with Deno 2.9.6, FFmpeg 7.1.1,
.NET 10.0.401, Whisper.net 1.9.1, `ggml-large-v3-turbo.bin`. `ggml-base.bin` was
not present, so no `base` comparison was made.

**Videos.**

| | ID | Duration | Content |
|---|---|---|---|
| 10 min | `YwARwww5aFo` | 649 s | GOTO/YOW! 2022, single speaker ("Why Most Data Projects Fail…") |
| 60 min | `2SXr48OYxbA` | 3783 s | NDC Oslo 2024, single speaker with audience ("Turbocharged: Writing High-Performance C# and .NET Code") |

**Acquisition and conversion** (wall clock; each step was run two or three
times, shown as the median of three or the mean of two, with the range):

| Step | 10 min | 60 min |
|---|---|---|
| yt-dlp process start alone (`--version`) | 1.21 s | 1.21 s |
| Resolve, `-J --no-playlist` | 4.29 s (4.22–4.43) | 4.52 s (3.98–5.01) |
| Resolve, `--print duration,is_live,title` | 3.65 s (3.63–4.46) | 3.92 s (3.73–5.17) |
| Download, `-f bestaudio --no-playlist` (a fresh yt-dlp call, so it extracts again) | 4.99 s (4.91–7.01) | 8.08 s (7.41–9.89) |
| Download from a saved info JSON (`--load-info-json`, no re-extraction) | 2.35 s (2.26–2.44) | 5.07 s (4.69–5.45) |
| One call that prints metadata **and** downloads (`--print … --no-simulate`) | 5.63 s (5.27–5.99) | 6.92 s (6.75–7.08) |
| Downloaded format | 251 (`251-8`), Opus/WebM, 127 kbps, 10.3 MB | 251, Opus/WebM, 118 kbps, 55.8 MB |
| `--download-sections "*0-120"` (first 2 min) | 4.63 s (3.98–4.72) | **63.2 s** (63.09–63.37) |
| Convert whole file to 16 kHz mono WAV (production `PadStartAsync(…, 0)` arguments) | 1.39 s (1.39–1.40), 20.8 MB | 8.65 s (8.62–8.86), 121 MB |
| Convert only the first 120 s (same arguments plus `-t 120`) | 0.33 s | 0.31 s |

The conversion arguments were exactly those `FFmpegAudioPreparer.BuildPadArguments`
produces for an offset of zero:
`-y -loglevel error -i <in> -af adelay=0|0 -vn -ac 1 -ar 16000 -acodec pcm_s16le <out>`.

**Transcription** (`large-v3-turbo`, CUDA; "steady" excludes the first window
after load):

| Step | 10 min | 60 min |
|---|---|---|
| Runtime loaded | `Transcription model ready on the Cuda runtime` | same |
| Model load, weights in the OS file cache | 1.43 s | 1.38–1.49 s (3 runs) |
| Model load, weights read cold (unbuffered copy, not in the file cache) | — | 1.72 s |
| First window after load | 0.784 s | 0.808–0.814 s |
| Windows timed | all 30 | 24 of 172 (the first 4, then spread evenly to the last) |
| Per window, min / median / max | 0.624 / 0.695 / 0.784 s | 0.601 / 0.746 / 0.814 s (second run 0.603 / 0.756 / 0.808 s) |
| Per window, steady median | 0.693 s | 0.739 s |
| Same windows with `Language=en` instead of detect | — | 0.471 / 0.624 / 0.673 s |
| Real-time factor (median / 28 s) | 0.025 (≈ 40× real time) | 0.027 (≈ 37× real time) |
| New coverage per window (22 s step) | ≈ 32× real time | ≈ 30× real time |
| Whole file, extrapolated | 30 windows ≈ 21 s | 172 windows ≈ 127 s |

Cropping a window out of the WAV in memory took about 1 ms, and releasing the
model 0.02 s. Every window produced sensible English text (60–110 words).

**What yt-dlp said.** Nothing went wrong, and no PO token was needed. `-v`
reports `JS runtimes: deno-2.9.6`, `PO Token Providers: none`, and extraction
through the `visionos` player API. On the 10-minute video it also logged, at
debug level, that YouTube is running an experiment binding GVS PO tokens to the
video ID for the web client, and that some web-client HTTPS formats were skipped
because YouTube is forcing SABR streaming for that client. Neither affected the
audio format we use, but both are the direction breakage will come from.
With Deno removed from `PATH` (Node was installed but is not enabled by default)
extraction still succeeded, with this warning on stderr:

> WARNING: [youtube] No supported JavaScript runtime could be found. Only deno is
> enabled by default; to use another runtime add --js-runtimes RUNTIME[:PATH] to
> your command/config. YouTube extraction without a JS runtime has been
> deprecated, and some formats may be missing.

**Derived time-to-first-play.** The Playback Gate resumes at 30 s of covered
media ahead. Window 0 alone covers `[0, 25)` less the 1 s guard next to the
unfinished window 1, which is 24 s — not enough — so the first play needs
**two windows**, which together cover 46 s. With V1 as the plan describes it
(resolve, then a separate full download, then a full conversion, then load, then
windows):

| V1 as planned | 10 min | 60 min |
|---|---|---|
| Resolve (`--print`) | 3.65 s | 3.92 s |
| Download (separate call) | 4.99 s | 8.08 s |
| Convert whole file | 1.39 s | 8.65 s |
| Model load, cold / warm (resident) | 1.72 s / 0 | 1.72 s / 0 |
| Windows 0 and 1 | 1.46 s | 1.59 s |
| **Time to 30 s ahead, cold model** | **≈ 13.2 s** | **≈ 24.0 s** |
| **Time to 30 s ahead, warm model** | **≈ 11.5 s** | **≈ 22.2 s** |

The same measurements rearranged — one yt-dlp call that reports metadata and
downloads, the model loading in parallel with the download (it is independent of
it and shorter), and the first 120 s converted on their own before the whole
file:

| Rearranged | 10 min | 60 min |
|---|---|---|
| One yt-dlp call (metadata + download) | 5.63 s | 6.92 s |
| Convert first 120 s | 0.33 s | 0.31 s |
| Model load (in parallel with the download) | 0 extra | 0 extra |
| Windows 0 and 1 | 1.46 s | 1.59 s |
| **Time to 30 s ahead, cold or warm model** | **≈ 7.4 s** | **≈ 8.8 s** |

The whole-file conversion then finishes about 16 s after the video was opened on
the 60-minute video, long before the playhead needs anything past 120 s.

### What the measurements say about the plan

- **Confirmed: transcription is not the bottleneck.** A window takes about
  0.7 s on the 3080 Ti, comfortably inside the plan's "about a second apart on
  the GPU" that the priority lane (W07) relies on, and 30–40× faster than
  playback. A Watch Session outruns the viewer by a wide margin, and the whole
  60-minute video is transcribed in about two minutes. Model load is under 2 s
  even from a cold file cache, so `UnloadAfterJob=false` matters less than the
  plan's risk table suggests; loading it in parallel with the download removes
  it from time-to-first-play entirely.
- **Confirmed: audio-only is about 1 MB per minute** (0.95 and 0.88 MB/min), and
  the plain download is fast (≈ 13 MB/s).
- **Contradicted: `--download-sections` is not a shortcut.** The plan's W17
  fallback is to fetch the first minutes on their own with `--download-sections`
  if the download dominates. On the 60-minute video that took **63 s for 2
  minutes of audio, against 8 s for all 63 minutes**, three runs in a row:
  yt-dlp hands a section to FFmpeg's HTTP reader, which YouTube paced at about
  2× real time (FFmpeg reported `speed=2.03x`). The 10-minute video was not
  paced, so it is not dependable either way. **Recommendation:** W17 must not
  use `--download-sections`. Download the whole file with yt-dlp's native
  downloader, and speed up the *conversion* instead.
- **Contradicted: the download does not dominate; conversion and a second
  extraction do.** On the 60-minute video, converting the whole file (8.65 s)
  costs as much as downloading it, and about 4 s of the "download" is yt-dlp
  extracting the page a second time, because `ResolveAsync` and
  `DownloadAudioAsync` are separate processes (each also pays 1.2 s just to
  start the PyInstaller exe). **Recommendations:** (a) W08 should make one
  yt-dlp call that emits the metadata and downloads — refusing livestreams in
  the same call with `--match-filter "!is_live"` rather than a prior resolve —
  or at least hand the resolve's info JSON to the download with
  `--load-info-json`; (b) the Watch Session should convert and start
  transcribing the first span (the first 120 s, or the windows around the
  playhead) before the whole-file conversion finishes.
- **Contradicted in degree: "a few seconds" is 11–24 s as planned.** V1 as
  written holds a first viewing of the 60-minute video for about 22 s even with
  the model warm, and the delay grows with the video's length because the whole
  file is converted before anything is transcribed. Rearranged as above, the
  same machine reaches 30 s of coverage in about 9 s for both videos, nearly
  independent of length. **Recommendation:** W17 is no longer conditional. Its
  target should be **under 10 s from opening a 60-minute video to playback**,
  cold model included, and its options should be reordered: a single yt-dlp
  call, loading the model in parallel with the download, and converting the
  head first (or decoding progressively). Warming the model at startup becomes
  optional. Folding the single call into W08 and the parallel load into W09 is
  cheaper than retrofitting them in W17.
- **Noted: language detection costs about 0.12 s per window** (0.746 s median
  detecting against 0.624 s forced to English). Detection runs per window, so a
  Watch Session could detect once on window 0 and pin the language for the rest;
  not needed for V1.
- **Noted: yt-dlp's JavaScript runtime is advisory today.** Without Deno the
  download still worked and yt-dlp only warned, so `GET /config` cannot rely on
  a failure to report a missing runtime. W08 should check for it explicitly (or
  treat that warning on stderr as a degraded state) rather than letting it
  surface later as missing formats.

### Browser spike (W01a)

Measured 2026-09-17 on real, signed-out YouTube watch pages (region AU, no
consent banner shown), by driving a browser and running throwaway scripts in the
page. No extension was built.

**Browser.** The Chromium browser pane embedded in the Claude desktop app:
`Chrome/152.0.7977.76` (brands `Chromium 152`), Windows 11. It is Chromium, not
stock Chrome, and two of its differences matter below: it does not apply the
autoplay gesture requirement, and a tab that is not in front still reports
`document.hidden === false`. Scripts ran in the page's main world; an extension
content script runs in an isolated world over the same DOM, so the element, its
media pipeline and Web Audio behave identically, but page globals such as
`window.__ff` would not be shared.

**Method.** We cannot hear the output, so every check reads samples. The graph was
`MediaElementSource → AnalyserNode (pre) → GainNode → AnalyserNode (post) →
destination`, with 512-sample (10.7 ms) analysers polled several hundred times
a second, each poll recording `ctx.currentTime`, `video.currentTime` and RMS. A
source the browser treats as cross-origin feeds exact zeros, so a non-zero pre
RMS proves real samples flow. For absolute alignment, the pre-gain envelope
(10 ms bins, labelled by `video.currentTime`) was cross-correlated with the
envelope of W01b's analysis WAV of the same video.

**Videos.** `YwARwww5aFo` (the 10-minute talk), then in-app navigation to
`N8TFwV4Q_w0`, `mUw27wG7uFA` and `orx8LLG1_hI`; `2SXr48OYxbA` in a fresh tab.

| # | Check | Result | Evidence |
|---|---|---|---|
| 1 | Route YouTube's `<video>` through Web Audio | **Works** | `src` is a `blob:` MediaSource URL. `AudioContext` 48 kHz, `running` on creation (no `resume()` needed). Over 1 s, 19 of 20 samples non-zero before and after the gain, identical values, mean RMS 0.059, peak 0.33. |
| 2 | Mute media `[t+2, t+3]` scheduled with `ctx.currentTime + (target − video.currentTime) / rate` | **Sample-accurate against the schedule; ≈ 5 ms against the real audio** | Post-gain exactly 0 (pre non-zero) in 192 polls. First all-zero poll 15 ms after the scheduled start and last one 0.6 ms before the scheduled end, i.e. inside one analyser window. By `video.currentTime` the silence ran 158.017 → 158.999 for a target of 158 → 159. Envelope against the WAV: best lag **+5 ms** (r = 0.997; r = 0.986 at 0, 0.61–0.67 at ±50 ms). `baseLatency` 10 ms, `outputLatency` 40 ms, which delays the gain and the words equally. |
| 3 | Seek +60 s, then `playbackRate = 2` | **Works, with a catch** | After the seek `seeking` and `waiting` fired at once, `video.currentTime` jumped to the target and **stayed frozen 1.47 s** while `ctx.currentTime` ran on, the graph carried zeros, then `seeked`/`playing` and audio resumed. At 2× a mute scheduled with the rate-1 mapping was cancelled with `cancelScheduledValues` and rescheduled: 0 silent polls in the stale window, 103 contiguous silent polls in the new one, edges +8 / −14 ms (ctx) against the schedule, 318.036 → 318.971 media. Envelope at 2× against the WAV: best lag **−5 ms** of media (r = 0.982). |
| 4 | Clock drift over 1× playback | **None measurable** | 66 pairs over 65.48 s: `video.currentTime` advanced exactly as far as `ctx.currentTime` (end difference 0 ms, worst single reading −2.6 ms, slope 0.002 ms/s). `performance.now()` drifted 2.6 ms against both. |
| 5 | SPA navigation by clicking a related video (three times) | **Same element, same graph, audio flows** | `video.html5-main-video` was the tagged element every time and the only `<video>`; no second `createMediaElementSource`. `yt-navigate-start`, `yt-navigate-finish` and `yt-page-data-updated` fired. Pre RMS non-zero on the new videos (e.g. 26 of 32 and 40 of 40 samples). Order on the first (250 ms polls): navigate-start; within 0.2 s `location` said `v=N8TFwV4Q_w0` while the element still played the **old** video at 146.15 s; one poll later the `blob:` `src` had changed and `currentTime` was 0; navigate-finish at +1.04 s; new audio at +1.36 s. |
| 6 | Ads | **Not observed** | Five watch loads (four in-app, one fresh tab), `#movie_player.ad-showing` sampled every 250 ms for about 3 minutes: never set. Untested. |
| 7 | Fresh tab, no gesture | **Not testable in this browser** | The new tab autoplayed with sound (`paused` false, `readyState` 4, `muted` false, `userActivation.hasBeenActive` false). A new `AudioContext` was `running`, `resume()` resolved, and 19 of 20 pre samples were non-zero. Stock Chrome would suspend the context; this browser does not enforce the policy, so the planned handling is still unproven. |
| 8 | Timer throttling in a background tab | **Not testable in this browser** | With the watch tab behind another for about 20 s, `document.hidden` stayed false, no `visibilitychange` fired, and a 100 ms `setInterval` ran at 83–117 ms throughout. A mute scheduled 20 s ahead still landed on media 50.0–51.0 while the tab was behind, but that says nothing about a throttled tab. |
| 9 | `createMediaElementSource` twice on the element | **Throws, even from another `AudioContext`** | Same context and a new context both: `InvalidStateError: … HTMLMediaElement already connected previously to a different MediaElementSourceNode.` |

**The load-bearing assumption holds.** YouTube's MSE `<video>` routes through
Web Audio with real samples, YouTube did nothing to stop it across seeks, rate
changes and in-app navigation, and gain automation on the audio clock lands on
the intended media span to within about 5 ms at 1× and 2×. The media clock is
driven by the audio clock, so there is nothing to re-sync. What remains open is
browser policy, not the mechanism: autoplay, background throttling and ads need
stock Chrome and the real extension.

### What the browser spike says about the plan

- **Confirmed: the timing design.** The `audioNow + (start − currentTime) / rate`
  mapping is accurate to about ±5 ms, far inside the 0.15 s / 0.25 s padding, and
  does not drift. Output latency needs no correction because the gain sits on the
  same path as the words. The per-user offset stays as an escape hatch but should
  default to 0.
- **Contradicted: the mapping is only valid while media is actually playing.**
  During a seek (and any rebuffer) `currentTime` stands still for over a second
  while the audio clock runs, so anything scheduled then lands late by the stall.
  **Recommendation:** W14 cancels automation on `waiting` and `emptied` as well
  as `seeking`/`pause`, and schedules only when `!paused` and `readyState ≥ 3`,
  re-planning on `playing`. Holding the gain closed while stalled is harmless,
  because the graph carries zeros then.
- **Contradicted: a `WeakMap` is not enough.** A media element can be attached to
  a `MediaElementSourceNode` once in its life, in any context. If our context is
  ever lost — the extension reloads or updates, the content script is injected a
  second time, or another extension (a volume booster) got there first — the
  element cannot be re-attached and nothing can censor it. **Recommendation:**
  W14 keeps one `AudioContext` per page for the page's life (never `close()` it),
  guards against double injection, and treats `InvalidStateError` as a fail-closed
  reason ("reload the page to filter") in W15.
- **Refined: SPA navigation.** YouTube reuses the element and our graph survives,
  as the plan assumed. But `location` changes before the element changes video,
  so a tick that reads the video ID from `location` would, for a poll or two, apply
  the new video's Hits to the old video's audio. **Recommendation:** W12 treats
  `yt-navigate-start` as "stop": cancel automation, drop the snapshot, close
  the gain and leave it closed. It starts the new Watch Session on
  `yt-navigate-finish`, and W14 schedules nothing until the element has fired
  `loadedmetadata` or `playing` for the new source.
- **Unchanged: the Playback Gate thresholds (8 s / 30 s) and the 2 s horizon.**
  Nothing here argues for other values; with no drift the horizon could be
  longer, but it only matters in a throttled tab, which was not measured.
- **Still unproven, moved to W16's checklist in stock Chrome:** a fresh tab with
  no gesture (is the context suspended, does `resume()` from the `play` event
  succeed, and does the overlay's click recover it); a background tab (tick
  cadence and whether scheduled mutes still land); and an ad break (does
  `.ad-showing` appear, and does `currentTime` refer to the ad).

## Decision

Web video is filtered with **two streams**, as laid out in
[04-web-video-plan.md](../04-web-video-plan.md#architecture):

- **Analysis stream, on the server.** The local service acquires the video's
  audio itself, by **video ID via yt-dlp** — never from a URL or option the
  browser sends — converts it to the 16 kHz analysis WAV, and transcribes it.
- **Viewing stream, in the browser.** The user watches YouTube's own stream.
  A **Chrome extension censors live with Web Audio**, routing the page's
  `<video>` through a gain node automated on the audio clock against
  `video.currentTime`, and holds playback until enough is covered.
- **Windows are the unit of progress.** The existing 28 s
  `TranscriptionWindows` are transcribed one at a time in playhead order; each
  finished window makes its share of the timeline final, Coverage is the union
  of finished shares, and Hits are recomputed from the transcript so far.
- **The extension polls.** A heartbeat about once a second carries the playhead
  to the server and brings back the latest snapshot; no SSE or WebSocket from
  the MV3 service worker.

With W01a's corrections, live censoring schedules only while the media is
actually playing, keeps one `AudioContext` for the page's life (an element can
be attached once, ever), and keys a new video on `yt-navigate-finish` rather
than on `location`. With W01b's corrections, acquisition makes one yt-dlp call
rather than a resolve followed by a download, never uses
`--download-sections`, and time-to-first-play is engineered (model load in
parallel with the download, the head of the file converted first) rather than
left to W17's discretion.

## Considered Options

The plan's "Why this split" table records the alternatives and why each was
rejected — `chrome.tabCapture`, uploading media from the extension, a
server-rendered censored stream, whole-file transcription, and SSE or
WebSocket — and is not repeated here. W01b adds one:

- **Fetch the first minutes separately with `--download-sections`**: rejected on
  the measurement — paced to about 2× real time by YouTube on one of the two
  videos, eight times slower than downloading the whole track.

## Consequences

- The picture never touches our process and adaptive bitrate is untouched; the
  cost is a second copy of the audio downloaded alongside the viewer's, about
  1 MB per minute.
- The design depends on yt-dlp keeping pace with YouTube (JS challenges, PO
  tokens, SABR). The spike already sees YouTube experimenting with both for the
  web client. It is kept behind `IWebAudioSource`, and failures are shown to the
  user, as the plan's risk table says.
- On a machine like this one a first viewing is held for 11–24 s as V1 is
  planned, or about 9 s with the reordering above; a second viewing plays at
  once from the Transcript cache (ADR-0002). Transcription outruns playback by
  30× or more once started, so seeks and the throughput check are not a
  concern on the GPU. A CPU-only host was not measured and will be far slower
  (ADR-0006 saw about 28 s per 8 s fixture on the CPU).
- W08's `IWebAudioSource` contract should change before it is built: one
  operation that yields the metadata and the downloaded file, with live videos
  refused inside the same yt-dlp call, instead of `ResolveAsync` followed by
  `DownloadAudioAsync`.
- Live censoring is accurate to a few milliseconds on Chromium, so the existing
  padding is ample. But the extension owns the page's audio for the page's life:
  if its `AudioContext` is lost, or another extension attached the element
  first, that video cannot be filtered until the page reloads.
- Autoplay without a gesture, background-tab throttling and ad breaks were not
  observable in the spike's browser; W16 verifies them in stock Chrome.
- W17 stops being conditional and gets a concrete target (under 10 s for the
  60-minute video on this machine) and a reordered list of options.
- Downloading with yt-dlp is against YouTube's Terms of Service; this stays a
  personal, local tool whose audio never leaves the machine, and the README says
  so (W18).

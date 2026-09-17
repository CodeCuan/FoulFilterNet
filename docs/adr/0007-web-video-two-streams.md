# ADR-0007: Web video runs as two streams

Status: **Proposed** (W01 spike; accepted or revised in W02)

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

> **Placeholder — to be filled in by the W01a browser spike on this branch.**
> Expected content: whether `MediaElementSource → GainNode` works on YouTube's
> MSE-backed `<video>`; how precisely a hard-coded `[t, t+1]` span is muted;
> behaviour on seek, rate change, SPA navigation to a second video, an ad break,
> and a fresh tab (autoplay policy and a suspended `AudioContext`).

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

Subject to W01a, and with W01b's corrections: acquisition makes one yt-dlp call
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
- W17 stops being conditional and gets a concrete target (under 10 s for the
  60-minute video on this machine) and a reordered list of options.
- Downloading with yt-dlp is against YouTube's Terms of Service; this stays a
  personal, local tool whose audio never leaves the machine, and the README says
  so (W18).

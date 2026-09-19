# Web video: end-to-end checklist (W16)

Two halves:

1. **The harness**: a local page that runs the extension's real content-script
   modules against the real service on fixture media, and checks by measurement
   that every ground-truth span came out silent. It covers everything except
   Chrome's own glue (`chrome.runtime`, `chrome.storage`, `chrome.action`) and
   YouTube itself. It has been run; the results are [below](#harness-results).
2. **The manual checklist** for a person in **stock Chrome on real YouTube**. It
   covers what the harness cannot: the unpacked extension, YouTube's page, ads,
   autoplay policy, background tabs. Fill in the Result column as you go.

Design: [04-web-video-plan.md](04-web-video-plan.md),
[ADR-0007](adr/0007-web-video-two-streams.md). Progress: [STATUS.md](STATUS.md).

---

## Part 1: the harness

### What it is

- **Server:** the development-only `file` provider (`Watch:DevFileProvider`,
  **off by default**, configuration only). When it is on, Watch Sessions also
  accept `{"provider": "file", "video_id": "<name>"}` for a file directly
  inside the configured directory. Names are whitelisted from the directory
  listing, so there is no path traversal, no leading `-` and no dot files. The
  service logs a `DEV FILE PROVIDER IS ON` warning at startup and serves:
  - `/dev/media` (the file list) and `/dev/media/<name>` (the files, with range
    requests, same origin so Web Audio does not treat the media as tainted);
  - `/dev/harness/` from `extension/harness/`, and `/dev/src/<name>.js` from
    `extension/src/`, read from the repository with `no-store`.

  With the provider on but no directory, or a directory that does not exist,
  the service refuses to start. With it off, `file` is a 400 and nothing under
  `/dev/` exists.
- **Page** (`extension/harness/`): a `<video class="html5-main-video">` inside a
  `#movie_player`, running `content-app.js` unchanged. Only Chrome's glue is
  replaced:
  - messages go to the real `relay.js` and `api-client.js`, called in the page
    and fetching same-origin through `file-bridge.js`. The content script sees
    a made-up YouTube ID, `ffHarness01`, and the bridge maps it to the file;
  - the badge goes to a recorder shown on the page;
  - the settings come from an in-memory store.

  `tap.js` gives `audio-graph.js` a context whose destination is a tap feeding
  four analysers: the raw element, the output, the output with 1 kHz notched out
  (the programme), and a 1 kHz band-pass (the bleep). When the video ends,
  `self-check.js` judges each manifest span:
  - A span is judged on samples whose analyser block lies wholly inside it.
  - **silence**: output below −50 dBFS.
  - **bleep**: programme below −50 dBFS, and the tone above −35 dBFS in at least
    80% of samples.
  - A span with fewer than 3 samples is `NOT PLAYED`.
  - Silenced speech away from every padded span is reported as extra silence, a
    warning.

  The page prints PASS/FAIL per span, logs it to the console and sets
  `window.__ffHarnessResult`. `window.__ffHarness` exposes the app, the samples
  and `finish()` for scripted runs.
- **Media:** `tests/fixtures/generate-harness-media.ps1` copies the fixtures and
  adds `long_video.mp4`. That file is the fixtures' audio joined twice (113.7 s,
  5 windows, 22 spans) under a test pattern. Its offsets are whole samples, so
  the ground truth stays exact. The repository fixtures are all shorter than one
  window, which is why the long video exists: it is what shows seeking and
  holds across several windows.

### Running it

```powershell
# once: build the harness media outside the repository
powershell -ExecutionPolicy Bypass -File tests/fixtures/generate-harness-media.ps1 -OutputDir $env:TEMP\ffn-harness-media

# a throwaway data directory and a Bad Words List with the fixtures' words
New-Item -ItemType Directory -Force $env:TEMP\ffn-harness-data | Out-Null
Set-Content $env:TEMP\ffn-harness-data\bad_words.txt "damn`ngo to hell`nhoe"

$env:Watch__DevFileProvider__Enabled = "true"
$env:Watch__DevFileProvider__Directory = "$env:TEMP\ffn-harness-media"
$env:Storage__DataDirectory = "$env:TEMP\ffn-harness-data"
$env:Storage__BadWordsPath = "$env:TEMP\ffn-harness-data\bad_words.txt"
$env:Transcription__ModelDirectory = "$PWD\models"
$env:Transcription__Model = "large-v3-turbo"
dotnet run --project src/FoulFilterNet.Web -- --urls http://localhost:8000
```

Then open `http://localhost:8000/dev/harness/?file=long_video.mp4` and press
**Play**. Query parameters:

| Parameter | Effect |
|---|---|
| `file` | The file to play (default `sample_video.mp4`) |
| `method` | `silence` or `bleep` |
| `rate` | The playback rate, e.g. `2` |
| `seekAt`, `seekTo` | Once the playhead passes `seekAt`, jump to `seekTo`. `seekAt=0` seeks before playing. |
| `offset` | `offsetMs` in the settings |
| `debug` | Log every session event |

**Forget session** cancels the server's session; the transcript cache stays, so
the next load is a cached view. For a true first view, delete
`transcripts/file-<name>*.json` in the data directory first.

**Never point `Storage__DataDirectory` at your real data directory for this,
and turn the provider off afterwards** (close the terminal, or
`Remove-Item Env:Watch__DevFileProvider__Enabled`).

### Harness results

Run on 2026-09-19 in the built-in browser pane of Claude Code on an RTX 3080 Ti,
`large-v3-turbo`, with the Bad Words List `damn`, `go to hell`, `hoe`. `PASS n/m`
counts the spans that were played; spans a seek skipped are `NOT PLAYED`, by
design. All runs except H9 were judged by the stricter self-check in use before
the edge fix below, so they passed a harder test.

| # | Run | URL query | Result | Notes |
|---|---|---|---|---|
| H1 | Repository fixture, first run of the session | `file=sample_video.mp4` | **PASS 1/1** | Speech at −12 dBFS outside the span; −120 dBFS (digital silence) inside. The session had finished before Play was pressed, so there was no hold. |
| H2 | First view (not cached), hold then release | `file=long_video.mp4` | **PASS 22/22** | Transcript cache deleted and server restarted. Play at 2.6 s after load was held (`still-preparing`) and released at 4.05 s once `[0, 68]` was covered, 1.47 s after the press. Complete (5/5 windows) at 6.1 s. |
| H3 | Second view (cached) | `file=long_video.mp4` after **Forget session** | **PASS 22/22** | `from_cache: true`. Played 2 ms after the press. On load the first reply said `queued`, so it held for about 1 s until the next heartbeat said `complete`. |
| H4 | Seek ahead before any coverage (not cached) | `file=long_video.mp4&seekAt=0&seekTo=80` | **PASS 9/9** | Held, then released at 3.05 s once `[70, 114]` was covered (`covered-to-end`). Windows ran 0, 1, 3, 4, 2: the first heartbeat went before the seek, so the seek counted from the third window. |
| H5 | Seek during playback (cached) | `file=long_video.mp4&seekAt=3&seekTo=60` | **PASS 11/11** | No hold. Every span after the seek was silent. |
| H6 | 2× speed | `file=long_video.mp4&rate=2` | **PASS 22/22** | About 22 samples per word. |
| H7 | Bleep | `file=long_video.mp4&method=bleep` | **PASS 22/22** | Programme silent, and the tone present in 100% of samples in every span. |
| H8 | Bleep, repository fixture | `file=sample_video.mp4&method=bleep` | **PASS 1/1** | |
| H9 | Bleep at 2× with a seek | `file=long_video.mp4&method=bleep&rate=2&seekAt=5&seekTo=50` | **PASS 12/12** | The first run **FAILED** on a harness bug, fixed with regression tests (see below). |
| H10 | Server stopped halfway (session complete) | `file=long_video.mp4&rate=2`, server killed at ~22 s of media | **PASS 22/22** | Three `unreachable` errors, but the view was `complete`, so filtering went on with the ✓ badge and no overlay. This is the designed behaviour. Chrome had buffered the media (served by the same process), so it played to the end; `finish()` was called by hand. |
| H11 | Audio-only file in the `<video>` | `file=repeated_hits.mp3` | **PASS 5/5** | Includes a word at 0.000 s. |

Found while running it:

- **Bug, fixed (harness):** the self-check judged a sample by the middle of its
  analyser block. At 2× a block covers about 42 ms of media, so a sample stamped
  just inside a word's end also heard the audio after it. In H9 a Hit ending 2 ms
  after the word failed on that audio. Now only blocks wholly inside the span
  count, and tap.js records each block's width. Regression tests are in
  `harness-self-check.test.js` and `harness-tap.test.js`.
- **Bug, fixed (harness):** the status line and result reported `0 hits`. The
  page read a view's own `hits`, which is null on an `unchanged` answer. It now
  reads the session's kept Hits.
- **Finding, not a bug (ASR timing):** every run shows the same six extra-silence
  warnings. The Hits there start 0.3–0.7 s before the true word (for example
  26.65 s for a word at 27.323 s). One is an extra `damn` Hit at 48.37–49.08 s.
  The extension silences exactly what the service sends; the timing is
  Whisper's. One Hit (97.24–98.40 s) ends only 2 ms after its word, so the
  padding only just covered it.
- **Finding (latency, for W17):** a seek does not send a heartbeat at once. The
  next one goes on the 1 s cadence, so the server keeps choosing windows from
  the old playhead for up to a second (H4).
- **Finding (latency, for W17):** a cached video's first reply is `queued`,
  because the cache lookup has not finished when the first heartbeat is
  answered. The gate therefore holds for one heartbeat, about 1 s (H3).
- **Measured:** speech onsets in the raw analyser came 40–50 ms after the
  manifest's word starts. That is the analyser block plus clock alignment, well
  inside the 0.15 s lead padding.

---

## Part 2: the manual checklist (stock Chrome, real YouTube)

### Prerequisites

- [ ] **FFmpeg** on `PATH` (as for batch jobs).
- [ ] **yt-dlp** and **Deno**: `winget install yt-dlp.yt-dlp` and
  `winget install DenoLand.Deno`. **Open a new terminal afterwards**: only new
  terminals have them on `PATH`. Check with `yt-dlp --version` and
  `deno --version`.
- [ ] The model file `models\ggml-large-v3-turbo.bin` in the repository.
- [ ] A Bad Words List. Words in the videos you pick make this easy to judge;
  `damn` and `hell` are common.
- [ ] Stock Google Chrome (not the Claude Code browser pane), current version.

### Start the service

In a new terminal at the repository root. **Do not** turn on the dev file
provider for this.

```powershell
$env:Transcription__ModelDirectory = "$PWD\models"
$env:Transcription__Model = "large-v3-turbo"
dotnet run --project src/FoulFilterNet.Web -- --urls http://localhost:8000
```

- [ ] The log has **no** `DEV FILE PROVIDER IS ON` line.
- [ ] `http://localhost:8000/config` shows `"web_video": {"available": true, ...}`
  with both versions.

### Install the extension

- [ ] `chrome://extensions` → **Developer mode** on → **Load unpacked** → select
  the repository's `extension/` folder. It loads with no errors.
- [ ] **Details → Extension options → Test connection** says web video is ready.
- [ ] Choose **silence** or **bleep** and **Save**. Run the checklist once with
  each if you have time.

To see what the content script is doing, run
`localStorage.ffDebug = '1'` in YouTube's console, then reload. Every session
event is then logged.

### The checks

Pick a video you have **never** filtered (for a first view) that has words on
your list in its first minute. A 10–20 minute video is ideal. Note its ID.

| # | Check | How | Expected | Result |
|---|---|---|---|---|
| 1 | First view | Open the video in a new tab and press play if it does not start. | The overlay says FoulFilter is preparing, with a percent, and playback is held. It starts by itself within about 10–25 s (W01 measured 11–24 s; W17 will shorten it). The badge goes `…` then `✓`. Every listed word is silent (or bleeped); the words around it are not cut. | |
| 2 | Second view (cached) | Close the tab. Wait over 30 minutes, or restart the service (the cache is on disk). Open the same video again. | It plays at once, or after at most about 1 s (see H3). No preparing overlay. Every word is still censored. `GET /watch/youtube/<id>` shows `"from_cache": true`. | |
| 3 | Seek ahead | In a new, uncached video, drag the playhead well ahead, e.g. to 10:00, soon after it starts. | If that point is not covered yet, it holds briefly (`preparing`) and resumes. Words after the seek are censored. It never plays uncovered audio. | |
| 4 | 2× speed | Settings → Playback speed → 2. | Words are still censored. The gate needs 2× as much coverage ahead (16 s to hold, 60 s to release). | |
| 5 | Ad break | Play a video with ads, e.g. from the home page while signed out. | During the ad: no overlay, no hold, sound not filtered, badge `AD`. After the ad, filtering resumes on the video (`✓`), and a word right after the ad is censored. | |
| 6 | SPA navigation | Click a recommended video in the sidebar. | The old video's sound is muted as the navigation starts (no tail of the old video leaks). The new video gets its own session: holds if uncached, then filters. The badge follows. | |
| 7 | Miniplayer | Start a video, then press `i` (or go to the home page) so it continues in the miniplayer. | The miniplayer is **muted**, badge `MUTE`, note "FoulFilter muted the miniplayer". Expanding back to the watch page resumes filtered sound. Home-page hover previews are not treated as the miniplayer: they neither mute nor hold anything. | |
| 8 | Server stopped halfway | Start an uncached long video. Once it is playing, stop the service (Ctrl+C). | Within a few seconds (3 failed heartbeats), or as soon as coverage ahead drops below 8 s: held, with the overlay "FoulFilterNet is not reachable", **Watch unfiltered**, and the badge `!`. Restart the service: the hold clears and filtering resumes. For a video already **complete**, it keeps filtering with no hold (harness H10). | |
| 9 | A batch job alongside | Upload an audiobook at `http://localhost:8000` so a job is transcribing, then start an uncached video. | The video's first play is delayed by at most about one window compared with check 1. The job keeps going and finishes; its output is unchanged. | |
| 10 | Fresh-tab autoplay | Paste a watch URL into a **new** tab and do not click anything. | Either it plays filtered, or it is held with **Click to enable FoulFilter** / **Enable sound**. One click on Enable sound starts it with filtered sound. Never unfiltered sound. Write down which happened. | |
| 11 | Background tab timing | Start a video with a word coming up, then switch to another tab for a minute and listen. | Words are still censored on time while the tab is in the background (the gain is scheduled on the audio clock, 2 s ahead). | |
| 12 | Extension reloaded with a tab open | With a video playing, click reload on the extension's card in `chrome://extensions`. Do not reload the page. | The orphaned tab stops getting answers. After about 3 heartbeats it holds with an error and **Watch unfiltered**. Reloading the page then gives either normal filtering or the audio error "…Reload the page to filter it." (`already-connected`). It never plays unfiltered on its own. | |
| 13 | Overlay look and click-through | Look at the overlay during a hold, in normal, theatre and full-screen mode. | It is readable and inside the player. Clicks outside its buttons reach YouTube's controls. The buttons do not also pause or play the video. | |
| 14 | YouTube autoplay-next versus a hold | Let a video end with autoplay on, so the next one starts while uncached. | The next video is held until it is covered. YouTube's autoplay does not fight the hold into a pause/play loop. | |
| 15 | Badge per tab | Two tabs: one filtering, one on the YouTube home page. Then do a full reload of the filtering tab. | Each tab's toolbar badge is its own (`✓` versus none). A full reload clears the badge until the new page sets it. | |
| 16 | Watch unfiltered | During any hold, click **Watch unfiltered**. | It plays at once with the original sound, badge `OFF`, for this video only. The next video is filtered again. | |
| 17 | Disabled | Turn **Enabled** off in the options. | No overlay, no hold, no badge text, original sound. Turning it back on works without reloading. | |

### Notes

Write down anything surprising here: the Chrome version, whether it was signed
in, what the ad looked like, timings.

-

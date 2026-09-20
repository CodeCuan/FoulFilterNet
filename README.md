# FoulFilterNet

Automated profanity removal for audio and video files, built for long-form media
such as audiobooks, podcasts and films. Upload files in the browser (or run one
from the terminal), and every word or phrase on your Bad Words List is made
inaudible — silenced, bleeped, or cut out — with the picture of a video left
untouched. A Chrome extension does the same to a **YouTube video as you watch
it**, without editing anything.

It transcribes with [Whisper.net](https://github.com/sandrohanea/whisper.net)
(whisper.cpp) on an NVIDIA GPU, matches the transcript against the list, and
edits the audio with FFmpeg. An optional LLM pass (Smart Cut) can widen a cut to
a whole idiom or reject a false positive.

This is a .NET 10 port of the Python FoulFilter, which is kept unmodified under
[`Legacy/`](Legacy) as the behavioural reference. The domain vocabulary is in
[CONTEXT.md](CONTEXT.md).

## Prerequisites

- **.NET SDK 10.0.400** or a later 10.0 feature band (see [`global.json`](global.json)).
- **FFmpeg** with `ffmpeg` and `ffprobe` on `PATH`.
- **An NVIDIA GPU with the CUDA 13 runtime** for GPU speed. Whisper.net's CUDA
  build is compiled against CUDA 13, so it needs a driver that supports 13.x
  (580 or newer) and the CUDA runtime libraries installed on the host. Without
  them transcription falls back to the CPU: it works and produces the same
  timestamps, but it is much slower (about 28 s for an 8 s clip on the CPU, where
  the GPU transcribes all seven test fixtures, model load included, in about
  6 s). The log line `Transcription model ready on the {Runtime} runtime` says
  which one was loaded.
- **Whisper GGML weights** — see below.
- For web video only: **yt-dlp** and **Deno** on `PATH`, and **Chrome** — see
  [Watching web video](#watching-web-video).

## Model weights

Transcription needs a GGML weights file named `ggml-<model>.bin` in the
directory `Transcription:ModelDirectory` names (default `models`). **A relative
path is resolved against the process's working directory**, so set an absolute
one when running from anywhere other than the repository root.

- **Web downloads a missing model on the first job** (from Hugging Face, via
  Whisper.net), into a temporary file that is moved into place when complete.
- **The CLI never downloads.** With no weights it stops with
  `No Whisper weights for '<model>'. Install 'ggml-<model>.bin' in '<directory>'.`
  and exit code 1. Run a job through Web once, or place the file by hand.

`Transcription:Model` picks the file: `base` (the default) means `ggml-base.bin`,
`large-v3-turbo` means `ggml-large-v3-turbo.bin`. Recognised names are `tiny`,
`base`, `small`, `medium` (each also with `.en`), `large-v1`, `large-v2`,
`large-v3` and `large-v3-turbo`; the Hugging Face spelling (`openai/whisper-base`)
works too. Approximate sizes: base ~142 MB, small ~466 MB, medium ~1.5 GB,
large-v3 ~2.9 GB, **large-v3-turbo ~1.6 GB** (the only one measured here, and the
model ADR-0006's measurements used).

Weights are gitignored (`models/`, `*.bin`) and must never be committed.

## Running the web app

```powershell
$env:Transcription__ModelDirectory = "$PWD\models"
dotnet run --project src/FoulFilterNet.Web
```

Open <http://localhost:8000>, drop files, pick a censor method, upload. Jobs run
one at a time and report progress live.

`dotnet run` applies `src/FoulFilterNet.Web/Properties/launchSettings.json`,
which sets the Development environment and binds
**<http://localhost:8000>** — the address the Chrome extension expects, and the
only localhost origin its manifest asks permission for, so debugging from an
IDE serves the extension without any extra argument. Pass
`-- --urls http://localhost:9000` to bind somewhere else, and change the
extension's server URL to match.

The same file runs Web with `src/FoulFilterNet.Web` as its working directory.
That is why the model directory is set explicitly above; without it weights
would be downloaded into `src/FoulFilterNet.Web/models`.

Web reads the Bad Words List from `bad_words.txt` in the data directory —
`%LOCALAPPDATA%\FoulFilterNet` on Windows, `~/.local/share/FoulFilterNet` on
Linux — unless `Storage:BadWordsPath` says otherwise. Nothing is seeded there;
[`Legacy/data/bad_words.example.txt`](Legacy/data/bad_words.example.txt) is a
template. One word or phrase per line, `#` starts a comment, matching is case-
and punctuation-insensitive, and phrases of up to three words are supported.

The data directory also holds `uploads/`, `outputs/`, `scratch/` and the
transcript cache `transcripts/`. On startup Web empties `uploads/` and
`scratch/`; outputs and transcripts are kept.

`GET /health` answers `{"status":"ok"}`; `GET /config` shows the model, whether
Smart Cut is really in use, and whether web video is available.

## Watching web video

The same service can filter a **YouTube video as it plays**, instead of editing
a file. Nothing is downloaded for you to keep and the picture never touches this
process: it runs as two streams ([ADR-0007](docs/adr/0007-web-video-two-streams.md),
[docs/04-web-video-plan.md](docs/04-web-video-plan.md)).

1. **Analysis, on the server.** Keyed by the video's ID, the service fetches its
   *audio only* with yt-dlp, converts it to a 16 kHz WAV and transcribes it
   window by window, publishing the Hits of each finished window as it goes.
2. **Live censoring, in the browser.** A Chrome extension routes the page's own
   `<video>` through Web Audio and closes the gain over each Hit, timed against
   `video.currentTime`. You watch YouTube's own HD stream, as normal.

### Prerequisites

Everything under [Prerequisites](#prerequisites) above, plus:

- **yt-dlp** on `PATH` (or named by `Sources:YtDlpPath`). It is a user-installed
  prerequisite like FFmpeg, and **keeping it up to date is your job**: YouTube
  changes, and an old yt-dlp fails to fetch. `yt-dlp -U` for the standalone
  build, otherwise your package manager.
- **Deno** on `PATH` (or `Sources:DenoPath`). Current yt-dlp needs a JavaScript
  runtime for YouTube; without one it only *warns* and the formats it wants are
  missing, so this is checked for explicitly rather than left to fail later.
- **Google Chrome**, for the extension.
- **Whisper weights**, as for a file job. `large-v3-turbo` is recommended:
  it is the model ADR-0006's and ADR-0007's measurements used, and the word
  boundaries the live gain is scheduled against are its DTW timestamps.

`GET /config` reports whether the two tools really run here, the way it reports
Smart Cut:

```powershell
(Invoke-RestMethod http://localhost:8000/config).web_video
```

```
available      : True
yt_dlp_version : 2026.08.19
deno_version   : 2.9.6
problem        :
```

When `available` is false, `problem` says which one could not be run and how to
point at it. The probe starts with the host and its answer is kept for 5 minutes
when it works and 30 seconds when it does not, so an install is picked up
without a restart, but not instantly.

### Starting the server for web video

```powershell
$env:Transcription__ModelDirectory = "$PWD\models"
$env:Transcription__Model = "large-v3-turbo"
dotnet run --project src/FoulFilterNet.Web
```

It binds <http://localhost:8000>, which is where the extension looks. The model
directory is set explicitly for the reason given above: `dotnet run` runs Web
with `src/FoulFilterNet.Web` as its working directory. Leave
`Transcription:UnloadAfterJob` at `false` so the model stays resident between
videos — the first view of the day pays for the load, the rest do not.

The service listens on localhost only, and `AllowedHosts` is
`localhost;127.0.0.1;[::1]`: a `Host` header naming anything else is refused
with a 400, so it cannot be reached through a LAN address or a hostname a web
page controls. The extension's own API is `POST /watch` (the heartbeat, which
starts the Watch Session and carries the playhead) and `GET`/`DELETE
/watch/youtube/{id}`.

### Installing the extension

1. Start the service.
2. Open `chrome://extensions` and turn on **Developer mode** (top right).
3. **Load unpacked**, and select the [`extension/`](extension) folder.
4. **Details → Extension options → Test connection**. It should say web video is
   ready; if not, it says what is missing.

Its options are: the **server URL** (`http://localhost:8000` by default — any
other origin is requested as a Chrome permission when you save it), **Filter
YouTube videos** on or off, the **censor method** (`silence` or `bleep`; there
is no `remove` live, as there is none for video files), what happens **when a
video cannot be filtered** (hold it and offer to watch unfiltered, which is V1's
only answer), and a **timing offset** of ±500 ms for a viewer whose output
latency needs it.

The toolbar badge says what the extension is doing on that tab: `✓` filtering,
`…` preparing, `AD` standing aside for an ad, `MUTE` the miniplayer muted, `OFF`
disabled or watching unfiltered, `!` something is wrong; its tooltip says more.
An overlay appears over the player only while playback is being held, gives the
reason, and offers **Watch unfiltered** for that video (and **Enable sound**
when Chrome has suspended the page's audio until a click).

[`extension/README.md`](extension/README.md) has the module layout, the rules
its code follows, and how to run its tests.

### How it behaves

- **The first view is held.** Playback holds while less than 8 seconds of
  filtered audio is ready ahead of the playhead, and is released once 30 seconds
  is (scaled by the playback rate, so 2× needs twice the media). On the
  reference machine that is about 9–10 seconds for a 60-minute video.
- **A video watched before plays at once**, from the Transcript cache, with
  every Hit known before the first frame.
- **Seeking** puts the window under the new playhead next in the queue, so a
  jump forward is filtered from where you landed rather than from the start.
- **Ads**: while YouTube is playing one, the gate and the filter both stand
  aside — nothing is held and nothing is censored.
- **The miniplayer is muted, not filtered.** Once you navigate away from a watch
  page nothing proves what that element is still playing, and a wrong guess
  would be audible.
- **Fail-closed.** If the server is unreachable, the video cannot be fetched, it
  is a livestream, or the transcription cannot keep up, playback is held and the
  overlay says why. **Watch unfiltered** releases it for that video only.
- **Sessions are per video and shared**: two tabs on the same video share one
  session and one fetch. A session with no heartbeat for 2 minutes is cancelled,
  a complete one is kept for 30 minutes, and a failed one is kept for a minute
  before the next heartbeat retries it.

The **Bad Words List is the same file the file jobs use** (`bad_words.txt` in
the data directory, unless `Storage:BadWordsPath` says otherwise). It is reread
while a video is being watched, so adding or removing a word takes effect on the
next poll — a second or so — **without transcribing anything again**.

### Web transcripts in the cache

A web video's Transcript goes into the same cache as a file's
(`Storage:TranscriptDirectory`, default `<data>/transcripts`), keyed
`youtube-<id>` instead of a file hash: the file is
`youtube-<id>_<video title>.json`. That is what makes the second viewing
instant. Delete that one file to force the video to be listened to again.
Only a session that heard the whole video is saved, so a video you abandoned
halfway starts again.

### Configuration keys

| Key | Default | What it does |
|---|---|---|
| `Sources:YtDlpPath` | `yt-dlp` | Found on `PATH` unless this names it |
| `Sources:DenoPath` | blank (yt-dlp's own search) | Passed to yt-dlp as `--js-runtimes deno:<path>` |
| `Watch:IdleTimeout` | `00:02:00` | No heartbeat for this long cancels the session |
| `Watch:CompletedRetention` | `00:30:00` | How long a complete session's snapshot is kept |
| `Watch:FailedRetention` | `00:01:00` | How long a failure is kept before a heartbeat retries |
| `Watch:SweepInterval` | `00:00:15` | How often expired sessions are dropped |
| `Watch:HeadSeconds` | `120` | Seconds converted on their own, ahead of the whole file, so the first windows start sooner (`0` turns it off) |
| `Watch:FirstAnswerWait` | `00:00:00.500` | How long the heartbeat that starts a session waits for the cache lookup, so a cached video is answered complete straight away |
| `Watch:DevFileProvider:Enabled` | `false` | **Development only — leave it off** |
| `Watch:DevFileProvider:Directory` | blank | The folder of fixture files the dev provider serves |
| `Watch:DevFileProvider:ExtensionDirectory` | blank | Where `extension/` is, for the harness page |

The Watch section sets only these; a session's scratch directory, the transcript
cache and the Bad Words List always follow `Storage`, so there is one place to
move the data rather than two that can disagree.

> **`Watch:DevFileProvider` is for developing this tool, and must stay off.**
> Turned on, the service also accepts a `file` provider for the files in the
> configured directory and serves them, plus the end-to-end harness page and the
> extension's sources, under `/dev/`. It logs a `DEV FILE PROVIDER IS ON`
> warning at startup, and refuses to start if it is on with no directory. It is
> described in [docs/web-video-checklist.md](docs/web-video-checklist.md).

### How long the first view takes

Measured through the real HTTP API on an RTX 3080 Ti with `large-v3-turbo`
(the full tables, and what they were before, are in
[ADR-0007](docs/adr/0007-web-video-two-streams.md#measurements-after-w17)). The
figure is the time from the extension's first heartbeat to 30 seconds of
filtered audio ahead of the playhead — what the gate waits for:

| | Cold model | Warm model |
|---|---|---|
| 60-minute video | 9.6 s (8.0–13.3) | 9.2 s |
| 10-minute video | 6.8 s | 6.5 s |
| Watched before (cached) | 0.08 s | 0.08 s |

Most of what is left is yt-dlp's single fetch call (5.8–8.3 s, network-bound);
everything the tool itself does is under a second.

### Limitations

- **YouTube watch pages only** (`https://www.youtube.com/watch?v=…`). No Shorts,
  no embedded players, no other sites, and livestreams and premieres are refused
  as unsupported.
- **Chrome only.** Manifest V3; Firefox and Safari are not supported.
- **No Smart Cut and no Rescan Pass for web video.** Both apply to file jobs
  only. `remove` has no meaning live and falls back to silence.
- Nothing is saved for a video you stopped watching part-way through.

### Terms of Service

**Downloading YouTube's audio with yt-dlp is against YouTube's Terms of
Service.** This is a local, personal tool: your machine fetches the audio,
transcribes it, and the audio never leaves it — nothing is uploaded,
republished or redistributed, and the picture is streamed by YouTube to your
browser as it always was. Whether to use it, and what follows from that, is
your decision and your responsibility.

## Running the CLI

`foulfilter` (`src/FoulFilterNet.Cli`) is `find_and_remove.py`, argument for
argument:

```powershell
dotnet run --project src/FoulFilterNet.Cli -- book.m4b bad_words.txt --output book_clean.m4b
```

```
foulfilter <file_path> <bad_words_list_path> [options]

  --output <output>         Explicit path to save the final file
  --bleep                   Bleep out words instead of replacing with silence
  --delete                  Cut words out entirely instead of silence (audio files only)
  --censor_method <method>  silence | bleep | remove | delete
  --debug                   Export transcript to text file (and log engine detail)
  --rescan                  Second detection pass with shifted chunk boundaries
  --no_edit                 Analyze only: report what would be cut, do not edit
```

- **Censor method precedence**: `--censor_method`, then `--bleep`, then
  `--delete`, then configuration (`Cli:CensorMethod`, or `CENSOR_METHOD`), then
  `silence`. An unrecognised configured value falls back to `silence`.
  `delete` is accepted as a spelling of `remove`. `remove` on a video falls back
  to silence, because cutting audio would desynchronise the picture.
- The output defaults to `censored_<name>` beside the input. A scratch directory
  `.<name>_scratch` is left beside the output; `--debug` writes
  `transcript.txt` there.
- Exit codes: `0` success, `1` an unreadable or unrecognised file or a failed
  job, `130` cancelled.
- The CLI reads the same `appsettings.json` as Web (copied beside its
  executable) and the same transcript cache, so a file transcribed by either
  host is not transcribed again by the other. `--rescan` always transcribes.
- Unlike Web, the CLI runs in your terminal's working directory, so relative
  file paths and the default `models` directory resolve against it. From
  anywhere but the repository root, set `Transcription__ModelDirectory` to an
  absolute path.

## Evaluating against the fixtures

`foulfilter-eval` (`src/FoulFilterNet.Evaluation`) runs every fixture in
`tests/fixtures/media/manifest.json` through the real pipeline and scores it
twice: the transcriber's **raw words**, and the **final hits** after padding and
merging. It reports detection, recall, precision (the fixtures' garden "hoe" is
an expected false positive), and signed boundary error: a positive `dStart` is a
late start and a negative `dEnd` an early end, the directions that leave speech
audible, and `margin` says by how much each planted span is covered.

```powershell
$env:Transcription__ModelDirectory = "$PWD\models"
$env:Transcription__Model = "large-v3-turbo"
dotnet run --project src/FoulFilterNet.Evaluation -- --work $env:TEMP\ff-eval
```

It prints a table and writes `evaluation.json` into `--work` (or `--json`) for a
later run to be diffed against. By default the Bad Words List is exactly the
manifest's phrases and the transcript cache is `<work>/transcripts`, so a fresh
`--work` always transcribes. Like the CLI it never downloads weights.

## Configuration

[`src/FoulFilterNet.Web/appsettings.json`](src/FoulFilterNet.Web/appsettings.json)
is the primary source for both hosts and lists every setting with its default.

| Key | Default | Legacy variable |
|---|---|---|
| `Transcription:Model` | `base` | `WHISPER_MODEL` |
| `Transcription:Language` | blank (detect) | `WHISPER_LANGUAGE` |
| `Transcription:Device` | `Auto` (`Auto`, `Cuda`, `Cpu`) | — |
| `Transcription:ModelDirectory` | `models` | — |
| `Transcription:UnloadAfterJob` | `false` | `UNLOAD_MODELS_AFTER_JOB` |
| `Storage:DataDirectory` | blank (per-user folder) | `DATA_DIR` |
| `Storage:TranscriptDirectory` | blank (`<data>/transcripts`) | `TRANSCRIPT_DIR` |
| `Storage:BadWordsPath` | blank (`<data>/bad_words.txt`) | `BAD_WORDS_PATH` |
| `Storage:MaxUploadMegabytes` | `4096` | `MAX_UPLOAD_MB` |
| `Cli:CensorMethod` | `silence` | `CENSOR_METHOD` |
| `SmartCut:Enabled` | `false` | `AI_ENHANCE` |
| `SmartCut:Mode` | `Local` (`Local`, `Google`) | `AI_MODE` |
| `SmartCut:LocalUrl` | `http://localhost:8080/v1/chat/completions` | `LOCAL_LLM_URL` |
| `SmartCut:LocalModel` | blank (ask the server) | `LOCAL_LLM_MODEL` |
| `SmartCut:GoogleModel` | `gemini-2.5-flash-lite` | — |
| `SmartCut:ContextRadius` | `11` | — |

Web video adds the `Sources:*` and `Watch:*` keys, which are listed under
[Watching web video](#configuration-keys).

- **Environment overrides** use the standard `Section__Key` spelling, e.g.
  `Transcription__Model=large-v3-turbo`.
- **The Python's variable names still work** and override `appsettings.json`.
  If both spellings are set for one key, `Section__Key` wins. A blank value
  counts as unset. The value translations (for example `AI_ENHANCE` is true only
  for `true`) are in the T32 output section of [docs/STATUS.md](docs/STATUS.md).
- **Retired variables** from the ROCm/PyTorch era (`ALIGN_DEVICE`,
  `WHISPER_MULTI_GPU`, `WHISPER_ATTN`, `ANALYSIS_CHUNK_SIZE`, `TORCH_INDEX_URL`,
  `HSA_OVERRIDE_GFX_VERSION`, `HF_HOME`, `MIOPEN_USER_DB_PATH`,
  `TRITON_CACHE_DIR`) do nothing; both hosts log a warning when one is set.
  Instead of `ALIGN_DEVICE=cpu`, use `UNLOAD_MODELS_AFTER_JOB` or
  `Transcription__Device=Cpu`.
- **Smart Cut is off by default.** Enabled but unusable (Google mode with no
  key, or an unparseable local URL) logs a warning and behaves as off, and
  `GET /config` reports it as off. A local server that is simply not running
  leaves each hit unchanged.
- **Set the Google API key as the `GOOGLE_API_KEY` environment variable**, and
  never put it in `appsettings.json` or any committed file. It is only read under
  that exact name, and only from the environment: a key in `appsettings.json`,
  under `GOOGLE_API_KEY` or `SmartCut:GoogleApiKey`, is ignored and Smart Cut
  stays off. .NET user-secrets are not supported.

## Tests

```powershell
dotnet test FoulFilterNet.slnx
```

Run it from the repository root. The default run needs no GPU, weights, network
or LLM; 41 tests are skipped unless opted in:

- `RUN_GPU_TESTS=1` — 16 transcription tests and 10 evaluation tests against the
  real engine and the fixtures in `tests/fixtures/media`. Needs FFmpeg and weights in the repository's
  `models/` directory (`FOULFILTER_MODEL_DIR` and `FOULFILTER_TEST_MODEL`
  override where and which; the default model is `large-v3-turbo`). They never
  download anything.
- `RUN_LIVE_LLM_TESTS=1` — 4 Smart Cut tests against a running OpenAI-compatible
  server at `LOCAL_LLM_URL` (optionally `LOCAL_LLM_MODEL`).
- `RUN_WEB_TESTS=1` — 11 tests of the yt-dlp source against the real yt-dlp and
  the real YouTube (one 19-second public video). Needs yt-dlp, Deno and the
  network; `FOULFILTER_YTDLP` and `FOULFILTER_DENO` name the executables when
  they are not on `PATH`.

All three gates accept `1`, `true` or `yes`.

The Chrome extension has its own tests, which need only Node 22 or later:

```powershell
cd extension
npm test
```

> **Never pass `--nologo` to `dotnet test`.** The solution uses
> Microsoft.Testing.Platform, which forwards unrecognised arguments to the test
> executables; they reject it, and the run reports "Zero tests ran" with exit
> code 5 — it looks like broken discovery, not a bad flag.

## More

- [docs/](docs) — the porting plan, the analysis of the Python, the task
  breakdown, and the web video plan and its checklist
  ([docs/README.md](docs/README.md) is the index).
- [docs/STATUS.md](docs/STATUS.md) — the live progress ledger, and the most
  detailed record of how each part actually works and why.
- [docs/adr/](docs/adr) — the port's architecture decisions;
  [Legacy/docs/adr/](Legacy/docs/adr) — the Python's, still referenced.
- [extension/README.md](extension/README.md) — the Chrome extension: its module
  layout, the rules it follows, and its tests.
- [CONTEXT.md](CONTEXT.md) — domain vocabulary and what the .NET design changed.
- [Legacy/README.md](Legacy/README.md) — the original Python application.

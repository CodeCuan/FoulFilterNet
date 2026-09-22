# FoulFilterNet

FoulFilterNet finds profanity in audio and video files and edits it out
(silence, bleep, or cut), built for long-form media such as audiobooks; it also
censors a YouTube video as it plays, through a Chrome extension (see *Web video*
below). It is a
.NET 10 port of the Python FoulFilter, which is kept unmodified under
[`Legacy/`](Legacy) as the behavioural reference; its own vocabulary is in
[Legacy/CONTEXT.md](Legacy/CONTEXT.md).

The port keeps the Python's domain vocabulary on purpose — the types in
`FoulFilterNet.Domain` carry these names. What changed is the engine underneath
and how the application is put together, both described after the glossary.

## Language

### Pipeline artifacts

**Transcript**:
The complete transcription of one media file: ordered Segments with text and
timestamps, and the Words inside them. Persisted to disk because it is
expensive to regenerate (see *Transcript cache* below).
_Avoid_: subtitles, captions, analysis data

**Segment**:
A contiguous span of transcribed speech with a start, an end, and its text.
Candidates are found over Segment text.

**Word**:
One whitespace-delimited word with its own start and end. whisper.cpp reports
sub-word *tokens*, not words, so the transcriber joins tokens into Words before
anything downstream sees them (ADR-0006).

**Candidate**:
A token position in a Segment whose normalized text matches an entry in the Bad
Words List. A Candidate has only approximate timestamps, interpolated across its
Segment by character offset.

**Hit**:
A Candidate confirmed for editing, with final padded start/end times. A Hit
takes its times from the matching Words where they exist, and falls back to the
Candidate's estimate where they do not; Candidates are reconciled to Hits by
time proximity, not by phrase alone. Hits are padded (0.15 s before, 0.25 s
after) and overlapping Hits are merged into one.
_Avoid_: match, detection (ambiguous between candidate and hit)

**Alignment Window** (Python only):
In the Python, a batch of Segments sent to the WhisperX forced aligner. The port
has no forced aligner — Whisper.net returns Words with the transcription — so
there are no windows. The `IAligner` seam survives, behind the no-op
`PassThroughAligner`, so a forced aligner could be reintroduced without changing
the pipeline. The rule that went with windows still holds in principle:
alignment is never partial-by-flaggedness.

### Editing

**Censor Method**:
How a Hit is rendered inaudible: `silence` (zero volume), `bleep` (1 kHz tone),
or `remove` (audio cut out; audio files only — video falls back to silence).
The CLI also accepts `delete` as a spelling of `remove`.
_Avoid_: edit mode, filter mode

**Smart Cut**:
An optional LLM refinement that widens, narrows, or rejects a Hit using
surrounding words (e.g. cutting the whole idiom "go to hell", skipping the tool
sense of "hoe"). Never creates new Hits. Widening is only permitted when the
Censor Method is `remove` on an audio file; silence and bleep (and all video)
always stay surgical because stretched dead air is more noticeable than the
word. **Off by default** (`SmartCut:Enabled`). When it is off, or on but
unusable (no Google API key, an unparseable local URL), a no-op advisor keeps
every Hit as found, and `GET /config` reports it as off.
_Avoid_: AI pass, semantic removal

### Jobs

**Job**:
One file moving through the pipeline, with progress state exposed over SSE.
Jobs are ephemeral: they exist only while the process runs, and a restart wipes
the upload and scratch directories.
_Avoid_: task, queue item

**Batch**:
A set of files uploaded together that become independent Jobs processed strictly
one at a time (GPU serialization).

**Resume**:
Reusing a persisted Transcript (matched by a hash of the file's content) to skip
transcription for a file processed before. A Rescan Pass never resumes: it is a
request to listen again, so the cache is not consulted.

**Rescan Pass**:
An optional second full transcription of the same file with 4 s of silence
prepended, so every chunk boundary moves. Catches swear words the first pass
missed — typically words garbled when they straddle a chunk boundary. Detection
only: it adds Segments before matching, and its timestamps are shifted back onto
the original timeline.
_Avoid_: double pass, offset pass, Scan Pass

### Configuration inputs

**Bad Words List**:
The plain-text file of words/phrases (one per line) that must not survive into
the output. Lines starting with `#` are ignored. Matching is case- and
punctuation-insensitive over whole tokens (`[a-z0-9']+`), never substrings;
entries may be multi-word phrases up to three words.
_Avoid_: blacklist, blocklist, swear list

**Legacy variable**:
One of the Python's environment variable names (`DATA_DIR`, `WHISPER_MODEL`,
`AI_ENHANCE`, ...), still honoured by mapping it onto a .NET configuration key.
A **retired** legacy variable (`ALIGN_DEVICE`, `HSA_OVERRIDE_GFX_VERSION`, ...)
does nothing and is warned about at startup.

**Scan Pass** (deprecated):
The Python's removed first-generation architecture that transcribed the whole
file twice. Superseded there by the hybrid design in ADR-0001, which the port in
turn replaced with ADR-0006.

### Web video

Filtering a YouTube video live as it plays, rather than editing a file
([04-web-video-plan.md](docs/04-web-video-plan.md),
[ADR-0007](docs/adr/0007-web-video-two-streams.md); how to run it is in
[README.md](README.md#watching-web-video)).

**Web Video**:
A video identified by a provider and that provider's ID (`youtube`,
`dQw4w9WgXcQ`), rather than by a file. Its canonical key is `youtube-<id>`, which
is also its Transcript cache key.
_Avoid_: URL (a URL is only one way to name it)

**Watch Session**:
The server's work to make one Web Video safe to watch: fetching its audio,
transcribing it window by window, and serving Hits as they are confirmed. It
lasts only while it is being watched, and ends after a period with no heartbeat.
The Transcript it builds outlives it (ADR-0002). Its states are Queued →
Fetching → Preparing → Transcribing → Complete, or Failed, Unsupported
(a livestream, say) or Cancelled — cancelled by the viewer, or by idle expiry.
One session serves every viewer of the same Web Video.
_Avoid_: job (a Job turns one file into another file), stream

**Heartbeat**:
The extension's poll of `POST /watch`, about once a second while a watch page is
open. It starts the Watch Session if there is none, carries the playhead (which
decides which window is transcribed next), keeps the session alive, and is
answered with the current snapshot.

**Coverage**:
The parts of a Web Video's timeline whose Hits are final: the union of the
finished windows' shares, trimmed by a 1 s **guard** at an edge whose
neighbouring window is not finished, where a phrase or its padding could still
run across (1.05 s when priority words are cut wider: their 0.8 s minimum
length plus 0.25 s pre-roll). It is a set of intervals, not one high-water mark, because a seek
can have windows transcribed out of order.

**Playback Gate**:
The extension's rule that holds playback while the stretch just ahead of the
playhead is not covered, and resumes once enough is. The thresholds are
wall-clock seconds — hold under 8, release at 30 — so they scale with the
playback rate.

**Live Censoring**:
Rendering the Censor Method at playback time by automating the page's audio, as
opposed to rendering a file. Only `silence` and `bleep` exist here. `remove`
falls back to `silence`, as it already does for video (ADR-0004).

## What the .NET design changed

### Speech recognition: Whisper.net instead of the hybrid

The Python transcribed with Hugging Face Whisper and recovered word timestamps
with WhisperX's wav2vec2 forced aligner — a split that existed only because
CTranslate2 had no ROCm build for its AMD card (ADR-0001). The port runs
**whisper.cpp through Whisper.net**, on CUDA where a usable NVIDIA card and
runtime exist and on the CPU otherwise. One model does both jobs: word
boundaries come from whisper.cpp's DTW timestamps, read as token *ends*, which
was measured against the fixtures and keeps every planted hit inside the
padding. The measurement, the rejected alternatives and the consequences are in
[ADR-0006](docs/adr/0006-whisper-net-collapses-transcription-and-alignment.md).

Weights are GGML files (`ggml-<size>.bin`) in `Transcription:ModelDirectory`.
The web service downloads a missing model on first use; the CLI never does.

### Project layout and dependency direction

One assembly per concern under `src/`, each with a matching `*.Tests` project
under `tests/`:

| Project | Holds | References |
|---|---|---|
| `FoulFilterNet.Domain` | Artifacts, frozen contracts (`Abstractions`), and the pure rules: tokenizer, Bad Words List, phrase matching, hit padding/merging, Smart Cut index mapping | nothing |
| `FoulFilterNet.Media` | FFmpeg/FFprobe: filtergraph builders, probing, audio preparation, the editor | Domain |
| `FoulFilterNet.Transcription` | Whisper.net engine, model files, Rescan arithmetic, release policy, `PassThroughAligner` | Domain |
| `FoulFilterNet.SmartCut` | Prompt, response parsing, Gemini and OpenAI-compatible transports, the advisor | Domain |
| `FoulFilterNet.Pipeline` | `MediaPipeline`, transcript cache, hit reconciliation, data locations, legacy variables | Domain, Media, Transcription, SmartCut |
| `FoulFilterNet.Jobs` | Job queue (`Channel<T>`), worker, event fan-out | Domain, Pipeline |
| `FoulFilterNet.Sources` | Web Video sources: `VideoRef` parsing and validation, the `IWebAudioSource` contract, the yt-dlp adapter, the development-only `file` provider | Domain, Media |
| `FoulFilterNet.Watch` | Watch Sessions: Coverage, the window scheduler, partial Hit snapshots (`WatchProgress`), the session, the manager and its sweeper | Domain, Pipeline, Transcription, Sources |
| `FoulFilterNet.Web` | ASP.NET Core minimal API, SSE, uploads, static UI, the Watch endpoints; composition root | Domain, Pipeline, Jobs, Sources, Watch |
| `FoulFilterNet.Cli` | `foulfilter`, the `find_and_remove.py` equivalent; composition root | Domain, Media, Pipeline, SmartCut, Transcription |
| `FoulFilterNet.Evaluation` | `foulfilter-eval`, the `eval_misses.py` equivalent: scores the pipeline against the fixture manifest; composition root | Domain, Media, Pipeline, SmartCut, Transcription |

Dependencies point inward to `Domain`. `MediaPipeline` reaches every engine
through the `Domain.Abstractions` interfaces (`ITranscriber`, `IAligner`,
`ISmartCutAdvisor`, `IMediaProber`, `IAudioPreparer`, `IMediaEditor`,
`ITranscriptStore`), so it is unit-tested with stubs; the two hosts are the only
places concrete engines are chosen and wired. Optional behaviour resolves to an
implementation rather than a branch — the no-op Smart Cut advisor, and
`ReleasePolicyTranscriber` for `Transcription:UnloadAfterJob` — so the pipeline
never checks a flag.

### Jobs and SSE

The Python's deque, lock and 0.5 s poll loop became a `Channel<T>` that one
`BackgroundService` worker blocks on, and its shared cancelled-set became a
`CancellationTokenSource` per job, checked at every pipeline checkpoint.
`GET /events` is server-sent events: a snapshot of every job on connect, then
unnamed events as jobs progress, with the stage names and percentages the
unchanged front end expects. A slow subscriber cannot stall the worker. The HTTP
surface (`/upload`, `/jobs`, `/status/{id}`, `/download/{id}`, `/download_zip`,
`/events`, `/config`) is the Python's, plus `/health` and the web video
endpoints the extension drives (`POST /watch`, `GET`/`DELETE
/watch/{provider}/{id}`).

### Configuration and legacy variables

`src/FoulFilterNet.Web/appsettings.json` is the primary source, and the CLI
reads the same file (it is copied beside the executable). Environment variables
override it under two spellings: the standard `Section__Key`
(`Transcription__Model`) and the legacy names, which `LegacyEnvironmentVariables`
translates. Where both are set for one key, `Section__Key` wins. A blank legacy
variable counts as unset. The Google API key is a secret: it is read only under
the name `GOOGLE_API_KEY`, is supplied through the environment, and is never
written into `appsettings.json`. The full mapping table is in the T32 output
section of [docs/STATUS.md](docs/STATUS.md).

Data defaults to a per-user folder (`%LOCALAPPDATA%\FoulFilterNet` on Windows,
`~/.local/share/FoulFilterNet` on Linux) instead of the Python's `/data`.
There is no container: the app runs natively (see "Container dropped" in
[docs/STATUS.md](docs/STATUS.md)).

### Transcript cache

Unchanged in purpose (ADR-0002): Jobs are ephemeral, Transcripts are not. A
Transcript is stored as `<md5>_<name>.json` under `Storage:TranscriptDirectory`
(default `<data directory>/transcripts`). Both hosts resolve the same directory
from the same keys, so a file transcribed by either is a Resume for the other.
The stored schema version is now checked — a mismatch, an unreadable file, or a
hash that disagrees with its name is a cache miss, never a failed job — and
writes go to a temporary file moved into place, so an interrupted save cannot
poison the cache. The Python wrote schema version 3 and the port writes version
1, so a Transcript left by the Python is a miss and the file is transcribed
again. A Web Video has no file to hash, so a Watch Session stores its Transcript
under the Web Video's key instead (`youtube-<id>_<title>.json`) in the same
directory; that is what makes a second viewing instant.

## Architecture decisions

The port's own decisions are in [`docs/adr/`](docs/adr):

- [ADR-0006](docs/adr/0006-whisper-net-collapses-transcription-and-alignment.md) —
  Whisper.net on CUDA collapses transcription and alignment
- [ADR-0007](docs/adr/0007-web-video-two-streams.md) — web video runs as two
  streams: server-side audio acquisition, client-side Live Censoring

The Python's decisions are frozen history in
[`Legacy/docs/adr/`](Legacy/docs/adr). They still explain behaviour the port
preserves:

- [ADR-0001](Legacy/docs/adr/0001-hybrid-asr.md) — hybrid ASR (superseded by
  ADR-0006; its all-or-nothing alignment rule survives)
- [ADR-0002](Legacy/docs/adr/0002-ephemeral-jobs-persistent-transcripts.md) —
  ephemeral jobs, persistent transcripts (still holds)
- [ADR-0003](Legacy/docs/adr/0003-single-container-consolidation.md) — single
  container (still holds; the port is one process)
- [ADR-0004](Legacy/docs/adr/0004-smart-cut-widening-scope.md) — Smart Cut
  widening scope (still holds)
- [ADR-0005](Legacy/docs/adr/0005-slim-base-image-vendor-pinned-wheels.md) —
  slim base image with vendor-pinned PyTorch wheels (does not apply: there is
  no PyTorch)

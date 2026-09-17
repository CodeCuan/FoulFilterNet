# 01 — Analysis of the Legacy Python Solution

Reference tree: [`/Legacy`](../Legacy) — 2,382 lines of Python across 14 modules,
plus a framework-free HTML/CSS/JS front end and Docker packaging.

---

## 1. What the application does

One media file (audio or video) goes in; a copy with every word on a
**Bad Words List** rendered inaudible comes out. The interesting work is
finding *where* those words are to within a few tens of milliseconds, because
the edit is surgical — a mis-timed cut is more noticeable than the profanity.

The vocabulary in [Legacy/CONTEXT.md](../Legacy/CONTEXT.md) is precise and worth
carrying over verbatim: **Transcript, Segment, Candidate, Alignment Window,
Hit, Censor Method, Smart Cut, Job, Batch, Resume, Rescan Pass, Bad Words List.**
The port should use exactly these names for its types.

## 2. Runtime topology

A single container (ADR-0003) running Uvicorn. Inside it:

- a **FastAPI** app serving the UI, the upload/download endpoints, and an SSE
  event stream;
- one **worker thread** draining an in-memory queue — jobs run strictly one at
  a time because they contend for a single GPU;
- the **pipeline** and its two model engines, held as process-wide singletons so
  weights survive between jobs.

Job state is deliberately ephemeral (ADR-0002): a restart loses the queue and
wipes `uploads/` and `scratch/`. **Transcripts** are the one durable artifact,
keyed by file hash under `/data/transcripts`, because transcription is by far
the most expensive stage and re-running a file is common.

## 3. Pipeline stages

`pipeline._run_job_impl` is the spine. Stages, with the progress percentages
the UI depends on:

| % | Stage | What happens |
|---|---|---|
| 2 | `preparing` | Video only: extract the audio track to `scratch/extracted_audio.m4a` (AAC) |
| 5–40 | `transcribing` | Whisper on GPU produces `Segment[]` |
| 42 | `transcribing` | Optional **Rescan Pass** — re-transcribe with boundaries shifted, union the segments |
| 45 | `transcribing` | …or skip both, reusing a cached Transcript (**Resume**) |
| 50 | `matching` | Bad Words List produces `Candidate[]` over segment text |
| 55–75 | `aligning` | Forced alignment of the *whole* transcript produces `Word[]` with precise times |
| 78–88 | `refining` | Optional **Smart Cut** LLM pass over each Hit |
| 88–90 | `editing` | FFmpeg renders the Censor Method |
| 100 | `completed` | |

Two structural rules fall out of the ADRs:

- **Alignment is all-or-nothing.** If any Candidate exists, the entire
  transcript is aligned, not just the flagged neighbourhoods (ADR-0001). This
  keeps word precision uniform and lets the Rescan Pass improve recall without
  forcing a re-align.
- **Widening is scoped.** Smart Cut may only *widen* a Hit when the Censor
  Method is `remove` **and** the media is audio (ADR-0004). Rejection of false
  positives is always allowed, because skipping never lengthens an edit.

## 4. Module inventory

| Module | LoC | Role | Purity |
|---|---:|---|---|
| `detector.py` | 112 | Tokenize, match Bad Words List, dedupe overlaps | **Pure** |
| `pipeline.py` | 352 | Orchestration, transcript cache, hit padding/merging | Mixed |
| `word_remover.py` | 195 | FFmpeg filtergraph construction + invocation | Mixed |
| `ai_helper.py` | 283 | Smart Cut prompt, LLM transports, index-to-time mapping | Mixed |
| `transcriber.py` | 159 | HF Whisper wrapper, Rescan Pass | Adapter |
| `aligner.py` | 151 | WhisperX wav2vec2 forced alignment in batches | Adapter |
| `main.py` | 413 | FastAPI app, JobManager, SSE, upload/download | Mixed |
| `find_and_remove.py` | 98 | CLI entry point | Adapter |
| `analyze_file_type.py` | 19 | libmagic media sniffing | Adapter |
| `video_edit.py` | 13 | Mux a new audio track into a video | Adapter |

The pure/mixed split matters: `detector`, the filtergraph builders, `merge_hits`
and `apply_smart_cut_indices` are already pure functions over plain dicts, and
they are exactly what the existing test suite covers. They port directly and
should be the first things written in .NET.

## 5. Behaviour that must be preserved

### 5.1 Matching (`detector.py`)

- Tokenizer is `[a-z0-9']+` over lowercased text — apostrophes are part of a
  token, everything else is a separator. This is why "classify the class" does
  **not** match `ass`: matching is over whole tokens, never substrings.
- Phrases up to **3 tokens**. List entries are normalized to token tuples;
  blank lines and `#` comments are dropped; an entry longer than 3 tokens is
  silently discarded.
- N-grams are tried **longest first**, then the dedupe step sorts by
  `(start, -length)` and drops any match fully contained in one already kept —
  so "go to hell" wins over a bare "hell" inside it.
- `find_candidates` interpolates approximate times linearly across a segment by
  *character* offset. `find_hits` does the same matching over aligned words and
  gets exact times.

### 5.2 Padding and merging (`pipeline.merge_hits`)

Pre-padding **0.15 s**, post-padding **0.25 s** (asymmetric — speech onsets are
detected late). Inverted windows are dropped, starts clamp at zero, everything
rounds to 3 decimals, then overlapping windows merge and their phrases
concatenate with `+`. These constants and the rounding are load-bearing: the
filtergraph strings the tests assert on are built from them.

### 5.3 Filtergraphs (`word_remover.py`)

Three graphs, asserted character-for-character by the existing tests:

- **silence** — comma-joined `volume` filters gated by `between(t,S,E)` with
  `volume=0`
- **bleep** — normalize to 44.1 kHz stereo, mute the hit windows to make
  `[base]`, then generate one 1 kHz `sine` per hit, `adelay` it into position,
  and `amix` everything with `normalize=0` so untouched audio keeps full volume
- **remove** — `atrim` every span *outside* the hits, `asetpts=PTS-STARTPTS`
  each, and `concat` them; raises if the hits would consume the whole file

Video keeps its frames: the audio is filtered and `-c:v copy` passes the video
stream through untouched. Bleep on video needs generated sources, so it renders
a censored audio track to a temp file first and muxes it back in.

### 5.4 Smart Cut (`ai_helper.py`)

A heavily few-shot-prompted LLM is handed a ±11-word window and the target
phrase, and returns `{reasoning, start_index, end_index}`. `-1/-1` means
*reject this Hit*. When widening is not permitted the indices are clamped to
the target word before mapping back to timestamps, which is how ADR-0004 is
actually enforced. Two transports: Gemini (`gemini-2.5-flash-lite`, all safety
categories `BLOCK_NONE`, exponential backoff on 429/503) and any
OpenAI-compatible endpoint, with a neat self-healing retry that asks `/models`
for the server's real model id when the configured one is rejected.

Failures degrade rather than propagate: an unreachable LLM returns `None`,
meaning *keep the original timestamps*, and the pipeline catches every
exception from the advisor so a flaky LLM can never fail a job.

### 5.5 Jobs (`main.py`)

Queue, worker and SSE fan-out, all hand-rolled on `threading`: a `deque`, a
`Lock`, a set of subscriber `Queue`s, and a 0.5 s poll loop. Cancellation of a
*running* job works by adding its id to a set that the progress callback checks
at the next checkpoint, raising `JobCancelled` from inside the pipeline.

Upload handling worth keeping: filenames are sanitized (basename only,
lowercased extension, colon becomes " -", brackets and quotes stripped)
specifically because those characters break FFmpeg filter parsing; uploads
stream in 1 MB chunks against a `MAX_UPLOAD_MB` cap.

## 6. Configuration surface

All via environment (`.env`). The ones with real behavioural weight:
`WHISPER_MODEL`, `WHISPER_LANGUAGE`, `CENSOR_METHOD`, `ALIGN_DEVICE`,
`AI_ENHANCE`, `AI_MODE`, `GOOGLE_API_KEY`, `LOCAL_LLM_URL`, `LOCAL_LLM_MODEL`,
`UNLOAD_MODELS_AFTER_JOB`, `BAD_WORDS_PATH`, `MAX_UPLOAD_MB`, `DATA_DIR`,
`TRANSCRIPT_DIR`. The rest (`HSA_OVERRIDE_GFX_VERSION`, `TORCH_INDEX_URL`,
`WHISPER_MULTI_GPU`, `WHISPER_ATTN`) exist only to wrangle ROCm/PyTorch and
become irrelevant under a different ASR stack.

## 7. HTTP surface

| Method | Path | Notes |
|---|---|---|
| POST | `/upload` | multipart, N files plus `censor_method`, `debug`, `rescan`; returns job ids |
| GET | `/jobs` | all job records, `*_path` keys stripped |
| GET | `/status/{job_id}` | one record |
| DELETE | `/jobs/{job_id}` | cancel queued or running; also used as "remove row" |
| GET | `/download/{job_id}` | the censored output |
| GET | `/download_zip?ids=a,b,c` | zip of completed outputs |
| GET | `/events` | SSE, `retry: 3000`, 15 s keepalive comments |
| GET | `/config` | UI feature flags |
| GET | `/` and `/static/*` | the UI |

The front end is vanilla ES6 with no build step and no dependencies — **it can
be carried across to `wwwroot` essentially unchanged**, which removes a whole
category of porting work.

## 8. Findings

Things noticed while reading that the port should fix rather than faithfully
reproduce. Each becomes a note on the relevant task.

1. **Smart Cut is unreachable in production.** `pipeline.run_job` accepts a
   `smart_cut` callable, but neither [main.py](../Legacy/src/main.py) nor
   [find_and_remove.py](../Legacy/src/find_and_remove.py) ever passes one.
   `GET /config` still advertises `ai_enhance: true` when the env vars are set,
   so the UI shows a "Smart Cut active" badge for a feature that never runs.
   The entire `ai_helper` module is reachable only from tests. **The port
   should wire it up** — the code is complete, it is just not connected.

2. **Fallback for unaligned candidates is matched by phrase, not position.**
   In `_run_job_impl`, `aligned_phrases` is a *set* of phrase strings, and any
   Candidate whose phrase is in that set is considered covered. If a word occurs
   five times and alignment only recovered one, the other four get no estimate
   fallback and are silently not censored. This is a recall bug. The port should
   reconcile Candidates to Hits **by time proximity**, not by string equality.

3. **`/download_zip` double-prefixes names.** Output files are already named
   `censored_<name>`, and the zip writes them with another `censored_` prefix,
   producing `censored_censored_book.mp3`.

4. **Transcript `version` is written but never checked.** `{"version": 3}` is
   stored and ignored on load, so a transcript from an older schema would be
   reused as if current. The port should validate it and treat a mismatch as a
   cache miss.

5. **`JobManager.cancelled` is mutated outside the lock** (added and discarded
   from both the request thread and the worker). Benign under CPython's GIL, but
   not the kind of thing that should survive into a port running on a real
   threading model. A per-job `CancellationTokenSource` replaces it cleanly.

6. **The worker busy-polls** the queue every 0.5 s rather than blocking on it.

7. **Media sniffing is doubly-specified** — `analyze_file_type` runs libmagic but
   short-circuits `.m4b`/`.m4a` by extension first, because libmagic reports
   those inconsistently. Since FFmpeg is already a hard dependency, FFprobe can
   answer "does this have a video stream?" authoritatively and libmagic can go.

## 9. Mapping to .NET

| Python | .NET | Note |
|---|---|---|
| `detector`, `merge_hits`, `apply_smart_cut_indices` | `FoulFilterNet.Domain` | Direct port, pure functions |
| filtergraph builders | `FoulFilterNet.Media` | Direct port; strings stay identical |
| `subprocess` calls to ffmpeg | `IFFmpegRunner` over `System.Diagnostics.Process` | Keep args identical for comparability |
| `analyze_file_type` (libmagic) | FFprobe stream inspection | Drops the `python-magic` dependency |
| `transcriber` plus `aligner` | `ITranscriber` / `IAligner` | **See below** |
| `ai_helper` transports | typed `HttpClient`s behind `ISmartCutAdvisor` | Prompt template ports verbatim |
| `JobManager` deque plus thread | `Channel<T>` plus `BackgroundService` | Blocking read, no poll loop |
| cancelled-set | `CancellationTokenSource` per job | Cooperative cancellation throughout |
| hand-rolled SSE | `TypedResults.ServerSentEvents` | First-class in .NET 10 |
| FastAPI routes | ASP.NET Core minimal API | One-to-one |
| `argparse` CLI | `System.CommandLine` | One-to-one |
| static UI | `wwwroot`, unchanged | No build step |
| env vars | `IOptions<T>` bound from configuration | Same variable names |

### 9.1 The ASR decision

This is the one stage with no equivalent library. The Python design is a
*hybrid* — HF Whisper for text, WhisperX's wav2vec2 aligner for word times —
and ADR-0001 is explicit that the split exists **only** because CTranslate2 has
no usable ROCm build. It is a workaround for a platform problem, not a
preference.

**Recommendation: `Whisper.net`** (whisper.cpp bindings). It is a maintained
NuGet package, it emits **word-level timestamps directly**, and it ships a
first-class CUDA runtime. That collapses transcription and alignment into a
single stage and deletes the entire reason ADR-0001 exists.

The target machine is an **RTX 3080 Ti (12 GB, Ampere)**, which makes this
easier than the analysis above assumed. Every constraint that shaped the Python
design was an AMD/ROCm constraint:

| Legacy workaround | Why it existed | On the 3080 Ti |
|---|---|---|
| Hybrid HF Whisper + WhisperX aligner (ADR-0001) | CTranslate2 has no RDNA2 ROCm build | Moot — CUDA is the best-supported path everywhere |
| `HSA_OVERRIDE_GFX_VERSION=10.3.0` | RDNA2 needs to masquerade as gfx1030 | Delete |
| `TORCH_INDEX_URL` vendor switching (ADR-0005) | Pick the ROCm vs CUDA wheel set | Delete — no PyTorch at all |
| `WHISPER_MULTI_GPU`, `WHISPER_ATTN=eager` | RDNA2 sharding faults, Triton SDPA bugs | Delete — single card, no Triton |
| `ALIGN_DEVICE=cpu` escape hatch | Aligner OOM while an LLM held VRAM | Keep the *idea* — 12 GB is shared with Smart Cut's local LLM if one is used |

12 GB comfortably holds `large-v3-turbo`, so model size is a free choice rather
than a compromise, and `UNLOAD_MODELS_AFTER_JOB` survives for VRAM sharing on a
desktop rather than for ROCm fragility.

Consequences to accept and record as a new ADR:

- Word timestamps come from whisper.cpp's DTW heuristic rather than a wav2vec2
  forced aligner, so boundaries will be *somewhat* looser. The existing
  0.15/0.25 s padding absorbs a good deal of that, and `scripts/eval_misses.py`
  in the Legacy tree already measures boundary error — it is the natural
  yardstick for deciding whether the difference matters in practice.
- `IAligner` stays in the design as a seam. When the transcriber already returns
  words, alignment is a no-op; if DTW precision proves insufficient, a forced
  aligner can be reintroduced behind that interface without touching the
  pipeline.
- The Rescan Pass, the transcript cache, and `UNLOAD_MODELS_AFTER_JOB` all
  survive unchanged — they are properties of the pipeline, not of the engine.

Alternatives considered: **ONNX Runtime with an exported Whisper** (more
plumbing, no word timestamps for free), and **keeping Python as an ASR sidecar**
(preserves exact behaviour but reintroduces the two-process topology ADR-0003
deliberately removed, and leaves the hardest dependency in place).

### 9.2 Deployment note

The current image is ~10 GB, almost entirely PyTorch wheels. An
`aspnet:10.0` base plus FFmpeg plus a whisper.cpp CUDA runtime is a few hundred
MB, with model weights fetched at runtime into the existing `/data` volume.

The port is also not Linux-bound, which matters here: the development machine
is Windows 11. The .NET app runs natively on the host with direct CUDA access,
so **Docker stops being a prerequisite for development** — it becomes a
deployment option rather than the only way to run the thing. Containerised GPU
access on Windows means WSL2 plus the NVIDIA container toolkit, which works on
this hardware but is no longer something to fight with just to see the app run.

*Later decision: the container was dropped entirely. The port runs natively, and
the Dockerfile T32 wrote was never built before being removed - see STATUS,
"Container dropped".*

# FoulFilterNet

Automated profanity removal for audio and video files, built for long-form media
such as audiobooks, podcasts and films. Upload files in the browser (or run one
from the terminal), and every word or phrase on your Bad Words List is made
inaudible — silenced, bleeped, or cut out — with the picture of a video left
untouched.

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
dotnet run --project src/FoulFilterNet.Web -- --urls http://localhost:8000
```

Open <http://localhost:8000>, drop files, pick a censor method, upload. Jobs run
one at a time and report progress live.

`dotnet run` applies `src/FoulFilterNet.Web/Properties/launchSettings.json`
(Development environment; `--urls` overrides its ports) and runs Web with
`src/FoulFilterNet.Web` as its working directory. That is why the model
directory is set explicitly above; without it weights would be downloaded into
`src/FoulFilterNet.Web/models`.

Web reads the Bad Words List from `bad_words.txt` in the data directory —
`%LOCALAPPDATA%\FoulFilterNet` on Windows, `~/.local/share/FoulFilterNet` on
Linux — unless `Storage:BadWordsPath` says otherwise. Nothing is seeded there;
[`Legacy/data/bad_words.example.txt`](Legacy/data/bad_words.example.txt) is a
template. One word or phrase per line, `#` starts a comment, matching is case-
and punctuation-insensitive, and phrases of up to three words are supported.

The data directory also holds `uploads/`, `outputs/`, `scratch/` and the
transcript cache `transcripts/`. On startup Web empties `uploads/` and
`scratch/`; outputs and transcripts are kept.

`GET /health` answers `{"status":"ok"}`; `GET /config` shows the model and
whether Smart Cut is really in use.

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

## Running in a container — not yet built or verified

A [`Dockerfile`](Dockerfile), [`docker-compose.yml`](docker-compose.yml) and
[`.env.example`](.env.example) exist, **but the image has never been built and no
job has run inside it.** They were written without a Docker daemon; the
assumptions they rest on, and how confident each is, are listed in the T32
output section of [docs/STATUS.md](docs/STATUS.md). Treat the commands below as
the intended usage, not a tested recipe.

```sh
cp .env.example .env        # optional; never commit it
docker compose build
docker compose up -d        # http://localhost:8000
docker compose exec foulfilter dotnet /app/foulfilter.dll /data/in.mp3 /data/bad_words.txt
```

It needs the NVIDIA Container Toolkit (on Windows, Docker Desktop with WSL2).
Everything persistent lives in `./data` mounted at `/data`, including the
weights in `/data/models`; put `bad_words.txt` there yourself.

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
or LLM; 13 tests are skipped unless opted in:

- `RUN_GPU_TESTS=1` — 9 transcription tests against the real engine and the
  fixtures in `tests/fixtures/media`. Needs FFmpeg and weights in the repository's
  `models/` directory (`FOULFILTER_MODEL_DIR` and `FOULFILTER_TEST_MODEL`
  override where and which; the default model is `large-v3-turbo`). They never
  download anything.
- `RUN_LIVE_LLM_TESTS=1` — 4 Smart Cut tests against a running OpenAI-compatible
  server at `LOCAL_LLM_URL` (optionally `LOCAL_LLM_MODEL`).

Both gates accept `1`, `true` or `yes`.

> **Never pass `--nologo` to `dotnet test`.** The solution uses
> Microsoft.Testing.Platform, which forwards unrecognised arguments to the test
> executables; they reject it, and the run reports "Zero tests ran" with exit
> code 5 — it looks like broken discovery, not a bad flag.

## More

- [docs/](docs) — the porting plan, the analysis of the Python, and the task
  breakdown ([docs/README.md](docs/README.md) is the index).
- [docs/STATUS.md](docs/STATUS.md) — the live progress ledger, and the most
  detailed record of how each part actually works and why.
- [docs/adr/](docs/adr) — the port's architecture decisions;
  [Legacy/docs/adr/](Legacy/docs/adr) — the Python's, still referenced.
- [CONTEXT.md](CONTEXT.md) — domain vocabulary and what the .NET design changed.
- [Legacy/README.md](Legacy/README.md) — the original Python application.

# FoulFilterNet Documentation

Porting FoulFilter (Python / FastAPI) to .NET 10.

| Doc | Purpose |
|---|---|
| [00-porting-plan.md](00-porting-plan.md) | Ground rules, target architecture, conventions, definition of done |
| [01-python-analysis.md](01-python-analysis.md) | Breakdown of the legacy Python solution |
| [02-task-breakdown.md](02-task-breakdown.md) | The ordered task list used to drive implementation |
| [03-parallelisation-review.md](03-parallelisation-review.md) | Which tasks can run concurrently |
| [04-web-video-plan.md](04-web-video-plan.md) | Filtering YouTube playback live via a Chrome extension (tasks W00–W18) |
| [STATUS.md](STATUS.md) | **Live progress ledger** — branches, commits, decisions. Read this first when resuming. |
| [adr/](adr) | The port's architecture decisions, numbered on from the Python's: [ADR-0006](adr/0006-whisper-net-collapses-transcription-and-alignment.md) (Whisper.net collapses transcription and alignment) |
| [../CONTEXT.md](../CONTEXT.md) | Domain vocabulary, and what the .NET design changed |
| [../README.md](../README.md) | Building, running, configuring and testing the solution |

The original Python implementation is preserved unmodified under [`/Legacy`](../Legacy)
and remains the reference for behaviour. Its domain vocabulary
([Legacy/CONTEXT.md](../Legacy/CONTEXT.md)) is carried forward in
[CONTEXT.md](../CONTEXT.md), and its architecture decisions
([Legacy/docs/adr](../Legacy/docs/adr), ADR-0001 to ADR-0005) are frozen
history that the port's ADRs build on; ADR-0006 supersedes ADR-0001.

## Test media

[`tests/fixtures/media`](../tests/fixtures/media) holds seven generated fixtures
with exact ground-truth timestamps in `manifest.json`. Regenerate with:

```powershell
powershell -ExecutionPolicy Bypass -File tests/fixtures/generate-fixtures.ps1
```

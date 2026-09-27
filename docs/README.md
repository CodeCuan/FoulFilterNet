# FoulFilterNet Documentation

Porting FoulFilter (Python / FastAPI) to .NET 10.

| Doc | Purpose |
|---|---|
| [00-porting-plan.md](00-porting-plan.md) | Ground rules, target architecture, conventions, definition of done |
| [01-python-analysis.md](01-python-analysis.md) | Breakdown of the legacy Python solution |
| [02-task-breakdown.md](02-task-breakdown.md) | The ordered task list used to drive implementation |
| [03-parallelisation-review.md](03-parallelisation-review.md) | Which tasks can run concurrently |
| [04-web-video-plan.md](04-web-video-plan.md) | Filtering YouTube playback live via a Chrome extension: the design, and the task breakdown W00–W19 |
| [05-crosstalk-plan.md](05-crosstalk-plan.md) | Hearing a swear spoken under another voice: the Priority Word Pass, its evidence, and the task breakdown X01–X04 |
| [web-video-checklist.md](web-video-checklist.md) | Web video end to end: the dev harness (how to run it, its recorded results) and the manual checklist for stock Chrome on real YouTube |
| [../extension/README.md](../extension/README.md) | The Chrome extension: module layout, the rules it follows, loading it unpacked, and its tests |
| [native-crash-trace.md](native-crash-trace.md) | Catching the `0xC0000409` fast-fail out of whisper.cpp: the trace, the launch profile, and how to read what it leaves |
| [STATUS.md](STATUS.md) | **Live progress ledger** — branches, commits, decisions. Read this first when resuming. |
| [adr/](adr) | The port's architecture decisions, numbered on from the Python's: [ADR-0006](adr/0006-whisper-net-collapses-transcription-and-alignment.md) (Whisper.net collapses transcription and alignment), [ADR-0007](adr/0007-web-video-two-streams.md) (web video runs as two streams), [ADR-0008](adr/0008-crosstalk-priority-word-pass.md) (crosstalk is caught by a prompted pass over short sub-windows) |
| [../CONTEXT.md](../CONTEXT.md) | Domain vocabulary, and what the .NET design changed |
| [../README.md](../README.md) | Building, running, configuring and testing the solution |

The original Python implementation is [FoulFilter](https://github.com/therealmichaelberna/FoulFilter)
remains the reference for behaviour. Its domain vocabulary
([CONTEXT.md](https://github.com/therealmichaelberna/FoulFilter/blob/main/CONTEXT.md)) is carried forward in
[CONTEXT.md](../CONTEXT.md), and its architecture decisions
([docs/adr](https://github.com/therealmichaelberna/FoulFilter/tree/main/docs/adr), ADR-0001 to ADR-0005) are frozen
history that the port's ADRs build on; ADR-0006 supersedes ADR-0001.

## Test media

[`tests/fixtures/media`](../tests/fixtures/media) holds seven generated fixtures
with exact ground-truth timestamps in `manifest.json`. Regenerate with:

```powershell
powershell -ExecutionPolicy Bypass -File tests/fixtures/generate-fixtures.ps1
```

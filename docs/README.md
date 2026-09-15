# FoulFilterNet Documentation

Porting FoulFilter (Python / FastAPI) to .NET 10.

| Doc | Purpose |
|---|---|
| [00-porting-plan.md](00-porting-plan.md) | Ground rules, target architecture, conventions, definition of done |
| [01-python-analysis.md](01-python-analysis.md) | Breakdown of the legacy Python solution |
| [02-task-breakdown.md](02-task-breakdown.md) | The ordered task list used to drive implementation |
| [03-parallelisation-review.md](03-parallelisation-review.md) | Which tasks can run concurrently |
| [STATUS.md](STATUS.md) | **Live progress ledger** — branches, commits, decisions. Read this first when resuming. |

The original Python implementation is preserved unmodified under [`/Legacy`](../Legacy)
and remains the reference for behaviour. Its domain vocabulary
([Legacy/CONTEXT.md](../Legacy/CONTEXT.md)) and architecture decisions
([Legacy/docs/adr](../Legacy/docs/adr)) carry over to the port.

## Test media

[`tests/fixtures/media`](../tests/fixtures/media) holds seven generated fixtures
with exact ground-truth timestamps in `manifest.json`. Regenerate with:

```powershell
powershell -ExecutionPolicy Bypass -File tests/fixtures/generate-fixtures.ps1
```

# 05 — Crosstalk: the Priority Word Pass

Status: **done** — X01–X04 are on `main`, and X05 made the pass's tuning
configuration (see [STATUS.md](STATUS.md)); the decision, the measurements and
the options that were rejected are recorded in
[ADR-0008](adr/0008-crosstalk-priority-word-pass.md). Tasks are numbered
`X01`–`X05` so they cannot be confused with the port's `T01`–`T34` or web
video's `W00`–`W19`.

## Problem

When two people talk over each other, Whisper transcribes the dominant voice
and leaves the other one out. A swear the second speaker says is never in the
transcript, so nothing downstream can find or cut it. Even when it is found,
DTW word timestamps under crosstalk are loose (0.1–0.3 s, one measured at
10 ms long), so part of the word stays audible.

The priority is the F-word family. The full Bad Words List must still be
filtered exactly as today.

## Evidence

`tests/fixtures/crosstalk/` (from `tests/fixtures/generate-crosstalk-fixtures.ps1`)
holds 35 two-speaker clips with 33 planted swears at exact times: edge
overlaps (`ct_edge_*`, the next speaker starts 0.3–1.0 s early), asides fully
under another sentence (`ct_sweep_*`, at 0/-3/-6 dB), tails (`ct_tail_*`),
and clean overlaps (`ct_clean_*`, for false positives). Score it with:

```bash
Transcription__Model=large-v3-turbo Transcription__ModelDirectory=F:/SourceCode/FoulFilterNet/models \
  dotnet run -c Release --project src/FoulFilterNet.Evaluation -- \
  --manifest tests/fixtures/crosstalk/manifest.json --bad-words <list> --innocent none.mp3
```

Measured 2026-09-22 (raw-word detection, CUDA, all 35 clips):

| Configuration | Edge | Sweep | Total /33 | Time |
|---|---|---|---|---|
| base, 28 s windows | 4/9 | 0/12 | 11 | 11.6 s |
| turbo, 28 s windows | 9/9 | 0/12 | 18 | 19.6 s |
| turbo + prompt, 28 s | 9/9 | 0/12 | 20 | 19.3 s |
| turbo, beam search 5 | – | – | worse (4/8 on the first 8) | – |
| turbo, 5 s windows | 9/9 | 0/12 | 16 | 35.5 s |
| **turbo, 5 s windows + prompt** | 9/9 | **9/12** | **29** | 35.0 s |
| base, 5 s windows + prompt | 5/9 | 0/12 | 12 | 35.4 s |

The prompt was the bare list `fuck, fucking, fucked, motherfucker`; a
natural sentence did worse. The prompted short-window pass invented no
swears on the clean clips and changed nothing on the original fixture set,
**but it loses ordinary words** (a whole line on `ct_clean_0`), so it cannot
replace the primary pass. Unioning only its F-words into the turbo primary
pass scores 29/33 (simulated).

## Design

### The Priority Word List

A second, short list: the words a secondary pass hunts for. Default built in
(`fuck`, `fucks`, `fucking`, `fuckin`, `fucked`, `fucker`, `motherfucker`),
overridable by `priority_words.txt` in the data directory (same format as
`bad_words.txt`) or `Transcription:PriorityWordsPath`. An empty list, or
`Transcription:PriorityPass=false`, turns the pass off. The prompt is the list
joined with `", "`.

The primary pass and the Bad Words List are unchanged: everything on the full
list is still found in the primary transcript.

### The Priority Word Pass lives inside a window

`IAnalysisAudio.TranscribeWindowAsync(index)` returns what window `index`
heard. With the pass on, that result is the primary hearing **plus** priority
words found by re-hearing the same window in short prompted sub-windows:

1. The primary processor hears the 28 s window as today.
2. A second processor, built with `.WithPrompt(list)`, hears the window's audio
   in sub-windows of 5 s stepping 2.5 s (arithmetic in a pure, tested type,
   like `TranscriptionWindows`), each padded with silence the way windows are
   today (see `9e8084f`).
3. From the sub-window results, keep only words whose normalized token is on
   the Priority Word List, each kept by exactly one sub-window (midpoint
   share, as `Stitch` does).
4. Drop any that duplicate a primary word (same normalized token, overlapping
   in time); add the rest to the window's words, in time order. Segments are
   not touched.

Because the unit is still the window, the Watch Session, its scheduler and
Coverage, the head (W17), the batch pipeline and the Rescan Pass all get the
extra words with no change of their own. Both processors take the one
`InferenceLane` turn per sub-inference (or the window's turn covers all of
them - whichever keeps Watch priority honest; decide and document).

The Transcript cache stores the extra words. Bump `Transcript.CurrentVersion`
so transcripts made without the pass are not resumed as if they had it.

### Priority padding

Hits on priority words get wider cuts than `HitPadding.Default` (0.15/0.25),
plus a minimum hit length, because crosstalk timestamps are loose. Tune both
against the crosstalk manifest's "covered" column without letting clean
fixtures' cut totals balloon. Applies to Watch and batch alike.

As built (X03): Domain `CutPadding` decides each Hit's window. A Hit whose
phrase normalizes to one token on the Priority Word List (a merged `a+b`
window counts if any part does) is first **grown backward from its reported
end to 0.8 s**, then padded **0.25 s before, 0.5 s after**; every other Hit
keeps `HitPadding.Default`, so ordinary filtergraphs are unchanged. Backward,
because a word masked by the other speaker collapses to a 10 ms DTW point at
or up to 0.47 s *after* its true end; the 0.5 s post-roll because priority
words that kept a length ended up to 0.44 s early. The same rule applies to
F-words the primary pass found (a Word carries no provenance, and they are
just as loose under crosstalk).

- `HitMerger(CutPadding)` pads; `HitReconciler(CutPadding)` tolerates, per
  Candidate phrase, that phrase's pre+post (0.75 s for a priority word), which
  is still the gap at which the merger fuses two windows.
- Batch: `MediaPipeline` takes an optional `CutPadding` (also used for Smart
  Cut adjustments). Watch: `WatchProgress` carries it; sessions get it via
  `WatchSessionServices`/`WatchSessionManager`/`AddWatch`. Web, CLI and
  Evaluation register one `PriorityWordSource` (the engine's list) and
  `CutPadding.ForPriorityWords(list)`; an empty list is `CutPadding.Default`.
- Watch's Coverage guard becomes `max(1 s, 0.8 + 0.25 s)` = 1.05 s, since a
  10 ms priority Hit just inside an unfinished neighbour reaches that far back.
- The evaluator scores raw words with the job's `CutPadding`
  (`ScoringRules.RawWordsFor`) and reports censored seconds per level.
- Constants, not configuration: `CutPadding.DefaultPriorityPadding` and
  `DefaultPriorityMinimumSeconds`. **Superseded by X05** - see below.

### As built (X05): the tuning is configuration

The five numbers X02 and X03 measured are bound from the `Transcription`
section, with the measured values as their defaults, so nothing changes out of
the box:

| Key | Default | What it is |
|---|---|---|
| `Transcription:PriorityPaddingPre` | 0.25 s | Pre-roll on a priority Hit |
| `Transcription:PriorityPaddingPost` | 0.5 s | Post-roll on a priority Hit |
| `Transcription:PriorityMinimumCutSeconds` | 0.8 s | How short a priority Hit may be before it is grown backward |
| `Transcription:PrioritySubWindowSeconds` | 5.0 s | One sub-window of the pass |
| `Transcription:PrioritySubWindowStepSeconds` | 2.5 s | Between sub-window starts |

- `PrioritySubWindows` (Transcription) owns the sub-window arithmetic that was
  `PriorityWindows`' two constants and its `Plan`; `PriorityTuning` reads the
  five keys and builds both halves - `PaddingFor(list)` for the `CutPadding`
  every host registers, and `SubWindows` for the layout `WhisperNetEngine`
  hands `PriorityPass`.
- **Zero, or omitted, means the shipped default.** A key left out of
  configuration binds as zero and so does one written as `0`, and there is no
  telling them apart; zero padding is what `Transcription:PriorityPass=false`
  is for. Anything that cannot work - negative, NaN, infinite, or a step longer
  than a sub-window - throws with the key named, at startup in Web and the CLI
  and when the pipeline is composed in the evaluator.
- **Watch's Coverage guard already followed the padding** (X03's
  `WatchProgress.GuardSecondsFor`), so it follows the configured padding with
  no further change: `max(1 s, minimum + pre)`, 1.05 s by default. X05's tests
  hold it to that by moving the padding and watching the guard move.
- Non-priority `HitPadding.Default` (0.15/0.25) stays a constant.

## Tasks

| Task | Scope |
|---|---|
| X01 | Priority Word List (domain type, default, file loading, config keys) and the pure sub-window arithmetic: plan sub-windows for a window, keep/dedupe/merge priority words. Unit tests only. |
| X02 | Wire the pass into the engine: prompted second processor, sub-window inference through the lane, merged window result, config binding in Web/CLI/Evaluation, cache version bump. Measure with the crosstalk manifest (expect ~29/33) and the original manifest (no regression). |
| X03 | Priority padding: wider padding and minimum length for priority-word hits in both batch and Watch. Tune on the crosstalk manifest's coverage; record before/after. |
| X04 | Throughput and docs: measure Watch real-time factor with the pass on (turbo, CUDA) and make sure a first viewing still keeps ahead of the playhead; README, ADR-0008, STATUS. |
| X05 | The padding and sub-window values as configuration, bound from `Transcription` with the measured values as defaults, validated and documented. See "As built (X05)" above. |

As built (X04): a window costs 2.83 s instead of 0.69 s (4.1×, down from 6.4×
once sub-windows that cannot contribute were dropped and the language stopped
being detected per sub-window), which is 7.5× real time; a first view waits
8.4 s instead of 3.8 s where no fetch is involved. Watch and a batch Job at once
peak at 4.2 GB of VRAM on the 12 GB card. The numbers, and the options that were
tried and rejected, are in [ADR-0008](adr/0008-crosstalk-priority-word-pass.md).

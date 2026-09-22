# ADR-0008: Crosstalk is caught by a prompted pass over short sub-windows

Status: **Accepted** (X01–X04, 2026-09-22/23). Builds on
[ADR-0006](0006-whisper-net-collapses-transcription-and-alignment.md) (one
whisper.cpp pass per 28 s window, DTW word timestamps) and applies to web video
as well ([ADR-0007](0007-web-video-two-streams.md)).

## Context

When two people talk over each other, Whisper transcribes the dominant voice and
leaves the other one out. A swear the second speaker says is never in the
transcript, so nothing downstream can find or cut it — the tool is silently
wrong rather than visibly failing. Even when such a word *is* transcribed, its
DTW timestamps are loose (0.1–0.3 s out, one measured at 10 ms long), so part of
the word stays audible through the ordinary 0.15 s / 0.25 s padding.

The priority is the F-word family; the full Bad Words List must go on being
filtered exactly as before. The design and the task breakdown are in
[05-crosstalk-plan.md](../05-crosstalk-plan.md).

### Evidence

`tests/fixtures/crosstalk/` (35 generated two-speaker clips, 33 planted swears
at exact times: edge overlaps, asides fully under another sentence at 0/-3/-6 dB,
tails, and clean overlaps for false positives) scored with
`FoulFilterNet.Evaluation`, on CUDA, against the user's own Bad Words List.
Raw-word detection, measured 2026-09-22:

| Configuration | Edge | Sweep | Total /33 | Time |
|---|---|---|---|---|
| base, 28 s windows | 4/9 | 0/12 | 11 | 11.6 s |
| turbo, 28 s windows | 9/9 | 0/12 | 18 | 19.6 s |
| turbo + prompt, 28 s windows | 9/9 | 0/12 | 20 | 19.3 s |
| turbo, beam search 5 | – | – | worse (4/8 on the first 8) | – |
| turbo, 5 s windows | 9/9 | 0/12 | 16 | 35.5 s |
| **turbo, 5 s windows + prompt** | 9/9 | **9/12** | **29** | 35.0 s |
| base, 5 s windows + prompt | 5/9 | 0/12 | 12 | 35.4 s |

The prompt that worked was the bare list `fuck, fucking, fucked, motherfucker`;
a natural sentence did worse. The prompted short-window pass invented no swears
on the clean clips and changed nothing on the original fixture set — **but it
loses ordinary words** (a whole line on `ct_clean_0`), so it cannot replace the
primary pass.

## Decision

A **Priority Word Pass** runs inside each transcription window, and priority
words are cut wider.

1. **A Priority Word List** — the F-word family built in (`fuck`, `fucks`,
   `fucking`, `fuckin`, `fucked`, `fucker`, `motherfucker`), overridable by
   `priority_words.txt` in the data directory or `Transcription:PriorityWordsPath`,
   switched off by an empty list or `Transcription:PriorityPass=false`. The
   prompt is that list joined with `", "`. It does not decide what is censored:
   the Bad Words List still does.
2. **The pass lives inside a window.** `WindowedAudio` hears the window with the
   primary processor, then hears the same samples again in 5 s sub-windows
   stepping 2.5 s with a second processor built `.WithPrompt(list)`, keeps only
   words on the list (each kept by exactly one sub-window, by midpoint share, as
   stitching does), drops those that duplicate a primary word, and merges the
   rest into the window's words. Because the unit is still the window, Watch,
   the batch pipeline, the Rescan Pass and Coverage need no change of their own.
3. **Every inference takes its own turn in the `InferenceLane`** — the primary
   hearing and each sub-window — so a waiting Watch window is delayed by at most
   one inference of a Job window, not by all twelve.
4. **Priority padding.** A Hit whose phrase normalizes to one token on the list
   (a merged `a+b` window counts if either part does) is grown backward from its
   reported end to 0.8 s, then padded 0.25 s before and 0.5 s after; everything
   else keeps 0.15 s / 0.25 s, so ordinary filtergraphs are byte-identical. The
   values come from the fixtures: masked words collapse to a 10 ms point up to
   0.47 s after the word's true end, and words that kept a length ended up to
   0.44 s early. Watch's Coverage guard becomes `max(1 s, 0.8 + 0.25)` = 1.05 s.
5. **`Transcript.CurrentVersion` is bumped to 2**, so a transcript made without
   the pass is not resumed as though it had it.

### What the pass gains, and what it costs

Detection and coverage on the crosstalk manifest (turbo, CUDA, the user's Bad
Words List; "covered" means the cut window contains the whole planted word):

| | Pass off | Pass on (X02) | With priority padding (X03, X04) |
|---|---|---|---|
| Detected | 18/33 | 29/33 | **29/33** |
| Covered | 3/33 | 10/33 | **27/33** (every detected F-word) |
| False positives (final hits) | 0 | 0 | **0** |
| Censored seconds | — | 22.7 s | 44.2 s |

The original fixture manifest is unchanged either way: 10/11 detected, 10/11
covered, one planted-innocent final hit, 10.583 s censored.

Throughput, measured on 2026-09-23 on the machine ADR-0007 describes (RTX
3080 Ti, `large-v3-turbo`, CUDA, Release) over a 10-minute speech-dense file
built from the fixtures (27 windows), and through the real HTTP API with the
`file` provider so no yt-dlp fetch is in the figures:

| | Pass off | Pass on |
|---|---|---|
| Per window, median (28 s heard, 22 s of new Coverage) | 0.69 s | **2.83 s** |
| Whole 10-minute file | 22.3 s | **90.0 s** |
| Speed against real time | 30–32× | **7.5×** |
| Time to 30 s of Coverage ahead (what the Playback Gate waits for) | 3.8 s | **8.4 s** |
| Crosstalk manifest, whole evaluator run | 21 s | 47 s |

VRAM (nvidia-smi, whole card, 1.8 GB of it the desktop): 3.6 GB with the model
resident and nothing open, 4.2 GB peak while transcribing — the same 4.2 GB peak
with a Watch Session and a batch Job open at once (four whisper states). So
about 2.4 GB for FoulFilterNet on a 12 GB card either way.

## Considered Options

- **Prompt the primary 28 s pass** (no second pass): 20/33. The prompt alone
  does not make Whisper hear the quieter speaker.
- **Beam search (5)**: worse than greedy on these clips, and slower.
- **Short windows for everything** (5 s + prompt as the only pass): 29/33 on
  swears, but it loses ordinary words and mishears others, so the full Bad Words
  List would suffer. Rejected: the primary pass must stay as it is.
- **A bigger model**: `base` scores 11–12 whatever else is done; turbo is what
  makes 29 possible. Turbo is now what Web's `appsettings.json` asks for.
- **Reducing whisper.cpp's audio context** for the 5 s sub-windows (X04, tried
  and measured): the encoder does less work, but decoding became erratic on this
  model — per-window time swung between 2.1 s and 8.4 s, worse on average than
  leaving it alone. Rejected.
- **Running the sub-windows only after the primary windows near the playhead**
  (deferring the pass to keep a first view fast): rejected because a window's
  Coverage would then be claimed before its priority words are known, and the
  viewer would hear exactly the words the pass exists to catch. Coverage means
  "final".

Two cheaper things *were* taken (X04), both without changing what is heard:

- **Sub-windows that cannot contribute are not heard.** A window keeps only
  words whose midpoint is in its share, so a sub-window whose own share lies
  wholly outside it can only produce words that stitching throws away: one of
  eleven in the middle of a file, more in a last window pulled back to end with
  it.
- **The sub-windows are heard in the language the primary pass heard the window
  in.** Detecting the language again costs whisper.cpp a second encoder pass per
  sub-window, and 28 s is the better sample to detect from anyway. Together
  these took a window from 4.44 s to 2.83 s (‑36 %) with the stitched
  transcript's priority words identical over the 10-minute file, and the
  manifests unchanged.

## Consequences

- **Transcription is about four times slower** wherever the pass is on, which is
  everywhere by default. A batch job of an audiobook takes four times as long;
  web video still outruns playback by 7×, and a first view costs about 4.5 s
  more. `Transcription:PriorityPass=false` buys the old speed back.
- **A second whisper state per open audio**, built lazily, disposed with the
  audio. VRAM stays comfortable on a 12 GB card, but a smaller card now holds
  two states per open audio, not one.
- **F-words are cut about 1.5 s wide instead of 0.8 s**, in files and in the
  browser alike. On clean material that is audibly more dead air around one
  word; it is the price of covering a word whose timestamps are a 10 ms point.
- **Cached transcripts from before the pass are re-transcribed once** (version 2).
- The pass is only as good as the list: a swear that is not a priority word and
  is spoken under another voice is still missed. Four of the 33 planted swears
  are still missed, all of them fully masked asides.
- Two uncovered detections on the crosstalk manifest are `bullshit` and `shit` —
  ordinary words, deliberately left on ordinary padding.

# ADR-0006: Whisper.net on CUDA collapses transcription and alignment

ADR-0001 split transcription from alignment for one reason: WhisperX's
transcription path depends on CTranslate2, which had no usable ROCm build for
the RDNA2 card the Python ran on, so the design transcribed with a Hugging Face
Whisper pipeline and recovered word boundaries with WhisperX's wav2vec2 forced
aligner. That split was a workaround for a platform problem, not a preference,
and the .NET port runs on an RTX 3080 Ti — where every constraint that shaped it
is gone.

The port therefore transcribes with **Whisper.net** (whisper.cpp bindings, with a
first-class CUDA runtime), which reports word-level timestamps itself. One pass
replaces two models on one card, and the second model, the second dependency and
`ALIGN_DEVICE` all disappear. `IAligner` survives as a seam — `PassThroughAligner`
hands the transcriber's words straight back — so a forced aligner can be
reintroduced without the pipeline changing. The Rescan Pass, the transcript cache
(ADR-0002) and `UNLOAD_MODELS_AFTER_JOB` are unchanged: they are properties of
the pipeline, not of the engine.

whisper.cpp has no word API. Each segment carries **sub-word tokens**, with a
word's first token carrying the leading space, so tokens are joined into
whitespace-delimited words before they become `Word` records. Each token offers
two different timestamps, and which one is used turned out to matter more than
anything else in this task, so both were measured.

## Measurement

The seven fixtures in `tests/fixtures/media` were synthesised one part at a time,
so every profanity's span in `manifest.json` is exact by construction — the
difference between a reported boundary and the manifest is real error, not
annotation noise. Each fixture was transcribed with `large-v3-turbo` through the
production path (`WhisperTranscriber` → `WhisperNetEngine` → `WhisperWords`),
matched with `PhraseMatcher.FindHits`, and each of the 11 planted profanities
paired with the nearest reported hit of the same phrase. The opt-in test
`LiveWhisperTranscriberTests` (`RUN_GPU_TESTS=1`) re-runs the same path.

Four joining rules over the same token stream, where *dtw* is the DTW instant
that appears when the model's alignment heads are configured:

| rule | \|Δstart\| mean / max | \|Δend\| mean / max | inside the padding |
|---|---|---|---|
| `t0 .. t1` (token-timestamp heuristic) | 0.310 / 0.790 s | 0.304 / 0.897 s | 5/11 starts, 7/11 ends |
| `dtw(first) .. dtw(last)` | 0.272 / 0.329 s | 0.066 / 0.197 s | 2/11 starts, 11/11 ends |
| **`dtw(previous) .. dtw(last)`** (chosen) | **0.107 / 0.242 s** | **0.069 / 0.207 s** | **10/11 starts, 11/11 ends** |
| `t0 .. dtw(last)` | 0.310 / 0.790 s | 0.069 / 0.207 s | 5/11 starts, 11/11 ends |

Detection recall was 11/11 under every rule, and the deliberate false positive
("hoe", a garden tool) was reported exactly once, which is Smart Cut's problem
rather than the engine's.

Two facts decided the rule. The DTW instants are roughly three times more
accurate than `t0`/`t1`, and they mark where a token **ended**, not where it
began — visible directly in the tokens (`' This' t0=3 t1=23 dtw=16`,
`' recording' t0=62 t1=115 dtw=112`) and confirmed by the middle row above, where
reading them as starts puts every word about 0.3 s late.

Direction matters more than magnitude, because an early start or a late end
over-censors while a late start or an early end leaves profanity audible. Under
the chosen rule the worst **late start is 0.070 s** against 0.15 s of pre-padding,
and the worst **early end is 0.207 s** against 0.25 s of post-padding: all 11
hits are fully covered, with 0.080 s and 0.043 s of margin in the worst cases.
Under `t0`/`t1` the audiobook's "damn" ended 0.897 s early, which the padding
would not have absorbed — 0.647 s of it would have stayed audible. **The answer
to "is whisper.cpp precise enough to replace the forced aligner?" is yes, but
only with the DTW pass and only with the instants read as token ends.**

whisper.cpp also reports a word with no length at all fairly often — 9 of the
fixtures' words came back with `t0 == t1`, among them "Long" and "horse". The
Python's aligner dropped such a word, where it meant alignment had failed; here
it means only that the heuristic was vague about a word that was certainly
spoken, and a dropped word cannot be matched against the Bad Words List at all.
They are kept with a minimal span, and the padding covers the difference.

**The numbers above were measured on the CPU.** The machine has an RTX 3080 Ti
and a CUDA 13.2 driver, but no CUDA toolkit runtime, so Whisper.net's loader fell
through its order to `Cpu` (about 28 s per 8 s fixture, and
`RuntimeOptions.LoadedLibrary` says so). whisper.cpp computes the same
timestamps on either backend, so the boundary results stand; the CUDA path's
*speed* is what remains unverified here, and it needs the CUDA 13 runtime
installed on the host.

*Addendum, 2026-09-17:* with the CUDA runtime installed the loader selects
`Cuda` on its own. The opt-in GPU tests then transcribe all seven fixtures,
model load included, in about 5.6 s, and the CLI's run on `single_hit.mp3`
reports the same boundaries as the CPU measurement to the millisecond.

*Addendum, 2026-09-17 (T34):* the evaluation harness (`foulfilter-eval`, see
the T34 output section of `docs/STATUS.md`) repeats this measurement
mechanically, and three corrections to the text above come out of it.

1. **The chosen row reproduces exactly on the CPU**: 0.107 / 0.242 s start,
   0.069 / 0.207 s end, 10/11 starts and 11/11 ends inside the padding, worst
   late start 0.070 s, worst early end 0.207 s, every span covered with 0.080 s
   and 0.043 s of margin.
2. **The backends are not identical.** On CUDA every figure matches except the
   mean start error, 0.105 s: the fourth "damn" of `repeated_hits.mp3` starts at
   6.700 s on CUDA against 6.680 s on the CPU (truth 6.790 s, so early and safe
   either way). The previous addendum's "to the millisecond" was checked on
   `single_hit.mp3` only, where it does hold. The conclusion is unchanged.
3. **"11 planted profanities" is ten profanities and one garden tool.** The
   eleven spans include `false_positive.mp3`'s "hoe", which the boundary figures
   rightly count (its timing is exact like the rest). But detection over all
   eleven is not the same as profanity recall (10/10), and the "hoe" hit is what
   makes precision 10/11.

The harness also scores the padded, merged windows the pipeline actually
censors, which this ADR did not. All eleven spans are covered there too. But
the audiobook produces one extra window, 4.570–5.283 s over "thought, a": the
segment-interpolated Candidate for "damn" lands 0.78 s before DTW's word, beyond
`HitReconciler`'s 0.40 s proximity tolerance, so it is censored at its estimate
as well as at the aligned word. That over-censors rather than leaking, and it
comes from reconciliation rather than from DTW's accuracy.

## Considered Options

- **Keep the Python hybrid as an ASR sidecar**: rejected — preserves exact
  behaviour but reintroduces the two-process topology ADR-0003 deliberately
  removed, and leaves the hardest dependency in place.
- **whisper.cpp token timestamps without the DTW pass**: rejected on the
  measurement — 0.9 s of boundary error escapes the hit padding, which is a
  censoring bug rather than an inaccuracy.
- **ONNX Runtime with an exported Whisper**: rejected — more plumbing and no
  word timestamps for free.
- **Reintroduce a wav2vec2 forced aligner behind `IAligner` now**: rejected as
  unnecessary; the seam is kept precisely so this stays available if a real
  corpus disagrees with the fixtures.

## Consequences

- Word boundaries come from whisper.cpp's DTW alignment rather than from forced
  alignment, and are looser — but measurably inside the 0.15 s / 0.25 s padding
  the hit merger already applies.
- DTW needs the model's own alignment heads, so the heads preset is derived from
  the configured model. A third-party model Whisper.net has no preset for falls
  back to `t0`/`t1`, where the first row of the table applies; such a deployment
  should expect boundaries that the padding does not always cover.
- `IAligner` stays in the design with `PassThroughAligner` behind it. If a real
  corpus shows worse error than these fixtures, a forced aligner drops in there
  and no pipeline code changes.
- Weights are acquired at runtime into a configured directory
  (`Transcription:ModelDirectory`), gitignored and never committed;
  `large-v3-turbo` is 1.6 GB.
- The measurement is repeatable rather than a one-off claim: the fixtures, the
  manifest and the opt-in test are all in the repository, and CI stays green
  without a GPU or weights because the test is skipped by default.
- The sample is small and clean — 11 hits, 5–13 s of single-voice English TTS.
  That is enough to reject the `t0`/`t1` rule outright, and not enough to put a
  confidence interval around 0.107 s. A longer real recording is the next
  measurement worth making (T34).
- **DTW truncates long input (found 2026-09-17).** With DTW on, Whisper.net 1.9.1
  returns only the first 30-second window of whatever it is given, silently: a
  52-minute video came back with 29 s of transcript. Every fixture is shorter
  than that, so neither this measurement nor T34 could see it. Turning DTW off
  transcribes the whole file but reproduced the first row of the table (5 of 11
  spans left partly audible after padding), so the engine keeps DTW and feeds
  whisper.cpp 28 s windows overlapping by 6 s (`TranscriptionWindows`), keeping
  each word from the window whose share its midpoint falls in. The fixture
  evaluation is unchanged by this, and the opt-in
  `HearsTheProfanityPastTheFirstThirtySeconds` test covers audio past 30 s.

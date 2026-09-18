// Routes a <video>'s sound through Web Audio so Live Censoring can close it
// over a word, or cover it with a bleep. Adapter, over an injected
// AudioContext factory; what to schedule is schedule.js.
//
// Per page, one AudioContext for the page's life, never closed (ADR-0007): a
// media element can be attached to a MediaElementSourceNode once in its life,
// in any context, so losing the context would leave the element unfilterable
// until the page reloads. The context is created on the first `attach`, which
// W15 should call from the `play` event (a suspended context means silence;
// `resume()` it there, or from the overlay's click).
//
// Per <video>, exactly one `createMediaElementSource`, remembered in a
// WeakMap (YouTube reuses the element across navigations, so the graph
// survives them). If the browser refuses with `InvalidStateError`, the
// element was attached before - by an earlier copy of this content script
// (the extension was reloaded or injected twice) or by another extension -
// and `attach` answers the W13 gate's `{kind: 'audio'}` error, which holds
// the video with "reload the page". That refusal is remembered too.
//
// The graph:
//
//   source -> programme gain -> destination
//   oscillator (1 kHz sine, one per page, started once) -> bleep gain -> destination
//
// Decided (W14):
// - The programme gain starts closed (0): nothing is heard until the
//   controller says what to do, which it does at once.
// - The bleep level is BLEEP_LEVEL, 0.125: FFmpeg's `sine` source at its
//   default amplitude (1/8), which is what FilterGraph.Bleep mixes into files.
// - Edges are short linear ramps (RAMP_SECONDS, 5 ms) to avoid clicks, placed
//   so the gain is fully closed over the whole planned span: a close ramps
//   down during the 5 ms *before* its time, an open ramps up during the 5 ms
//   *after* it. Both lie inside the service's padding (0.15 s before, 0.25 s
//   after). A close whose ramp would start in the past starts it at the
//   plan's `audioNow`. The bleep gain mirrors the programme gain exactly (a
//   crossfade). Changes "now" (`closedNow`, `muteNow`, `openNow`) are steps.
// - `apply` first cancels every scheduled change from the plan's `audioNow`,
//   then sets the value now, then schedules the plan: re-applying a plan is
//   always safe.
// - `muteNow` is silence, never a bleep (leaving a page, or a video W15
//   cannot vouch for).

/**
 * @typedef {import('./schedule.js').SchedulePlan} SchedulePlan
 *
 * @typedef {'silence' | 'bleep'} CensorMethod
 *
 * @typedef {object} AudioParamLike
 * @property {number} value
 * @property {(value: number, time: number) => unknown} setValueAtTime
 * @property {(value: number, time: number) => unknown} linearRampToValueAtTime
 * @property {(time: number) => unknown} cancelScheduledValues
 *
 * @typedef {object} AudioNodeLike
 * @property {(destination: unknown) => unknown} connect
 *
 * @typedef {AudioNodeLike & { gain: AudioParamLike }} GainNodeLike
 *
 * @typedef {AudioNodeLike & { type: string, frequency: { value: number }, start: (when?: number) => void }} OscillatorLike
 *
 * @typedef {object} AudioContextLike  The parts of an AudioContext used here.
 * @property {number} currentTime
 * @property {string} state
 * @property {unknown} destination
 * @property {(element: unknown) => AudioNodeLike} createMediaElementSource
 * @property {() => GainNodeLike} createGain
 * @property {() => OscillatorLike} createOscillator
 * @property {() => Promise<void>} resume
 *
 * @typedef {object} AudioError  The W13 gate's error for an element that cannot be filtered.
 * @property {'audio'} kind
 * @property {'already-connected' | 'no-context' | 'failed'} reason
 * @property {string} detail
 *
 * @typedef {object} AudioGraph
 * @property {(plan: SchedulePlan, method?: CensorMethod) => void} apply
 * @property {() => void} muteNow
 * @property {() => void} openNow
 * @property {() => Promise<string>} resume  Resumes the context; resolves to its state, never rejects.
 * @property {number} currentTime  The context's clock, audio seconds.
 * @property {string} state  The context's state (`running`, `suspended`, ...).
 *
 * @typedef {{ ok: true, graph: AudioGraph } | { ok: false, error: AudioError }} AttachResult
 */

/** The bleep tone's frequency, Hz (as FilterGraph.Bleep). */
export const BLEEP_FREQUENCY = 1000;

/** The bleep tone's gain: FFmpeg's `sine` default amplitude, as FilterGraph.Bleep mixes it. */
export const BLEEP_LEVEL = 0.125;

/** Length of each edge's ramp, seconds. */
export const RAMP_SECONDS = 0.005;

/** @param {AudioError['reason']} reason @param {string} detail @returns {AudioError} */
function audioError(reason, detail) {
  return Object.freeze({ kind: 'audio', reason, detail });
}

const ALREADY_CONNECTED = audioError(
  'already-connected',
  'The video’s sound is already connected elsewhere (FoulFilter was reloaded, or another extension got there first). Reload the page to filter it.',
);

/**
 * The graphs of one page. `getPageAudio` keeps the page's one instance; this
 * factory is exported for tests.
 *
 * @param {object} options
 * @param {() => AudioContextLike} options.createContext  E.g. `() => new AudioContext()`.
 */
export function createAudioGraphs({ createContext }) {
  /** @type {AudioContextLike | null} */
  let context = null;
  /** @type {GainNodeLike | null} */
  let toneSplit = null;
  /** @type {WeakMap<object, AttachResult>} */
  const attached = new WeakMap();

  /** The context and the running tone, made on first use. */
  function ensureContext() {
    if (context === null) {
      const made = createContext();
      const oscillator = made.createOscillator();
      oscillator.type = 'sine';
      oscillator.frequency.value = BLEEP_FREQUENCY;
      // A unit gain every video's bleep gain hangs off, so the one oscillator serves them all.
      const split = made.createGain();
      split.gain.value = 1;
      oscillator.connect(split);
      oscillator.start();
      context = made;
      toneSplit = split;
    }
    return /** @type {AudioContextLike} */ (context);
  }

  /**
   * @param {AudioContextLike} ctx
   * @param {AudioNodeLike} source
   * @returns {AudioGraph}
   */
  function buildGraph(ctx, source) {
    const programme = ctx.createGain();
    programme.gain.value = 0;
    source.connect(programme);
    programme.connect(ctx.destination);

    const bleep = ctx.createGain();
    bleep.gain.value = 0;
    /** @type {GainNodeLike} */ (toneSplit).connect(bleep);
    bleep.connect(ctx.destination);

    const p = programme.gain;
    const b = bleep.gain;

    /** @param {number} time @param {number} programmeValue @param {number} bleepValue */
    function setNow(time, programmeValue, bleepValue) {
      p.cancelScheduledValues(time);
      b.cancelScheduledValues(time);
      p.setValueAtTime(programmeValue, time);
      b.setValueAtTime(bleepValue, time);
    }

    return Object.freeze({
      apply(plan, method = 'silence') {
        const level = method === 'bleep' ? BLEEP_LEVEL : 0;
        const now = plan.audioNow;
        setNow(now, plan.closedNow ? 0 : 1, plan.closedNow ? level : 0);
        for (const event of plan.events) {
          if (event.gain === 0) {
            const from = Math.max(now, event.time - RAMP_SECONDS);
            p.setValueAtTime(1, from);
            p.linearRampToValueAtTime(0, event.time);
            b.setValueAtTime(0, from);
            b.linearRampToValueAtTime(level, event.time);
          } else {
            p.setValueAtTime(0, event.time);
            p.linearRampToValueAtTime(1, event.time + RAMP_SECONDS);
            b.setValueAtTime(level, event.time);
            b.linearRampToValueAtTime(0, event.time + RAMP_SECONDS);
          }
        }
      },
      muteNow() {
        setNow(ctx.currentTime, 0, 0);
      },
      openNow() {
        setNow(ctx.currentTime, 1, 0);
      },
      async resume() {
        try {
          await ctx.resume();
        } catch {
          // Refused without a user gesture: the state says so.
        }
        return ctx.state;
      },
      get currentTime() {
        return ctx.currentTime;
      },
      get state() {
        return ctx.state;
      },
    });
  }

  return {
    /**
     * Route a <video> through the graph, once per element.
     *
     * @param {object} video
     * @returns {AttachResult}
     */
    attach(video) {
      const known = attached.get(video);
      if (known) {
        return known;
      }
      let ctx;
      try {
        ctx = ensureContext();
      } catch (error) {
        return { ok: false, error: audioError('no-context', `Web Audio is not available: ${messageOf(error)}`) };
      }
      /** @type {AttachResult} */
      let result;
      try {
        const source = ctx.createMediaElementSource(video);
        result = Object.freeze({ ok: true, graph: buildGraph(ctx, source) });
      } catch (error) {
        if (/** @type {any} */ (error)?.name !== 'InvalidStateError') {
          return { ok: false, error: audioError('failed', `Web Audio refused the video: ${messageOf(error)}`) };
        }
        result = Object.freeze({ ok: false, error: ALREADY_CONNECTED });
      }
      attached.set(video, result);
      return result;
    },

    /** The page's context, or null before the first attach. */
    get context() {
      return context;
    },
  };
}

/** @param {unknown} error */
function messageOf(error) {
  return /** @type {any} */ (error)?.message ?? String(error);
}

/** @type {ReturnType<typeof createAudioGraphs> | null} */
let pageAudio = null;

/**
 * The page's one set of graphs (and so its one AudioContext), made on the
 * first call; later calls ignore `createContext`.
 *
 * @param {() => AudioContextLike} [createContext]
 */
export function getPageAudio(createContext = () => /** @type {any} */ (new AudioContext())) {
  pageAudio ??= createAudioGraphs({ createContext });
  return pageAudio;
}

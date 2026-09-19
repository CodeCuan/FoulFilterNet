// The harness's listening post (W16): an AudioContext for audio-graph.js
// whose `destination` is a tap, so the self-check can measure exactly what
// the extension sends to the speakers. Adapter over an injected AudioContext.
//
//   <video> -> MediaElementSource ─┬─> (audio-graph.js: programme gain) ─┐
//                                  └─> raw analyser                      ├─> tap -> real destination
//   (audio-graph.js: 1 kHz oscillator -> bleep gain) ────────────────────┘    ├─> out analyser
//                                                                            ├─> 1 kHz notch -> programme analyser
//                                                                            └─> 1 kHz band-pass -> tone analyser
//
// audio-graph.js is used unchanged: it is handed `context` below, which
// forwards everything to the real context except `destination` (the tap) and
// `createMediaElementSource` (which also feeds the raw analyser). Every
// analyser feeds a silent gain into the real destination, so the browser
// keeps pulling audio through it.

import { rmsDb, sampleTime } from './self-check.js';

/** Analyser block size: about 21 ms at 48 kHz. */
export const FFT_SIZE = 1024;

/** The bleep's frequency (audio-graph.js BLEEP_FREQUENCY). */
export const TONE_HZ = 1000;

/** Notch width: Q 5 at 1 kHz removes about 200 Hz around the bleep. */
export const NOTCH_Q = 5;

/** Band-pass width for the tone: Q 20 at 1 kHz passes about 50 Hz. */
export const BAND_Q = 20;

/**
 * @param {() => AudioContext} createContext  E.g. `() => new AudioContext()`.
 */
export function createTap(createContext) {
  const real = createContext();
  const sink = real.createGain();
  sink.gain.value = 0;
  sink.connect(real.destination);

  const analyser = () => {
    const a = real.createAnalyser();
    a.fftSize = FFT_SIZE;
    a.smoothingTimeConstant = 0;
    a.connect(sink);
    return a;
  };

  const tap = real.createGain();
  tap.gain.value = 1;
  tap.connect(real.destination);

  const raw = analyser();
  const out = analyser();
  tap.connect(out);

  const notch = real.createBiquadFilter();
  notch.type = 'notch';
  notch.frequency.value = TONE_HZ;
  notch.Q.value = NOTCH_Q;
  tap.connect(notch);
  const programme = analyser();
  notch.connect(programme);

  const band = real.createBiquadFilter();
  band.type = 'bandpass';
  band.frequency.value = TONE_HZ;
  band.Q.value = BAND_Q;
  tap.connect(band);
  const tone = analyser();
  band.connect(tone);

  /** The context audio-graph.js is given. */
  const context = {
    get currentTime() {
      return real.currentTime;
    },
    get state() {
      return real.state;
    },
    destination: tap,
    /** @param {HTMLMediaElement} element */
    createMediaElementSource(element) {
      const source = real.createMediaElementSource(element);
      source.connect(raw);
      return source;
    },
    createGain: () => real.createGain(),
    createOscillator: () => real.createOscillator(),
    resume: () => real.resume(),
  };

  const buffer = new Float32Array(FFT_SIZE);
  /** @param {AnalyserNode} node */
  const level = (node) => {
    node.getFloatTimeDomainData(buffer);
    return rmsDb(buffer);
  };

  return {
    context,
    real,
    analysers: { raw, out, programme, tone },
    /** Seconds of audio one analyser block covers. */
    get windowSeconds() {
      return FFT_SIZE / (real.sampleRate || 48000);
    },
    /**
     * One sample of all four levels, stamped with the media time the block
     * describes.
     *
     * @param {{ currentTime: number, playbackRate: number }} video
     */
    read(video) {
      return {
        t: sampleTime(video.currentTime, FFT_SIZE / (real.sampleRate || 48000), video.playbackRate),
        raw: level(raw),
        out: level(out),
        programme: level(programme),
        tone: level(tone),
      };
    },
  };
}

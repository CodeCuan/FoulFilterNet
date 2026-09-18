// The Playback Gate's overlay: a panel over the player saying why the video
// is held, with a "Watch unfiltered" button. Adapter over an injected
// document; what it says comes from gate.js's `gateOverlay` (the model).
//
// - It lives in a host element appended to `#movie_player`, positioned over
//   the whole player. The host lets pointer events through (clicks on the
//   picture still reach YouTube); only the button takes them.
// - Its content is in a closed shadow root, so YouTube's stylesheets cannot
//   reach in and ours cannot leak out. Styles are set through the CSSOM
//   (`style.setProperty`), not a <style> element or a style attribute, which
//   a page's Content-Security-Policy could refuse. `all: initial` stops
//   inherited properties (font, colour) coming through the shadow boundary.
// - The button's click is stopped from bubbling to the player, which would
//   otherwise take it as a click on the video (play/pause).
// - `render(model, player)` moves the host into `player` when the player
//   changed (YouTube can replace it) and takes it out when `player` is null.
// - W15 adds a second button, "Enable sound" (`model.showEnable`), for a
//   suspended AudioContext: its click is the user gesture Chrome's autoplay
//   policy wants before `resume()` may succeed. Hidden unless asked.

/**
 * @typedef {import('./gate.js').OverlayModel} OverlayModel
 *
 * @typedef {object} StyledElement
 * @property {{ setProperty(name: string, value: string, priority?: string): void }} style
 */

/** The host element's id, for finding it in the page while debugging. */
export const OVERLAY_HOST_ID = 'foulfilter-gate-overlay';

export const UNFILTERED_LABEL = 'Watch unfiltered';

export const ENABLE_LABEL = 'Enable sound';

/**
 * @param {StyledElement} element
 * @param {Record<string, string>} properties
 */
function styleOf(element, properties) {
  for (const [name, value] of Object.entries(properties)) {
    element.style.setProperty(name, value, 'important');
  }
}

/**
 * @param {object} options
 * @param {Document} options.document
 * @param {() => void} options.onUnfiltered  The user clicked "Watch unfiltered".
 * @param {() => void} [options.onEnable]  The user clicked "Enable sound".
 */
export function createOverlay({ document, onUnfiltered, onEnable }) {
  const host = document.createElement('div');
  host.id = OVERLAY_HOST_ID;
  styleOf(host, {
    position: 'absolute',
    inset: '0',
    'z-index': '2147483000',
    'pointer-events': 'none',
    display: 'none',
  });

  const root = host.attachShadow({ mode: 'closed' });

  const panel = document.createElement('div');
  styleOf(panel, {
    all: 'initial',
    position: 'absolute',
    left: '50%',
    top: '50%',
    transform: 'translate(-50%, -50%)',
    'max-width': '80%',
    padding: '16px 20px',
    'border-radius': '8px',
    background: 'rgba(0, 0, 0, 0.8)',
    color: '#fff',
    'font-family': 'Roboto, Arial, sans-serif',
    'font-size': '15px',
    'line-height': '1.4',
    'text-align': 'center',
    'pointer-events': 'none',
  });

  const title = document.createElement('div');
  styleOf(title, { 'font-weight': 'bold', 'font-size': '17px' });
  const titleText = document.createElement('span');
  const percent = document.createElement('span');
  title.append(titleText, percent);

  const detail = document.createElement('div');
  styleOf(detail, { 'margin-top': '6px' });

  /** @param {string} label */
  function makeButton(label) {
    const made = document.createElement('button');
    made.type = 'button';
    made.textContent = label;
    styleOf(made, {
      'margin-top': '12px',
      'margin-left': '4px',
      'margin-right': '4px',
      padding: '6px 14px',
      border: '1px solid #fff',
      'border-radius': '4px',
      background: 'transparent',
      color: '#fff',
      font: 'inherit',
      cursor: 'pointer',
      'pointer-events': 'auto',
    });
    return made;
  }

  const button = makeButton(UNFILTERED_LABEL);
  const enableButton = makeButton(ENABLE_LABEL);
  enableButton.hidden = true;

  /** @param {Event} event */
  function onClick(event) {
    event.stopPropagation();
    event.preventDefault();
    onUnfiltered();
  }
  /** @param {Event} event */
  function onEnableClick(event) {
    event.stopPropagation();
    event.preventDefault();
    onEnable?.();
  }
  button.addEventListener('click', onClick);
  enableButton.addEventListener('click', onEnableClick);

  panel.append(title, detail, button, enableButton);
  root.append(panel);

  let destroyed = false;

  return {
    /** The host element (for tests and debugging). */
    host,

    /**
     * Show `model` over `player`, or hide it.
     *
     * @param {OverlayModel} model
     * @param {Element | null} player  `#movie_player`; null takes the overlay out of the page.
     */
    render(model, player) {
      if (destroyed) return;
      if (player == null) {
        host.remove();
        return;
      }
      if (host.parentNode !== player) {
        player.append(host);
      }
      if (!model?.visible) {
        host.style.setProperty('display', 'none', 'important');
        return;
      }
      titleText.textContent = model.title;
      percent.textContent = typeof model.percent === 'number' ? ` ${model.percent}%` : '';
      detail.textContent = model.detail;
      button.hidden = !model.showUnfiltered;
      enableButton.hidden = !(/** @type {any} */ (model).showEnable === true);
      host.style.setProperty('display', 'block', 'important');
    },

    /** Take the overlay out of the page for good. Idempotent. */
    destroy() {
      if (destroyed) return;
      destroyed = true;
      button.removeEventListener('click', onClick);
      enableButton.removeEventListener('click', onEnableClick);
      host.remove();
    },
  };
}

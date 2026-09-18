// overlay.js against a tiny fake document: elements are EventTargets with
// children, textContent, a style that records setProperty, and a shadow root.
// Just enough to drive the adapter; not a DOM.

import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { createOverlay, OVERLAY_HOST_ID, UNFILTERED_LABEL } from '../src/overlay.js';
import { HIDDEN_OVERLAY } from '../src/gate.js';

class FakeStyle {
  values = new Map();
  setProperty(name, value, priority) {
    this.values.set(name, { value, priority });
  }
  get(name) {
    return this.values.get(name)?.value;
  }
}

class FakeNode extends EventTarget {
  children = [];
  parentNode = null;
  append(...nodes) {
    for (const node of nodes) {
      node.parentNode?.children.splice(node.parentNode.children.indexOf(node), 1);
      node.parentNode = this;
      this.children.push(node);
    }
  }
  /** Every descendant, depth first, crossing into shadow roots. */
  *walk() {
    for (const child of this.children) {
      yield child;
      yield* child.walk();
      if (child.shadow) yield* child.shadow.walk();
    }
  }
}

class FakeElement extends FakeNode {
  id = '';
  type = '';
  hidden = false;
  textContent = '';
  style = new FakeStyle();
  shadow = null;
  shadowMode = null;

  constructor(tagName) {
    super();
    this.tagName = tagName.toUpperCase();
  }

  attachShadow({ mode }) {
    this.shadowMode = mode;
    this.shadow = new FakeNode();
    return this.shadow;
  }

  remove() {
    if (!this.parentNode) return;
    this.parentNode.children.splice(this.parentNode.children.indexOf(this), 1);
    this.parentNode = null;
  }
}

const fakeDocument = { createElement: (tag) => new FakeElement(tag) };

const MODEL = Object.freeze({
  visible: true,
  title: 'FoulFilter is preparing this video',
  detail: 'Listening ahead of you.',
  percent: 42,
  showUnfiltered: true,
});

function setup() {
  const clicks = { count: 0 };
  const overlay = createOverlay({ document: fakeDocument, onUnfiltered: () => clicks.count++ });
  const player = new FakeElement('div');
  const shadow = overlay.host.shadow;
  const all = () => [...shadow.walk()];
  const button = () => all().find((e) => e.tagName === 'BUTTON');
  const texts = () => all().map((e) => e.textContent).filter((t) => t !== '');
  return { overlay, player, clicks, shadow, button, texts };
}

const shownDisplay = (overlay) => overlay.host.style.get('display');

describe('overlay: construction', () => {
  it('is not in the page until rendered', () => {
    assert.equal(setup().overlay.host.parentNode, null);
  });

  it('has a findable id', () => {
    assert.equal(setup().overlay.host.id, OVERLAY_HOST_ID);
  });

  it('uses a closed shadow root', () => {
    assert.equal(setup().overlay.host.shadowMode, 'closed');
  });

  it('covers the player', () => {
    const { host } = setup().overlay;
    assert.deepEqual([host.style.get('position'), host.style.get('inset')], ['absolute', '0']);
  });

  it('lets pointer events through the host', () => {
    assert.equal(setup().overlay.host.style.get('pointer-events'), 'none');
  });

  it('takes pointer events on the button', () => {
    assert.equal(setup().button().style.get('pointer-events'), 'auto');
  });

  it('lets pointer events through every other element', () => {
    const s = setup();
    const others = [...s.shadow.walk()].filter((e) => e.tagName !== 'BUTTON' && e.style.get('pointer-events'));
    assert.ok(others.every((e) => e.style.get('pointer-events') === 'none'));
  });

  it('marks its styles important so the page cannot override them', () => {
    assert.equal(setup().overlay.host.style.values.get('position').priority, 'important');
  });

  it('labels the button Watch unfiltered', () => {
    assert.equal(setup().button().textContent, UNFILTERED_LABEL);
  });

  it('makes the button a plain button (not submit)', () => {
    assert.equal(setup().button().type, 'button');
  });

  it('is hidden until given a visible model', () => {
    assert.equal(shownDisplay(setup().overlay), 'none');
  });
});

describe('overlay: render', () => {
  it('puts the host into the player', () => {
    const { overlay, player } = setup();
    overlay.render(MODEL, player);
    assert.equal(overlay.host.parentNode, player);
  });

  it('shows the host for a visible model', () => {
    const { overlay, player } = setup();
    overlay.render(MODEL, player);
    assert.equal(shownDisplay(overlay), 'block');
  });

  it('shows the title', () => {
    const { overlay, player, texts } = setup();
    overlay.render(MODEL, player);
    assert.ok(texts().includes(MODEL.title));
  });

  it('shows the percent after the title', () => {
    const { overlay, player, texts } = setup();
    overlay.render(MODEL, player);
    assert.ok(texts().includes(' 42%'));
  });

  it('shows no percent when there is none', () => {
    const { overlay, player, texts } = setup();
    overlay.render({ ...MODEL, percent: null }, player);
    assert.ok(!texts().some((t) => t.includes('%')));
  });

  it('shows 0%', () => {
    const { overlay, player, texts } = setup();
    overlay.render({ ...MODEL, percent: 0 }, player);
    assert.ok(texts().includes(' 0%'));
  });

  it('shows the detail', () => {
    const { overlay, player, texts } = setup();
    overlay.render(MODEL, player);
    assert.ok(texts().includes(MODEL.detail));
  });

  it('shows the button when asked', () => {
    const { overlay, player, button } = setup();
    overlay.render(MODEL, player);
    assert.equal(button().hidden, false);
  });

  it('hides the button when not asked', () => {
    const { overlay, player, button } = setup();
    overlay.render({ ...MODEL, showUnfiltered: false }, player);
    assert.equal(button().hidden, true);
  });

  it('hides the host for a hidden model', () => {
    const { overlay, player } = setup();
    overlay.render(MODEL, player);
    overlay.render(HIDDEN_OVERLAY, player);
    assert.equal(shownDisplay(overlay), 'none');
  });

  it('keeps the host in the player while hidden', () => {
    const { overlay, player } = setup();
    overlay.render(HIDDEN_OVERLAY, player);
    assert.equal(overlay.host.parentNode, player);
  });

  it('hides the host for no model', () => {
    const { overlay, player } = setup();
    overlay.render(MODEL, player);
    overlay.render(null, player);
    assert.equal(shownDisplay(overlay), 'none');
  });

  it('replaces the text on a second render', () => {
    const { overlay, player, texts } = setup();
    overlay.render(MODEL, player);
    overlay.render({ ...MODEL, title: 'FoulFilterNet is not reachable' }, player);
    assert.ok(!texts().includes(MODEL.title));
  });

  it('does not append the host twice to the same player', () => {
    const { overlay, player } = setup();
    overlay.render(MODEL, player);
    overlay.render(MODEL, player);
    assert.equal(player.children.length, 1);
  });

  it('moves the host to a new player', () => {
    const { overlay, player } = setup();
    const next = new FakeElement('div');
    overlay.render(MODEL, player);
    overlay.render(MODEL, next);
    assert.deepEqual([player.children.length, overlay.host.parentNode], [0, next]);
  });

  it('takes the host out of the page for no player', () => {
    const { overlay, player } = setup();
    overlay.render(MODEL, player);
    overlay.render(MODEL, null);
    assert.equal(player.children.length, 0);
  });
});

describe('overlay: the button', () => {
  it('calls onUnfiltered on click', () => {
    const { overlay, player, button, clicks } = setup();
    overlay.render(MODEL, player);
    button().dispatchEvent(new Event('click', { cancelable: true }));
    assert.equal(clicks.count, 1);
  });

  it('stops the click reaching the player', () => {
    const { overlay, player, button } = setup();
    overlay.render(MODEL, player);
    const event = new Event('click', { cancelable: true });
    let stopped = false;
    event.stopPropagation = () => (stopped = true);
    button().dispatchEvent(event);
    assert.equal(stopped, true);
  });

  it('prevents the default action', () => {
    const { overlay, player, button } = setup();
    overlay.render(MODEL, player);
    const event = new Event('click', { cancelable: true });
    button().dispatchEvent(event);
    assert.equal(event.defaultPrevented, true);
  });
});

describe('overlay: destroy', () => {
  it('takes the host out of the page', () => {
    const { overlay, player } = setup();
    overlay.render(MODEL, player);
    overlay.destroy();
    assert.equal(player.children.length, 0);
  });

  it('stops listening to the button', () => {
    const { overlay, player, button, clicks } = setup();
    overlay.render(MODEL, player);
    overlay.destroy();
    button().dispatchEvent(new Event('click'));
    assert.equal(clicks.count, 0);
  });

  it('ignores renders afterwards', () => {
    const { overlay, player } = setup();
    overlay.destroy();
    overlay.render(MODEL, player);
    assert.equal(player.children.length, 0);
  });

  it('is idempotent', () => {
    const { overlay, player } = setup();
    overlay.render(MODEL, player);
    overlay.destroy();
    assert.doesNotThrow(() => overlay.destroy());
  });
});

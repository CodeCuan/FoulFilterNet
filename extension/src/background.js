// The service worker (an ES module). A thin adapter: it relays content-script
// and options-page messages to the local service and replies with the result.
// All the logic is in relay.js; nothing is kept between messages, because MV3
// stops an idle worker.

import { isFromExtension, isProtocolMessage } from './protocol.js';
import { createRelay } from './relay.js';
import { createSettingsStore } from './settings.js';

const store = createSettingsStore({ area: chrome.storage.sync, onChanged: chrome.storage.onChanged });

const relay = createRelay({
  loadSettings: store.load,
  // Wrapped so fetch is always called with the global as `this`.
  fetch: (input, init) => fetch(input, init),
});

// Registered synchronously at the top level, as MV3 requires, so a message
// that wakes the worker is not missed.
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (!isProtocolMessage(message) || !isFromExtension(sender, chrome.runtime.id)) {
    return false;
  }
  relay.handle(message).then(sendResponse);
  return true; // the reply is sent asynchronously
});

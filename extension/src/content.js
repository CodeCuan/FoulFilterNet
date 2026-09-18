// The content script's entry point, loaded as an ES module by
// content-loader.js. Deliberately empty until W12 (page watcher) and W15
// (wiring) give it work: it may import any module under src/, and talks to
// the service only through the service worker (protocol.js), never by fetch.

export {};

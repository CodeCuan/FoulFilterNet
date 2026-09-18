// The content script Chrome injects on YouTube. Manifest content scripts are
// classic scripts and cannot `import`, so this only loads the real entry point
// as an ES module. Every module it reaches must be listed in
// web_accessible_resources (manifest.json); see extension/README.md.
(() => {
  // Chrome injects this once per document, but a second copy (another world,
  // a programmatic injection) must not start a second filter.
  if (globalThis.__foulFilterLoader) {
    return;
  }
  globalThis.__foulFilterLoader = true;

  import(chrome.runtime.getURL('src/content.js')).catch((error) => {
    console.warn('FoulFilter could not start on this page:', error);
  });
})();

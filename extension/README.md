# FoulFilter extension

The Chrome (Manifest V3) half of web video filtering: it censors a YouTube
video's own audio as it plays, using Hits served by the local FoulFilterNet
service. The design is in
[docs/04-web-video-plan.md](../docs/04-web-video-plan.md) and
[ADR-0007](../docs/adr/0007-web-video-two-streams.md).

## Rules

- **Plain JavaScript ES modules.** No TypeScript, no bundler, no build step:
  the files in `src/` are what Chrome loads.
- **No npm dependencies.** `package.json` exists only to declare
  `"type": "module"` and the test script. Do not add `dependencies` or
  `devDependencies`.
- **Pure modules in `src/`, tested densely.** Anything that is a decision
  (video IDs, coverage, the Playback Gate, gain scheduling) is a pure module
  with no DOM or `chrome.*` access, and gets thorough tests in `test/` with
  `node:test` and `node:assert/strict`.
- **Adapters stay thin.** Code that touches the DOM, Web Audio or `chrome.*`
  only translates between the browser and the pure modules; it holds no logic
  worth testing on its own and is verified by hand.

## Testing

Node 22 or later:

```bash
cd extension
npm test
```

The script runs `node --test "test/**/*.test.js"`. Name test files
`*.test.js`. A bare directory argument (`node --test test/`) is not used: on
Windows, Node 22 treats it as a module path and fails.

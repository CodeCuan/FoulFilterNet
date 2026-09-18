// Which YouTube video a page is showing, from its URL. Pure.
//
// V1 filters the watch page only: `https://www.youtube.com/watch?v=<id>`, with
// any other query parameters (`&t=`, `&list=`, `&index=`, ...) and any
// fragment. Everything else is "no video" (null): shorts, embeds, youtu.be
// links, m.youtube.com, music.youtube.com, channel, home and search pages.
//
// Decided (W12):
// - https only. YouTube redirects http to https, and the content script only
//   runs on https pages, so an http URL can only come from a caller's mistake.
// - The host must be exactly www.youtube.com (the URL parser lower-cases it,
//   so `WWW.YouTube.com` counts); no port other than the default, no
//   credentials, no trailing dot.
// - The path must be exactly `/watch`: `/watch/` and `/watch/<id>` are not
//   watch pages in V1.
// - `v` is read after percent-decoding, as the page reads it. More than one
//   `v` counts only when every copy is the same ID; conflicting copies give
//   null rather than a guess, since filtering with another video's Hits is
//   worse than not filtering.
// - The ID rule is the server's (VideoRef, W03): exactly 11 of ASCII
//   `[A-Za-z0-9_-]`, case-sensitive, and a leading `-` is valid.

/** An ID as the server accepts it. */
const VIDEO_ID = /^[A-Za-z0-9_-]{11}$/;

/** The only host whose pages are filtered. */
export const WATCH_HOST = 'www.youtube.com';

/** The only path that is a watch page. */
export const WATCH_PATH = '/watch';

/**
 * Whether a string is a YouTube video ID: exactly 11 of `[A-Za-z0-9_-]`.
 *
 * @param {unknown} id
 * @returns {id is string}
 */
export function isVideoId(id) {
  return typeof id === 'string' && VIDEO_ID.test(id);
}

/**
 * The video ID of a watch page URL, or null for anything that is not one.
 * Never throws.
 *
 * @param {unknown} url  An absolute URL, e.g. `location.href`.
 * @returns {string | null}
 */
export function videoIdFromUrl(url) {
  if (typeof url !== 'string') {
    return null;
  }

  let parsed;
  try {
    parsed = new URL(url);
  } catch {
    return null;
  }

  if (
    parsed.protocol !== 'https:' ||
    parsed.hostname !== WATCH_HOST ||
    parsed.port !== '' ||
    parsed.username !== '' ||
    parsed.password !== '' ||
    parsed.pathname !== WATCH_PATH
  ) {
    return null;
  }

  const ids = parsed.searchParams.getAll('v');
  if (ids.length === 0 || ids.some((id) => id !== ids[0])) {
    return null;
  }
  return isVideoId(ids[0]) ? ids[0] : null;
}

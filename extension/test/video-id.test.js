import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { isVideoId, videoIdFromUrl, WATCH_HOST, WATCH_PATH } from '../src/video-id.js';

const ID = 'dQw4w9WgXcQ';
const watch = (query) => `https://www.youtube.com/watch?${query}`;

describe('isVideoId', () => {
  it('accepts 11 letters and digits', () => {
    assert.equal(isVideoId(ID), true);
  });

  it('accepts underscores and dashes', () => {
    assert.equal(isVideoId('a_b-c_d-e_f'), true);
  });

  it('accepts a leading dash (the server does too)', () => {
    assert.equal(isVideoId('-abcdefghij'), true);
  });

  it('accepts an ID made only of dashes', () => {
    assert.equal(isVideoId('-----------'), true);
  });

  it('refuses 10 characters', () => {
    assert.equal(isVideoId('dQw4w9WgXc'), false);
  });

  it('refuses 12 characters', () => {
    assert.equal(isVideoId('dQw4w9WgXcQQ'), false);
  });

  it('refuses the empty string', () => {
    assert.equal(isVideoId(''), false);
  });

  it('refuses a space', () => {
    assert.equal(isVideoId('dQw4w9 WgXc'), false);
  });

  it('refuses a dot', () => {
    assert.equal(isVideoId('dQw4w9.WgXc'), false);
  });

  it('refuses a non-ASCII letter', () => {
    assert.equal(isVideoId('dQw4w9WgXcé'), false);
  });

  it('refuses full-width digits', () => {
    assert.equal(isVideoId('dQw4w9WgXc１'), false);
  });

  it('refuses a trailing newline', () => {
    assert.equal(isVideoId(`${ID}\n`), false);
  });

  it('refuses a non-string', () => {
    assert.equal(isVideoId(12345678901), false);
  });

  it('refuses null', () => {
    assert.equal(isVideoId(null), false);
  });
});

describe('videoIdFromUrl: watch pages', () => {
  it('reads the id of a plain watch URL', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}`)), ID);
  });

  it('reads the id with a start time after it', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}&t=42s`)), ID);
  });

  it('reads the id with a start time before it', () => {
    assert.equal(videoIdFromUrl(watch(`t=42&v=${ID}`)), ID);
  });

  it('reads the id of a playlist entry', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}&list=PLabc123&index=3`)), ID);
  });

  it('reads the id with many unrelated parameters', () => {
    assert.equal(videoIdFromUrl(watch(`app=desktop&v=${ID}&pp=ygUEdGVzdA%3D%3D&ab_channel=X`)), ID);
  });

  it('ignores a fragment', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}#comments`)), ID);
  });

  it('ignores a fragment that looks like a v parameter', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com/watch#v=${ID}`), null);
  });

  it('keeps the id\'s case', () => {
    assert.equal(videoIdFromUrl(watch('v=ABCdefGHIjk')), 'ABCdefGHIjk');
  });

  it('accepts an id with a leading dash', () => {
    assert.equal(videoIdFromUrl(watch('v=-abcdefghij')), '-abcdefghij');
  });

  it('accepts an id with underscores', () => {
    assert.equal(videoIdFromUrl(watch('v=___________')), '___________');
  });

  it('accepts an upper-case host (the URL parser lower-cases it)', () => {
    assert.equal(videoIdFromUrl(`https://WWW.YouTube.COM/watch?v=${ID}`), ID);
  });

  it('accepts an upper-case scheme', () => {
    assert.equal(videoIdFromUrl(`HTTPS://www.youtube.com/watch?v=${ID}`), ID);
  });

  it('accepts the explicit default port', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com:443/watch?v=${ID}`), ID);
  });

  it('decodes a percent-encoded id', () => {
    assert.equal(videoIdFromUrl(watch('v=%64%51%77%34w9WgXcQ')), ID);
  });

  it('decodes a percent-encoded parameter name', () => {
    assert.equal(videoIdFromUrl(watch(`%76=${ID}`)), ID);
  });

  it('accepts the same id given twice', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}&v=${ID}`)), ID);
  });

  it('ignores an empty query parameter elsewhere', () => {
    assert.equal(videoIdFromUrl(watch(`&v=${ID}&`)), ID);
  });

  it('ignores a parameter whose name only contains v', () => {
    assert.equal(videoIdFromUrl(watch(`vv=xxxxxxxxxxx&v=${ID}`)), ID);
  });
});

describe('videoIdFromUrl: not a watch page', () => {
  it('is null for the home page', () => {
    assert.equal(videoIdFromUrl('https://www.youtube.com/'), null);
  });

  it('is null for search results', () => {
    assert.equal(videoIdFromUrl('https://www.youtube.com/results?search_query=test'), null);
  });

  it('is null for a channel', () => {
    assert.equal(videoIdFromUrl('https://www.youtube.com/@SomeChannel'), null);
  });

  it('is null for a channel\'s videos tab', () => {
    assert.equal(videoIdFromUrl('https://www.youtube.com/@SomeChannel/videos'), null);
  });

  it('is null for a playlist page', () => {
    assert.equal(videoIdFromUrl('https://www.youtube.com/playlist?list=PLabc123'), null);
  });

  it('is null for a short', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com/shorts/${ID}`), null);
  });

  it('is null for an embed', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com/embed/${ID}`), null);
  });

  it('is null for a live URL', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com/live/${ID}`), null);
  });

  it('is null for /watch/ with a trailing slash', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com/watch/?v=${ID}`), null);
  });

  it('is null for the id in the path after /watch/', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com/watch/${ID}`), null);
  });

  it('is null for an upper-case path', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com/WATCH?v=${ID}`), null);
  });

  it('is null for a youtu.be link', () => {
    assert.equal(videoIdFromUrl(`https://youtu.be/${ID}`), null);
  });

  it('is null for youtube.com without www', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}`).replace('www.', '')), null);
  });

  it('is null for m.youtube.com', () => {
    assert.equal(videoIdFromUrl(`https://m.youtube.com/watch?v=${ID}`), null);
  });

  it('is null for music.youtube.com', () => {
    assert.equal(videoIdFromUrl(`https://music.youtube.com/watch?v=${ID}`), null);
  });

  it('is null for youtube-nocookie.com', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube-nocookie.com/watch?v=${ID}`), null);
  });

  it('is null for a look-alike host that ends in youtube.com', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com.evil.example/watch?v=${ID}`), null);
  });

  it('is null for a look-alike host that starts with a longer name', () => {
    assert.equal(videoIdFromUrl(`https://notwww.youtube.com/watch?v=${ID}`), null);
  });

  it('is null for a host with a trailing dot', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com./watch?v=${ID}`), null);
  });

  it('is null for http (YouTube is https only; decided)', () => {
    assert.equal(videoIdFromUrl(`http://www.youtube.com/watch?v=${ID}`), null);
  });

  it('is null for another port', () => {
    assert.equal(videoIdFromUrl(`https://www.youtube.com:8443/watch?v=${ID}`), null);
  });

  it('is null with credentials in the URL', () => {
    assert.equal(videoIdFromUrl(`https://user:pw@www.youtube.com/watch?v=${ID}`), null);
  });

  it('is null for a username alone', () => {
    assert.equal(videoIdFromUrl(`https://user@www.youtube.com/watch?v=${ID}`), null);
  });

  it('is null for a scheme-relative URL', () => {
    assert.equal(videoIdFromUrl(`//www.youtube.com/watch?v=${ID}`), null);
  });

  it('is null for a path alone', () => {
    assert.equal(videoIdFromUrl(`/watch?v=${ID}`), null);
  });

  it('is null for a javascript: URL', () => {
    assert.equal(videoIdFromUrl(`javascript:alert('/watch?v=${ID}')`), null);
  });
});

describe('videoIdFromUrl: bad ids', () => {
  it('is null without a v parameter', () => {
    assert.equal(videoIdFromUrl(watch('t=10')), null);
  });

  it('is null without a query', () => {
    assert.equal(videoIdFromUrl('https://www.youtube.com/watch'), null);
  });

  it('is null for an empty v', () => {
    assert.equal(videoIdFromUrl(watch('v=')), null);
  });

  it('is null for a bare v with no equals sign', () => {
    assert.equal(videoIdFromUrl(watch('v')), null);
  });

  it('is null for a 10-character v', () => {
    assert.equal(videoIdFromUrl(watch('v=dQw4w9WgXc')), null);
  });

  it('is null for a 12-character v', () => {
    assert.equal(videoIdFromUrl(watch('v=dQw4w9WgXcQQ')), null);
  });

  it('is null for a v with a non-ASCII letter', () => {
    assert.equal(videoIdFromUrl(watch('v=dQw4w9WgXcé')), null);
  });

  it('is null for a v with a percent-encoded non-ASCII letter', () => {
    assert.equal(videoIdFromUrl(watch('v=dQw4w9WgXc%C3%A9')), null);
  });

  it('is null for a v with an emoji', () => {
    assert.equal(videoIdFromUrl(watch('v=dQw4w9WgX🙂')), null);
  });

  it('is null for a v with an encoded space', () => {
    assert.equal(videoIdFromUrl(watch(`v=%20${ID.slice(1)}`)), null);
  });

  it('is null for a v with a plus (a space once decoded)', () => {
    assert.equal(videoIdFromUrl(watch('v=dQw4w9+WgXc')), null);
  });

  it('is null for a v with an encoded slash', () => {
    assert.equal(videoIdFromUrl(watch('v=dQw4w%2FWgXcQ')), null);
  });

  it('is null for an option-looking v', () => {
    assert.equal(videoIdFromUrl(watch('v=--exec=cal')), null);
  });

  it('is null for an upper-case V parameter', () => {
    assert.equal(videoIdFromUrl(watch(`V=${ID}`)), null);
  });

  it('is null for two different ids (never guess)', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}&v=jNQXAC9IVRw`)), null);
  });

  it('is null for a valid id followed by an invalid copy', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}&v=bad`)), null);
  });

  it('is null for an invalid id followed by a valid one', () => {
    assert.equal(videoIdFromUrl(watch(`v=bad&v=${ID}`)), null);
  });

  it('is null for a valid id followed by an empty v', () => {
    assert.equal(videoIdFromUrl(watch(`v=${ID}&v=`)), null);
  });
});

describe('videoIdFromUrl: not a URL', () => {
  it('is null for the empty string', () => {
    assert.equal(videoIdFromUrl(''), null);
  });

  it('is null for a bare id', () => {
    assert.equal(videoIdFromUrl(ID), null);
  });

  it('is null for garbage', () => {
    assert.equal(videoIdFromUrl('not a url at all'), null);
  });

  it('is null for undefined', () => {
    assert.equal(videoIdFromUrl(undefined), null);
  });

  it('is null for null', () => {
    assert.equal(videoIdFromUrl(null), null);
  });

  it('is null for a URL object (strings only)', () => {
    assert.equal(videoIdFromUrl(new URL(watch(`v=${ID}`))), null);
  });

  it('is null for a number', () => {
    assert.equal(videoIdFromUrl(42), null);
  });
});

describe('constants', () => {
  it('name the watch host', () => {
    assert.equal(WATCH_HOST, 'www.youtube.com');
  });

  it('name the watch path', () => {
    assert.equal(WATCH_PATH, '/watch');
  });
});

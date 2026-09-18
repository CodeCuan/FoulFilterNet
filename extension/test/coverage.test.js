import { describe, it } from 'node:test';
import assert from 'node:assert/strict';
import { contains, coveredAhead, coveredRunEnd, normaliseCoverage, TOUCH_TOLERANCE } from '../src/coverage.js';

describe('normaliseCoverage', () => {
  it('gives no runs for an empty list', () => {
    assert.deepEqual(normaliseCoverage([]), []);
  });

  it('gives no runs for null', () => {
    assert.deepEqual(normaliseCoverage(null), []);
  });

  it('gives no runs for undefined', () => {
    assert.deepEqual(normaliseCoverage(undefined), []);
  });

  it('gives no runs for a non-array', () => {
    assert.deepEqual(normaliseCoverage({ 0: [0, 1], length: 1 }), []);
  });

  it('keeps a single interval', () => {
    assert.deepEqual(normaliseCoverage([[0, 24]]), [[0, 24]]);
  });

  it('keeps separate intervals apart', () => {
    assert.deepEqual(normaliseCoverage([[0, 24], [50, 70]]), [[0, 24], [50, 70]]);
  });

  it('merges touching intervals', () => {
    assert.deepEqual(normaliseCoverage([[0, 25], [25, 47]]), [[0, 47]]);
  });

  it('merges a chain of touching intervals', () => {
    assert.deepEqual(normaliseCoverage([[0, 25], [25, 47], [47, 69]]), [[0, 69]]);
  });

  it('merges intervals separated by float noise', () => {
    assert.deepEqual(normaliseCoverage([[0, 25], [25 + TOUCH_TOLERANCE / 2, 47]]), [[0, 47]]);
  });

  it('does not merge intervals separated by more than the tolerance', () => {
    assert.equal(normaliseCoverage([[0, 25], [25.001, 47]]).length, 2);
  });

  it('merges overlapping intervals', () => {
    assert.deepEqual(normaliseCoverage([[0, 30], [20, 47]]), [[0, 47]]);
  });

  it('merges an interval inside another', () => {
    assert.deepEqual(normaliseCoverage([[0, 50], [10, 20]]), [[0, 50]]);
  });

  it('sorts unsorted intervals', () => {
    assert.deepEqual(normaliseCoverage([[50, 70], [0, 24]]), [[0, 24], [50, 70]]);
  });

  it('merges unsorted touching intervals', () => {
    assert.deepEqual(normaliseCoverage([[25, 47], [0, 25]]), [[0, 47]]);
  });

  it('drops an empty interval', () => {
    assert.deepEqual(normaliseCoverage([[5, 5]]), []);
  });

  it('drops a reversed interval', () => {
    assert.deepEqual(normaliseCoverage([[10, 5]]), []);
  });

  it('drops an interval with a NaN end', () => {
    assert.deepEqual(normaliseCoverage([[0, NaN]]), []);
  });

  it('drops an interval with an infinite end', () => {
    assert.deepEqual(normaliseCoverage([[0, Infinity]]), []);
  });

  it('drops an interval of strings', () => {
    assert.deepEqual(normaliseCoverage([['0', '10']]), []);
  });

  it('drops an interval that is not a pair', () => {
    assert.deepEqual(normaliseCoverage([[0], 3, null]), []);
  });

  it('keeps the valid intervals among invalid ones', () => {
    assert.deepEqual(normaliseCoverage([[10, 5], [0, 4], null]), [[0, 4]]);
  });

  it('keeps an interval that starts below zero', () => {
    assert.deepEqual(normaliseCoverage([[-2, 4]]), [[-2, 4]]);
  });

  it('does not mutate its input', () => {
    const input = [[25, 47], [0, 25]];
    normaliseCoverage(input);
    assert.deepEqual(input, [[25, 47], [0, 25]]);
  });

  it('returns new pairs, not the input\'s', () => {
    const pair = [0, 10];
    assert.notEqual(normaliseCoverage([pair])[0], pair);
  });
});

describe('coveredAhead', () => {
  const COVERAGE = [[0, 24], [50, 70]];

  it('is 0 with no coverage', () => {
    assert.equal(coveredAhead([], 10), 0);
  });

  it('is 0 for null coverage', () => {
    assert.equal(coveredAhead(null, 10), 0);
  });

  it('is the whole run from its start', () => {
    assert.equal(coveredAhead(COVERAGE, 0), 24);
  });

  it('is the rest of the run from inside it', () => {
    assert.equal(coveredAhead(COVERAGE, 10), 14);
  });

  it('is 0 exactly at the end of a run', () => {
    assert.equal(coveredAhead(COVERAGE, 24), 0);
  });

  it('is 0 in a gap', () => {
    assert.equal(coveredAhead(COVERAGE, 30), 0);
  });

  it('is 0 just before a run starts', () => {
    assert.equal(coveredAhead(COVERAGE, 49.999), 0);
  });

  it('is the whole later run exactly at its start', () => {
    assert.equal(coveredAhead(COVERAGE, 50), 20);
  });

  it('is 0 past the last run', () => {
    assert.equal(coveredAhead(COVERAGE, 100), 0);
  });

  it('does not count a run beyond a gap', () => {
    assert.equal(coveredAhead([[0, 24], [24.5, 60]], 20), 4);
  });

  it('runs across touching intervals', () => {
    assert.equal(coveredAhead([[0, 25], [25, 47]], 20), 27);
  });

  it('runs across touching intervals given out of order', () => {
    assert.equal(coveredAhead([[25, 47], [0, 25]], 20), 27);
  });

  it('runs across overlapping intervals', () => {
    assert.equal(coveredAhead([[0, 30], [20, 47]], 10), 37);
  });

  it('counts a negative position as 0', () => {
    assert.equal(coveredAhead(COVERAGE, -1), 24);
  });

  it('is 0 for a negative position when 0 is not covered', () => {
    assert.equal(coveredAhead([[5, 10]], -1), 0);
  });

  it('is 0 for a NaN position', () => {
    assert.equal(coveredAhead(COVERAGE, NaN), 0);
  });

  it('is 0 for an infinite position', () => {
    assert.equal(coveredAhead(COVERAGE, Infinity), 0);
  });

  it('is 0 for a position that is not a number', () => {
    assert.equal(coveredAhead(COVERAGE, '10'), 0);
  });

  it('works with fractional seconds', () => {
    assert.ok(Math.abs(coveredAhead([[0, 24.75]], 0.25) - 24.5) < 1e-9);
  });

  it('ignores an invalid interval that would otherwise cover the position', () => {
    assert.equal(coveredAhead([[20, 10]], 15), 0);
  });
});

describe('coveredRunEnd', () => {
  it('is null with no coverage', () => {
    assert.equal(coveredRunEnd([], 0), null);
  });

  it('is the end of the run containing the position', () => {
    assert.equal(coveredRunEnd([[0, 24], [50, 70]], 55), 70);
  });

  it('is the end of the run exactly at its end', () => {
    assert.equal(coveredRunEnd([[0, 24]], 24), 24);
  });

  it('is the end of the merged run', () => {
    assert.equal(coveredRunEnd([[25, 47], [0, 25]], 3), 47);
  });

  it('is null in a gap', () => {
    assert.equal(coveredRunEnd([[0, 24], [50, 70]], 30), null);
  });

  it('is null for NaN', () => {
    assert.equal(coveredRunEnd([[0, 24]], NaN), null);
  });

  it('counts a negative position as 0', () => {
    assert.equal(coveredRunEnd([[0, 24]], -3), 24);
  });
});

describe('contains', () => {
  const COVERAGE = [[0, 24], [50, 70]];

  it('is false with no coverage', () => {
    assert.equal(contains([], 0, 1), false);
  });

  it('is true for a span inside a run', () => {
    assert.equal(contains(COVERAGE, 5, 10), true);
  });

  it('is true for a whole run', () => {
    assert.equal(contains(COVERAGE, 0, 24), true);
  });

  it('is false for a span running past a run', () => {
    assert.equal(contains(COVERAGE, 20, 30), false);
  });

  it('is false for a span across a gap', () => {
    assert.equal(contains(COVERAGE, 10, 60), false);
  });

  it('is false for a span in a gap', () => {
    assert.equal(contains(COVERAGE, 30, 40), false);
  });

  it('is true across touching intervals', () => {
    assert.equal(contains([[0, 25], [25, 47]], 20, 30), true);
  });

  it('is true across unsorted touching intervals', () => {
    assert.equal(contains([[25, 47], [0, 25]], 20, 30), true);
  });

  it('is true for a point inside a run', () => {
    assert.equal(contains(COVERAGE, 10, 10), true);
  });

  it('is true for a point on a run\'s end', () => {
    assert.equal(contains(COVERAGE, 24, 24), true);
  });

  it('is true for a point on a run\'s start', () => {
    assert.equal(contains(COVERAGE, 50, 50), true);
  });

  it('is false for a point in a gap', () => {
    assert.equal(contains(COVERAGE, 30, 30), false);
  });

  it('is false for a reversed span', () => {
    assert.equal(contains(COVERAGE, 10, 5), false);
  });

  it('is false for a span starting below zero when coverage starts at zero', () => {
    assert.equal(contains(COVERAGE, -1, 5), false);
  });

  it('is false for NaN ends', () => {
    assert.equal(contains(COVERAGE, NaN, 5), false);
  });

  it('is false for non-numeric ends', () => {
    assert.equal(contains(COVERAGE, '0', '5'), false);
  });

  it('is false for null coverage', () => {
    assert.equal(contains(null, 0, 1), false);
  });
});

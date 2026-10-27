'use strict';
const test = require('node:test'), assert = require('node:assert/strict');
const { sealSnapshot, queryAt, project, diversity, baseline, RUBRIC } = require('../story-passages/index.cjs');
const { loadCanonical, EXPECTED_ACTION_SHA } = require('../story-passages/run.cjs');
const evidence = id => [{ evidenceId: 'generated-action', sourceId: 'generated-model-actions', locator: `test-only / ${id}`,
  origin: 'synthetic', method: 'generated', claimStatus: 'authored' }];
function row(id, kind, startMs, endMs, extra = {}) {
  return { recordId: id, actionKind: kind, period: 1, startMs, endMs, team: 'brentford', playerId: kind === 'possession' ? null : 'b1',
    recipientId: kind === 'pass' ? 'b2' : null, recipientTeam: kind === 'pass' ? 'brentford' : null,
    outcome: kind === 'possession' ? 'controlled' : kind === 'pass' ? 'completed' : 'goal', fromSlotId: kind === 'possession' ? null : 'slot1',
    toSlotId: kind === 'pass' ? 'slot2' : null, revision: 1, supersedesRevision: null, state: 'active', conflicts: null,
    acceptedSequence: 1, releaseMs: endMs, evidence: evidence(id), ...extra };
}
function fixture(rows = [row('pass1', 'pass', 100, 1000), row('spell1', 'possession', 0, 2000)]) {
  return sealSnapshot({ version: 1, resolution: 'flat-active-revision-1-only', fixtureId: 'test-fixture', datasetId: 'test-dataset',
    datasetVersion: 'test-v1', streamId: 'test-stream', acceptedHeadSequence: 2, closed: true,
    capabilities: ['generated-model-actions-v1'], sourceFingerprint: 'a'.repeat(64), presentationPolicyId: 'test-policy', presentationPolicyRevision: 1,
    durationMs: 10000, halfMs: 5000, teams: ['brentford', 'chelsea'], people: { b1: 'brentford', b2: 'brentford', c1: 'chelsea' }, rows });
}
function changed(change) { const value = fixture(); change(value); return sealSnapshot(value); }
const view = (snapshot, cursor = snapshot.durationMs, override = {}) => project(snapshot, { ...queryAt(snapshot, cursor), ...override });

test('synthetic safety: incomplete spell withholds final membership, facts and label at end-1; end releases', () => {
  const snapshot = fixture(), before = view(snapshot, 1999), at = view(snapshot, 2000);
  assert.equal(before.status, 'available'); assert.deepEqual(before.passages, []);
  assert.equal(before.facts.passAttempts, 1); assert.equal(before.facts.durationMs, 0);
  assert.equal(at.passages.length, 1); assert.equal(at.passages[0].facts.durationMs, 2000);
  assert.deepEqual(at.passages[0].actionRecordIds, ['pass1']); assert.equal(at.passages[0].label, 'Brief exchange');
  assert.deepEqual(view(snapshot, 1999).passages, []); // Previously viewing the future gives no exemption.
});
test('synthetic safety: pass completion end-1/end and later action/spell release', () => {
  const snapshot = changed(s => { s.rows[0].releaseMs = 3000; });
  assert.equal(view(snapshot, 999).facts.passAttempts, 0);
  assert.equal(view(snapshot, 1000).facts.passAttempts, 0);
  assert.equal(view(snapshot, 2999).passages.length, 0);
  assert.equal(view(snapshot, 3000).passages.length, 1);
  const plain = fixture(); assert.equal(view(plain, 999).facts.passAttempts, 0); assert.equal(view(plain, 1000).facts.passAttempts, 1);
  const spellLate = changed(s => { s.rows[1].releaseMs = 4000; });
  assert.equal(view(spellLate, 3999).passages.length, 0); assert.equal(view(spellLate, 4000).passages.length, 1);
});
test('synthetic safety: gaps and periods preserve separate control membership', () => {
  const snapshot = fixture([row('a', 'possession', 0, 1000), row('b', 'possession', 2000, 5000),
    row('c', 'possession', 5000, 6000, { period: 2 })]);
  const result = view(snapshot); assert.equal(result.status, 'available');
  assert.deepEqual(result.passages.map(p => p.boundaryBefore), ['recording-start', 'unowned-gap', 'period']);
  assert.deepEqual(result.passages.map(p => p.facts.durationMs), [1000, 3000, 1000]);
  assert.equal(view(changed(s => { s.rows[0].endMs = 5001; })).status, 'unavailable');
});
test('synthetic safety: turnover ends old control; cannot attach to new team', () => {
  const snapshot = fixture([row('turn', 'pass', 100, 1000, { recipientId: 'c1', recipientTeam: 'chelsea', outcome: 'turnover' }),
    row('a', 'possession', 0, 1000), row('b', 'possession', 1000, 2000, { team: 'chelsea' })]);
  const result = view(snapshot); assert.equal(result.status, 'available');
  assert.equal(result.passages[0].facts.turnovers, 1); assert.deepEqual(result.passages[1].actionRecordIds, []);
  const broken = structuredClone(snapshot); broken.rows[1].endMs = 1100;
  assert.equal(view(sealSnapshot(broken)).status, 'unavailable');
});
test('synthetic safety: missing capability Liverpool differs from known zero and undefined denominator', () => {
  const zero = view(fixture(), 0); assert.equal(zero.status, 'available'); assert.equal(zero.facts.passAttempts, 0);
  assert.equal(zero.facts.passCompletionPercent, null);
  const missing = changed(s => { s.fixtureId = 'fixture-liverpool'; s.capabilities = []; });
  const result = view(missing); assert.equal(result.status, 'unavailable'); assert.equal(result.facts, null); assert.deepEqual(result.passages, []);
});
test('synthetic safety: generated provenance, membership and outcomes fail closed', () => {
  for (const mutate of [s => { s.rows[0].evidence[0].method = 'observed'; }, s => { s.rows[0].evidence[0].origin = 'real'; },
    s => { s.rows[0].evidence = []; }, s => { s.rows[0].outcome = 'turnover'; }, s => { s.rows[0].recipientId = 'c1'; },
    s => { s.rows[0].releaseMs = null; }, s => { s.rows[0].releaseMs = 999; }, s => { s.rows[0].startMs = -1; }])
    assert.equal(view(changed(mutate)).status, 'unavailable');
});
test('synthetic safety: wrong fixture/dataset/recording/version/head/presentation/fingerprint rejects', () => {
  const snapshot = fixture();
  for (const name of ['fixtureId', 'datasetId', 'datasetVersion', 'streamId', 'sourceFingerprint', 'fingerprint', 'presentationPolicyId', 'presentationPolicyRevision'])
    assert.equal(view(snapshot, 10000, { [name]: 'wrong' }).status, 'unavailable', name);
  for (const applicationSequence of [null, 1, 3]) assert.equal(view(snapshot, 10000, { applicationSequence }).status, 'unavailable');
  assert.equal(view(snapshot, 10000, { knowledgeMode: 'known_then' }).status, 'unavailable');
  assert.equal(view(snapshot, 10000, { knowledgeAsOfUtc: '2026-01-01T00:00:00Z' }).status, 'unavailable');
  assert.equal(view(changed(s => { s.rows[0].acceptedSequence = 3; })).status, 'unavailable');
});
test('synthetic safety: unsupported corrections/withdrawals/conflicts/duplicates and tampering reject', () => {
  for (const mutate of [s => { s.rows[0].revision = 2; s.rows[0].supersedesRevision = 1; }, s => { s.rows[0].state = 'withdrawn'; },
    s => { s.rows[0].conflicts = []; }, s => { s.rows[0].effectiveTime = { basis: 'date' }; },
    s => { s.rows[0].withdrawalReason = 'unknown'; }, s => { s.rows.push(s.rows[0]); }, s => { s.closed = false; }])
    assert.equal(view(changed(mutate)).status, 'unavailable');
  const tampered = fixture(); tampered.rows[0].outcome = 'turnover';
  assert.equal(view(tampered).status, 'unavailable');
});
test('synthetic safety: orphan actions, overlapping intervals, oversized spells reject', () => {
  for (const mutate of [s => { s.rows[0].endMs = 3000; }, s => { s.rows.push(row('other', 'possession', 500, 1500)); },
    s => { s.rows.push(row('overlap', 'pass', 500, 1500)); },
    s => { s.durationMs = 200000; s.halfMs = 100000; s.rows[1].endMs = 61000; s.rows[1].releaseMs = 61000; },
    s => { s.rows = [row('control', 'possession', 0, 2000), ...Array.from({ length: 13 }, (_, i) => row(`p${i}`, 'pass', i * 100, i * 100 + 50))]; }])
    assert.equal(view(changed(mutate)).status, 'unavailable');
});
test('synthetic safety: frozen rubric is nonoverlapping, shot has precedence, zero is Unclear', () => {
  assert.equal(RUBRIC.version, 'provisional-count-rubric-v1');
  assert.deepEqual([0, 1, 2, 3, 4].map(passCompleted => baseline({ passCompleted, shots: 0 })), ['Unclear', 'Brief exchange', 'Brief exchange', 'Passing sequence', 'Passing sequence']);
  assert.equal(baseline({ passCompleted: 4, shots: 1 }), 'Shot-ending passage');
  const result = view(fixture([row('shot', 'shot', 0, 1000), row('control', 'possession', 0, 1000)]));
  assert.equal(result.passages[0].label, 'Shot-ending passage'); assert.equal(result.passages[0].facts.passCompletionPercent, null);
});

// Actual generated corpus: kept separate from synthetic safety fixtures above.
test('actual canonical snapshot pins counts, boundaries, explicit membership and structural diversity', () => {
  const { snapshot, receipts } = loadCanonical();
  assert.equal(snapshot.sourceFingerprint, EXPECTED_ACTION_SHA); assert.equal(snapshot.acceptedHeadSequence, 979);
  const result = view(snapshot), audit = diversity(result.passages);
  assert.equal(result.status, 'available', result.reason);
  assert.deepEqual(result.facts, { passAttempts: 728, passCompleted: 583, turnovers: 145, shots: 3, durationMs: 5850000, passCompletionPercent: 100 * 583 / 728 });
  assert.equal(result.passages.length, 150);
  assert.equal(result.passages.flatMap(p => p.actionRecordIds).length, 731);
  assert.equal(new Set(result.passages.flatMap(p => p.actionRecordIds)).size, 731);
  assert.deepEqual([audit.semanticPatterns, audit.exactFactPatterns, audit.semantic[0].count, audit.baselineRubricDisagreements], [7, 12, 143, 0]);
  assert.equal(audit.distinct24Available, false); assert.equal(audit.heldOutCases, 0); assert.equal(audit.modelCalls, 0); assert.equal(audit.modelAccuracy, null);
  for (const passage of result.passages) {
    assert.equal(view(snapshot, passage.endMs - 1).passages.some(p => p.id === passage.id), false);
    assert.equal(view(snapshot, passage.endMs).passages.some(p => p.id === passage.id), true);
  }
  assert.equal(receipts.length, 986);
});
test('synthetic safety: renaming/team/time changes do not create semantic diversity', () => {
  const passage = view(fixture()).passages[0];
  const copies = Array.from({ length: 30 }, (_, i) => ({ ...passage, id: `copy${i}`, team: `team${i}`, startMs: i * 100, endMs: i * 100 + 2000 }));
  const audit = diversity(copies); assert.equal(audit.semanticPatterns, 1); assert.equal(audit.exactFactPatterns, 1); assert.equal(audit.distinct24Available, false);
});

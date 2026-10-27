'use strict';
// Offline audit only: callers supply one already flattened, revision-1 snapshot.
const crypto = require('node:crypto');
const POLICY = 'generated-model-actions-v1';
const GROUPING = Object.freeze({ version: 'control-spell-v1', maxDurationMs: 60000, maxActions: 12 });
const RUBRIC = Object.freeze({
  version: 'provisional-count-rubric-v1',
  definitions: {
    'Shot-ending passage': 'At least one shot; final action is a shot; takes precedence over pass labels.',
    'Passing sequence': 'No shots and at least three completed passes.',
    'Brief exchange': 'No shots and one or two completed passes.',
    Unclear: 'No shots and no completed passes. Valid evidence, not unavailable evidence.'
  },
  qualification: 'Generated replay; provisional editorial count thresholds, not a tactical judgment.'
});
const sha = value => crypto.createHash('sha256').update(typeof value === 'string' || Buffer.isBuffer(value) ? value : JSON.stringify(value)).digest('hex');
const without = (value, key) => Object.fromEntries(Object.entries(value).filter(([name]) => name !== key));
function sealSnapshot(snapshot) {
  const value = structuredClone(snapshot);
  value.rows = value.rows.map(row => ({ ...without(row, 'fingerprint'), fingerprint: sha(without(row, 'fingerprint')) }));
  value.fingerprint = sha(without(value, 'fingerprint'));
  return value;
}
function requireValid(condition, reason) { if (!condition) throw Error(reason); }
function baseline(facts) {
  if (facts.shots) return 'Shot-ending passage';
  if (facts.passCompleted >= 3) return 'Passing sequence';
  return facts.passCompleted ? 'Brief exchange' : 'Unclear';
}
// Separate expression of the frozen rubric permits a visible comparison; neither is a gold label.
function rubricLabel(actions) {
  if (actions.some(row => row.actionKind === 'shot')) return 'Shot-ending passage';
  const completed = actions.filter(row => row.actionKind === 'pass' && row.outcome === 'completed').length;
  if (completed === 0) return 'Unclear';
  return completed < 3 ? 'Brief exchange' : 'Passing sequence';
}
function facts(actions, durationMs = null) {
  const passes = actions.filter(row => row.actionKind === 'pass');
  return { passAttempts: passes.length, passCompleted: passes.filter(row => row.outcome === 'completed').length,
    turnovers: passes.filter(row => row.outcome === 'turnover').length, shots: actions.filter(row => row.actionKind === 'shot').length,
    durationMs, passCompletionPercent: passes.length ? 100 * passes.filter(row => row.outcome === 'completed').length / passes.length : null };
}
function validate(snapshot, query) {
  requireValid(snapshot.version === 1 && snapshot.resolution === 'flat-active-revision-1-only', 'unsupported snapshot resolution');
  requireValid(snapshot.capabilities?.includes(POLICY), 'missing generated action capability');
  for (const name of ['fixtureId', 'datasetId', 'datasetVersion', 'streamId', 'sourceFingerprint', 'fingerprint', 'presentationPolicyId', 'presentationPolicyRevision'])
    requireValid(snapshot[name] != null && query[name] === snapshot[name], `binding mismatch: ${name}`);
  requireValid(/^[a-f0-9]{64}$/.test(snapshot.sourceFingerprint) && snapshot.fingerprint === sha(without(snapshot, 'fingerprint')), 'snapshot fingerprint mismatch');
  requireValid(snapshot.closed === true && Number.isSafeInteger(snapshot.acceptedHeadSequence) && snapshot.acceptedHeadSequence > 0
    && query.applicationSequence === snapshot.acceptedHeadSequence, 'unsupported or stale application head');
  requireValid(query.knowledgeMode === 'retrospective_replay' && query.knowledgeAsOfUtc == null, 'unsupported knowledge mode');
  requireValid(Number.isSafeInteger(query.cursorMs) && query.cursorMs >= 0 && query.cursorMs <= snapshot.durationMs, 'invalid cursor');
  requireValid(Number.isSafeInteger(snapshot.halfMs) && snapshot.halfMs > 0 && snapshot.halfMs < snapshot.durationMs, 'invalid period bounds');
  requireValid(Array.isArray(snapshot.teams) && snapshot.teams.length === 2 && new Set(snapshot.teams).size === 2 && Array.isArray(snapshot.rows), 'missing teams/actions');
  const ids = new Set();
  for (const row of snapshot.rows) {
    requireValid(!ids.has(row.recordId) && typeof row.recordId === 'string', 'duplicate/missing record identity'); ids.add(row.recordId);
    requireValid(row.revision === 1 && row.supersedesRevision === null && row.state === 'active' && row.conflicts === null
      && row.effectiveTime == null && row.withdrawalReason == null,
      'unsupported revision/withdrawal/conflict; resolve with real ledger first');
    requireValid(row.fingerprint === sha(without(row, 'fingerprint')), 'record fingerprint mismatch');
    requireValid(Number.isSafeInteger(row.acceptedSequence) && row.acceptedSequence > 0 && row.acceptedSequence <= snapshot.acceptedHeadSequence, 'record beyond snapshot head');
    requireValid(row.evidence?.length === 1 && row.evidence[0].method === 'generated' && row.evidence[0].origin === 'synthetic'
      && row.evidence[0].claimStatus === 'authored' && row.evidence[0].sourceId === 'generated-model-actions'
      && row.evidence[0].evidenceId === 'generated-action' && row.evidence[0].locator.includes(row.recordId), 'invalid generated provenance');
    requireValid([1, 2].includes(row.period) && Number.isSafeInteger(row.startMs) && Number.isSafeInteger(row.endMs)
      && row.startMs >= (row.period === 1 ? 0 : snapshot.halfMs) && row.endMs > row.startMs
      && row.endMs <= (row.period === 1 ? snapshot.halfMs : snapshot.durationMs), 'invalid action interval/period');
    requireValid(Number.isSafeInteger(row.releaseMs) && row.releaseMs >= row.endMs && row.releaseMs <= snapshot.durationMs, 'missing/invalid release');
    requireValid(snapshot.teams.includes(row.team) && ['pass', 'shot', 'possession'].includes(row.actionKind), 'unsupported action/team');
    const member = (id, team) => typeof id === 'string' && snapshot.people[id] === team;
    if (row.actionKind === 'pass') requireValid(member(row.playerId, row.team) && member(row.recipientId, row.recipientTeam)
      && row.playerId !== row.recipientId && row.fromSlotId && row.toSlotId && row.fromSlotId !== row.toSlotId
      && row.outcome === (row.team === row.recipientTeam ? 'completed' : 'turnover'), 'invalid pass identity/outcome');
    else if (row.actionKind === 'shot') requireValid(member(row.playerId, row.team) && row.outcome === 'goal'
      && row.recipientId === null && row.recipientTeam === null && row.toSlotId === null, 'invalid generated shot');
    else requireValid(row.outcome === 'controlled' && ['playerId', 'recipientId', 'recipientTeam', 'fromSlotId', 'toSlotId'].every(key => row[key] === null), 'invalid team control');
  }
}
function group(snapshot) {
  const spells = snapshot.rows.filter(row => row.actionKind === 'possession').sort((a, b) => a.startMs - b.startMs);
  const actions = snapshot.rows.filter(row => row.actionKind !== 'possession').sort((a, b) => a.startMs - b.startMs);
  const assigned = new Set(), passages = [];
  for (let i = 0; i < spells.length; i++) {
    const spell = spells[i], previous = spells[i - 1];
    requireValid(!previous || previous.endMs <= spell.startMs, 'overlapping control spells');
    const members = actions.filter(row => row.period === spell.period && row.team === spell.team && row.startMs >= spell.startMs && row.endMs <= spell.endMs);
    requireValid(spell.endMs - spell.startMs <= GROUPING.maxDurationMs && members.length <= GROUPING.maxActions, 'passage exceeds audit caps');
    for (let j = 0; j < members.length; j++) {
      const row = members[j];
      requireValid(!assigned.has(row.recordId) && (!j || members[j - 1].endMs <= row.startMs), 'overlapping/duplicate actions');
      assigned.add(row.recordId);
      if (row.outcome === 'turnover' || row.actionKind === 'shot') requireValid(j === members.length - 1 && row.endMs === spell.endMs, 'turnover/shot must end control spell');
    }
    const metrics = facts(members, spell.endMs - spell.startMs), label = baseline(metrics);
    passages.push({ id: `passage:${spell.recordId}`, period: spell.period, team: spell.team, startMs: spell.startMs, endMs: spell.endMs,
      eligibleAtMs: Math.max(spell.endMs, spell.releaseMs, ...members.map(row => row.releaseMs)),
      boundaryBefore: !previous ? 'recording-start' : previous.period !== spell.period ? 'period' : previous.endMs < spell.startMs ? 'unowned-gap' : 'control-change',
      controlRecordId: spell.recordId, actionRecordIds: members.map(row => row.recordId),
      evidence: [spell, ...members].map(row => ({ recordId: row.recordId, revision: row.revision, fingerprint: row.fingerprint, evidenceIds: row.evidence.map(e => e.evidenceId) })),
      facts: metrics, label, rubricLabel: rubricLabel(members), qualification: 'Generated replay',
      semanticSignature: JSON.stringify(members.map(row => [row.actionKind, row.outcome])),
      factSignature: JSON.stringify([members.map(row => [row.actionKind, row.outcome]), metrics.durationMs]) });
  }
  requireValid(assigned.size === actions.length, 'action lacks one containing control spell');
  return passages;
}
function queryAt(snapshot, cursorMs) {
  const names = ['fixtureId', 'datasetId', 'datasetVersion', 'streamId', 'sourceFingerprint', 'fingerprint', 'presentationPolicyId', 'presentationPolicyRevision'];
  return { ...Object.fromEntries(names.map(name => [name, snapshot[name]])), applicationSequence: snapshot.acceptedHeadSequence,
    knowledgeMode: 'retrospective_replay', cursorMs };
}
function project(snapshot, query) {
  try {
    validate(snapshot, query);
    const passages = group(snapshot).filter(passage => passage.eligibleAtMs <= query.cursorMs);
    const eligible = snapshot.rows.filter(row => row.endMs <= query.cursorMs && row.releaseMs <= query.cursorMs);
    return { status: 'available', qualification: 'Generated replay', binding: { ...query }, passages,
      facts: facts(eligible, eligible.filter(row => row.actionKind === 'possession').reduce((sum, row) => sum + row.endMs - row.startMs, 0)) };
  } catch (error) { return { status: 'unavailable', reason: error.message, passages: [], facts: null }; }
}
function diversity(passages) {
  const frequencies = key => Object.entries(passages.reduce((counts, item) => { counts[item[key]] = (counts[item[key]] || 0) + 1; return counts; }, {}))
    .map(([signature, count]) => ({ signature, count })).sort((a, b) => b.count - a.count || a.signature.localeCompare(b.signature));
  const semantic = frequencies('semanticSignature'), exactFacts = frequencies('factSignature');
  return { passages: passages.length, semanticPatterns: semantic.length, exactFactPatterns: exactFacts.length, semantic, exactFacts,
    baselineRubricDisagreements: passages.filter(p => p.label !== p.rubricLabel).length,
    labels: frequencies('label'), distinct24Available: semantic.length >= 24 && exactFacts.length >= 24,
    heldOutCases: 0, modelCalls: 0, modelAccuracy: null, aiUtilityEstablished: false,
    decision: 'STOP before provider trial: every proposed distinction is computable from explicit counts/outcomes; no model-only supported distinction.',
    limitation: 'All source passages inspected for audit: none is held out. Signature counts are upper bounds on diversity, not independent football cases.' };
}
module.exports = { POLICY, GROUPING, RUBRIC, sha, sealSnapshot, facts, baseline, rubricLabel, queryAt, project, diversity };

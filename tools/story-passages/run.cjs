'use strict';
const fs = require('node:fs'), path = require('node:path'), zlib = require('node:zlib');
const { POLICY, GROUPING, RUBRIC, sha, sealSnapshot, queryAt, project, diversity } = require('./index.cjs');
const ROOT = path.resolve(__dirname, '../..');
const EXPECTED_ACTION_SHA = 'e790083f8482661a27a5f8f4999aa0b11cb4676e046d8346bd04a3c3d65b89b1';
function loadCanonical(root = ROOT) {
  const receipts = [], bytesByPath = new Map();
  const read = relative => {
    const bytes = fs.readFileSync(path.join(root, relative));
    receipts.push({ path: relative.replaceAll('\\', '/'), bytes: bytes.length, beforeSha256: sha(bytes) });
    bytesByPath.set(relative, bytes); return bytes;
  };
  const sourceDir = '.local/shared-inputs/brentford-full-v5', canonicalDir = '.local/canonical-brentford-v5';
  const actionsBytes = read(`${sourceDir}/generated-actions.json`), source = JSON.parse(actionsBytes);
  if (sha(actionsBytes) !== EXPECTED_ACTION_SHA) throw Error('Canonical generated action pin changed');
  const sourceManifest = JSON.parse(read(`${sourceDir}/manifest.json`));
  if (sourceManifest.generatedActions.sha256 !== EXPECTED_ACTION_SHA || sourceManifest.generatedActions.count !== source.rows.length) throw Error('Source receipt mismatch');
  for (const file of ['data/official-match-records/brentford-chelsea-2026-09-18.json', 'data/reconstructed-match-illustrations/brentford-chelsea-2026-09-18.json', 'prototypes/first-experience/pl-reconstructed-model.js', 'tools/export-brentford-full.cjs'])
    if (sha(read(file)) !== sourceManifest.hashes[file]) throw Error(`Original source pin changed: ${file}`);
  const index = JSON.parse(read(`${canonicalDir}/export.json`)), dataset = index.datasets[0];
  if (index.datasets.length !== 1 || dataset.actions !== source.rows.length || dataset.stream.fixtureId !== source.fixtureId || index.chunks.length !== dataset.batches) throw Error('Archive identity/count mismatch');
  let manifest, schedule, people, closed = false;
  const records = [], seen = new Set();
  for (let i = 0; i < index.chunks.length; i++) {
    const chunk = index.chunks[i];
    if (path.basename(chunk.file) !== chunk.file) throw Error('Unsafe archive path');
    const bytes = read(`${canonicalDir}/${chunk.file}`);
    if (sha(bytes) !== chunk.hash || bytes.length !== chunk.bytes) throw Error('Archive chunk fingerprint mismatch');
    const batch = JSON.parse(zlib.gunzipSync(bytes));
    if (batch.sequence !== i + 1 || chunk.sequence !== batch.sequence || chunk.digest !== batch.digest || JSON.stringify(batch.stream) !== JSON.stringify(dataset.stream)
      || closed || batch.manifest.datasetId !== dataset.stream.datasetId || batch.manifest.datasetVersion !== dataset.stream.datasetVersion
      || batch.manifest.fixtureId !== source.fixtureId) throw Error('Archive sequence/identity mismatch');
    manifest ||= batch.manifest;
    if (sha(batch.manifest) !== sha(manifest)) throw Error('Unsupported manifest revisions/changes');
    for (const record of batch.records) {
      if (!['football_action', 'presentation_schedule', 'match_squad'].includes(record.payload.kind)) continue;
      if (seen.has(record.recordId) || record.revision !== 1 || record.supersedesRevision !== null || record.recordState !== 'active'
        || record.conflicts !== null || record.withdrawalReason !== null || record.effectiveTime !== null || record.datasetId !== dataset.stream.datasetId
        || record.datasetVersion !== dataset.stream.datasetVersion || record.fixtureId !== source.fixtureId) throw Error('Unsupported revision or record binding');
      seen.add(record.recordId);
      if (record.payload.kind === 'football_action') records.push({ record, acceptedSequence: batch.sequence });
      if (record.payload.kind === 'presentation_schedule') { if (schedule) throw Error('Ambiguous schedules'); schedule = record.payload; }
      if (record.payload.kind === 'match_squad') { if (people) throw Error('Ambiguous squads'); people = record.payload.people; }
    }
    closed = batch.closeStream;
  }
  if (!closed || !schedule || !people || records.length !== source.rows.length) throw Error('Incomplete closed archive');
  if (!manifest.capabilities.includes(POLICY) || !manifest.sources.some(s => s.sourceId === 'generated-model-actions' && s.artifactHash === EXPECTED_ACTION_SHA)
    || schedule.segments.length !== 2 || schedule.segments.some((s, i) => s.periodId !== `period-pl-2645236-${i ? 'second' : 'first'}`
      || s.cursorStartMs !== (i ? source.halfMs : 0) || s.cursorEndMs !== (i ? source.durationMs : source.halfMs) || s.localStartMs !== 0 || s.phase != null)) throw Error('Unsupported provenance or period schedule');
  const release = new Map();
  for (const item of schedule.releases) { if (release.has(item.recordId)) throw Error('Ambiguous releases'); release.set(item.recordId, item.cursorMs); }
  const originals = new Map(source.rows.map(row => [row.recordId, row]));
  const club = name => `team-pl-reference-${name}`;
  const person = id => id === null ? null : `person-pl-reference-${id.replace(':', '-')}`;
  const rows = records.map(({ record, acceptedSequence }) => {
    const row = originals.get(record.recordId), payload = record.payload;
    if (!row) throw Error('Archive action absent from canonical source');
    const fieldPaths = ['/footballTime', '/payload/actionKind', '/payload/teamId', '/payload/personId', '/payload/recipientId', '/payload/recipientTeamId', '/payload/outcome', '/payload/fromSlotId', '/payload/toSlotId'];
    if (fieldPaths.some(field => JSON.stringify(record.fieldEvidence[field]) !== '["generated-action"]')) throw Error('Missing generated field evidence');
    const offset = row.period === 1 ? 0 : source.halfMs;
    if (JSON.stringify(payload) !== JSON.stringify({ kind: 'football_action', actionKind: row.actionKind, teamId: club(row.team), personId: person(row.playerId),
      recipientId: person(row.recipientId), recipientTeamId: row.recipientTeam === null ? null : club(row.recipientTeam), outcome: row.outcome, fromSlotId: row.fromSlotId, toSlotId: row.toSlotId })
      || record.footballTime.periodId !== `period-pl-2645236-${row.period === 1 ? 'first' : 'second'}`
      || record.footballTime.startMs !== row.startMs - offset || record.footballTime.endMs !== row.endMs - offset
      || record.footballTime.precision !== 'source_interval' || record.footballTime.endInclusive !== false) throw Error('Canonical action mismatch');
    return { ...row, revision: record.revision, supersedesRevision: record.supersedesRevision, state: record.recordState, conflicts: record.conflicts,
      acceptedSequence, releaseMs: release.get(row.recordId), evidence: record.evidence, canonicalRecordFingerprint: sha(record) };
  });
  const rawPeople = Object.fromEntries(people.map(p => [p.personId, p.teamId]));
  for (const row of source.rows) for (const [id, team] of [[row.playerId, row.team], [row.recipientId, row.recipientTeam]])
    if (id !== null && rawPeople[person(id)] !== club(team)) throw Error('Canonical roster mismatch');
  const snapshot = sealSnapshot({ version: 1, resolution: 'flat-active-revision-1-only', ...dataset.stream,
    acceptedHeadSequence: index.chunks.at(-1).sequence, closed, capabilities: manifest.capabilities,
    sourceFingerprint: EXPECTED_ACTION_SHA, presentationPolicyId: schedule.policyId, presentationPolicyRevision: schedule.policyRevision,
    durationMs: source.durationMs, halfMs: source.halfMs, teams: ['brentford', 'chelsea'],
    people: Object.fromEntries(sourceManifest.people.map(p => [p.id, p.team])), rows });
  return { snapshot, receipts, bytesByPath, root, sourceManifest };
}
function run(root = ROOT) {
  const loaded = loadCanonical(root), { snapshot, receipts, bytesByPath } = loaded;
  const result = project(snapshot, queryAt(snapshot, snapshot.durationMs));
  if (result.status !== 'available') throw Error(result.reason);
  const audit = diversity(result.passages), destination = path.join(root, '.local/story-passages-trial');
  fs.mkdirSync(path.join(destination, 'source-bytes'), { recursive: true });
  const save = (file, value) => fs.writeFileSync(path.join(destination, file), `${JSON.stringify(value, null, 2)}\n`);
  // Preserve core source bytes exactly; chunk receipts retain all archive before/after hashes.
  for (const [relative, bytes] of bytesByPath) if (!relative.endsWith('.batch')) {
    const file = relative.replaceAll(/[\\/]/g, '__');
    fs.writeFileSync(path.join(destination, 'source-bytes', file), bytes);
  }
  for (const receipt of receipts) {
    receipt.afterSha256 = sha(fs.readFileSync(path.join(root, receipt.path)));
    if (receipt.beforeSha256 !== receipt.afterSha256) throw Error(`Source changed during audit: ${receipt.path}`);
  }
  save('accepted-flat-snapshot.json', snapshot);
  save('passages.json', result);
  const unique = new Map(); for (const passage of result.passages) if (!unique.has(passage.factSignature)) unique.set(passage.factSignature, passage);
  save('representative-cases.json', { qualification: 'Generated replay, audit-inspected representatives; not held-out gold labels', cases: [...unique.values()] });
  save('evidence-manifest.json', { version: 1, policy: POLICY, grouping: GROUPING, rubric: RUBRIC,
    snapshotFingerprint: snapshot.fingerprint, sourceFingerprint: snapshot.sourceFingerprint,
    archiveBinding: { datasetId: snapshot.datasetId, datasetVersion: snapshot.datasetVersion, streamId: snapshot.streamId, fixtureId: snapshot.fixtureId, acceptedHeadSequence: snapshot.acceptedHeadSequence },
    authority: 'Offline hash-verified closed archive only; not a live ledger/query acceptance or known-then proof.', receipts });
  const summary = { sourceRows: snapshot.rows.length, sourceFacts: result.facts, audit, grouping: GROUPING, rubric: RUBRIC,
    sourceBytesUnchanged: true, providerCalls: 0, productionIntegration: false, browserGate: 'not applicable: offline tooling only' };
  save('summary.json', summary);
  return { destination, ...summary };
}
if (require.main === module) {
  try { console.log(JSON.stringify(run(), null, 2)); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
module.exports = { loadCanonical, run, EXPECTED_ACTION_SHA };

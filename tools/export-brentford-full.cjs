'use strict';
// Bounded operator-side export of the EXISTING pure model. No movement rules live here.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto'), zlib = require('node:zlib');
const root = path.resolve(__dirname, '..');
const generatedActionPolicy = 'generated-model-actions-v1';
function buildGeneratedActions(model) {
  const rows = [];
  let control = null;
  const finishControl = () => { if(control) rows.push(control); control = null; };
  for(const action of model.actionLedger) {
    const startMs = Math.round(action.start*1000), endMs = Math.round(action.end*1000);
    const owned = ['carry','pass','shot'].includes(action.kind) && ['brentford','chelsea'].includes(action.fromTeam);
    if(!Number.isSafeInteger(startMs) || !Number.isSafeInteger(endMs) || startMs >= endMs ||
      ![1,2].includes(action.period) || startMs < (action.period===1?0:model.half*1000) ||
      endMs > (action.period===1?model.half:model.duration)*1000) throw Error('Invalid generated model action bounds');
    if(owned) {
      if(control && control.period===action.period && control.team===action.fromTeam && control.endMs===startMs) control.endMs = endMs;
      else {
        finishControl();
        control = {actionKind:'possession',period:action.period,startMs,endMs,team:action.fromTeam,
          playerId:null,recipientId:null,recipientTeam:null,outcome:'controlled',fromSlotId:null,toSlotId:null};
      }
    } else finishControl(); // Holds, restarts and unowned intervals never count as team control.
    const selfPass = action.kind==='pass' && (action.from===action.to || action.playerId===action.recipientId);
    if((action.kind==='pass' && !selfPass) || action.kind==='shot') {
      if(!owned || !action.playerId || (action.kind==='pass' && (!action.recipientId || !action.toTeam))) throw Error('Missing generated action participants');
      const pass = action.kind==='pass';
      rows.push({actionKind:action.kind,period:action.period,startMs,endMs,team:action.fromTeam,
        playerId:action.playerId,recipientId:pass?action.recipientId:null,recipientTeam:pass?action.toTeam:null,
        outcome:pass?(action.fromTeam===action.toTeam?'completed':'turnover'):'goal',fromSlotId:action.from,toSlotId:pass?action.to:null});
    }
  }
  finishControl();
  // Completion is the release point for counts; a paused cursor must not count partial actions/spells.
  rows.sort((a,b)=>a.endMs-b.endMs || a.startMs-b.startMs || (a.actionKind<b.actionKind?-1:a.actionKind>b.actionKind?1:0));
  const numbers = [0,0];
  return {version:1,fixtureId:'fixture-pl-2645236',policy:generatedActionPolicy,
    durationMs:model.duration*1000,halfMs:model.half*1000,
    rows:rows.map(row=>({recordId:`generated-action-v1-p${row.period}-${String(++numbers[row.period-1]).padStart(6,'0')}`,...row}))};
}
function exportFull(destination = path.join(root, '.local/shared-inputs/brentford-full-v5')) {
destination = path.resolve(destination);
const normalizationPolicy = 'event-context-v2;accepted-goal-participants-v1;reported-actions-separate-from-native-points;no-assist-inference';
const enrichmentPath = path.join(root, 'data/match-enrichment/brentford-chelsea-2026-09-18-event-context-v2.json');
const enrichment = JSON.parse(fs.readFileSync(enrichmentPath));
const sequencePolicy = 'shared-event-sequence-v2;accepted-goal-participants-v1;authored-layout-not-tracking;no-cut-bridge';
const sequencePath = path.join(root, 'data/match-enrichment/brentford-chelsea-2026-09-18-sequences-v2.json');
const sequences = JSON.parse(fs.readFileSync(sequencePath));
const officialPath = path.join(root, 'data/official-match-records/brentford-chelsea-2026-09-18.json');
const authoredPath = path.join(root, 'data/reconstructed-match-illustrations/brentford-chelsea-2026-09-18.json');
const modelPath = path.join(root, 'prototypes/first-experience/pl-reconstructed-model.js');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const sourceInputs = [...enrichment.sources, enrichment.unalignedTimeline];
for(const input of sourceInputs) {
  if(hash(fs.readFileSync(path.join(root,input.path))) !== input.sha256) throw Error(`Enrichment source pin changed: ${input.path}`);
}
for(const input of sequences.sources) if(hash(fs.readFileSync(path.join(root,input.path))) !== input.sha256) throw Error(`Sequence source pin changed: ${input.path}`);
const official = JSON.parse(fs.readFileSync(officialPath)), authored = JSON.parse(fs.readFileSync(authoredPath));
const model = require(modelPath).create(authored, official);
fs.mkdirSync(destination, {recursive:true});
function immutable(file, bytes) {
  const target = path.join(destination, file);
  try { fs.writeFileSync(target, bytes, {flag:'wx'}); }
  catch (error) { if(error.code !== 'EEXIST' || hash(fs.readFileSync(target)) !== hash(bytes)) throw error; }
}
const chunks = []; let frames = [], count = 0, rawBytes = 0, compressedBytes = 0;
function flush() {
  if(!frames.length) return;
  const raw = Buffer.from(JSON.stringify(frames)), compressed = zlib.gzipSync(raw, {level:9});
  const file = `frames-${String(chunks.length).padStart(6,'0')}.json.gz`;
  immutable(file, compressed);
  chunks.push({file, count:frames.length, startMs:frames[0].time*1000, endMs:frames.at(-1).time*1000,
    bytes:compressed.length, rawBytes:raw.length, sha256:hash(compressed), rawSha256:hash(raw)});
  rawBytes += raw.length; compressedBytes += compressed.length; frames = [];
}
for(let cursorMs=0; cursorMs<=5880000; cursorMs+=100) {
  const f = model.frame(cursorMs/1000);
  if(!f || f.players.length !== 22) throw Error(`Missing existing model frame ${cursorMs}`);
  frames.push({time:cursorMs/1000, clock:f.clock, period:f.period,
    players:f.players.map(p => ({slotId:p.slotId, player:{id:p.player.id}, x:p.x, y:p.y})), ball:{x:f.ball.x,y:f.ball.y}});
  count++; if(frames.length === 64) flush();
}
flush();
if(count !== 58801) throw Error('Incomplete full model export');
const eventTimings = official.events.map(e => {
  const stage = model.highlight(e.id).stage, mapped = model.frame(stage).facts.find(f => f.id === e.id);
  if(!mapped) throw Error(`Missing existing event mapping ${e.id}`);
  return {id:e.id, period:mapped.period, windowStartMs:Math.round(mapped.windowStart*1000), releaseMs:Math.round(mapped.stage*1000)};
});
const hashes = Object.fromEntries([officialPath, authoredPath, modelPath, __filename, enrichmentPath, sequencePath, ...sourceInputs.map(p=>path.join(root,p.path))].map(p => [path.relative(root,p).replaceAll('\\','/'), hash(fs.readFileSync(p))]));
hashes['normalization:event-context-v2'] = hash(Buffer.from(normalizationPolicy));
hashes['normalization:shared-event-sequence-v2'] = hash(Buffer.from(sequencePolicy));
const actionArtifact = buildGeneratedActions(model), actionBytes = Buffer.from(JSON.stringify(actionArtifact));
immutable('generated-actions.json', actionBytes);
hashes['generated-actions.json'] = hash(actionBytes);
const generatedActions = {version:1,file:'generated-actions.json',sha256:hash(actionBytes),count:actionArtifact.rows.length,policy:generatedActionPolicy};
immutable('official-record.json', fs.readFileSync(officialPath)); immutable('authored-config.json', fs.readFileSync(authoredPath));
immutable('event-enrichment.json', fs.readFileSync(enrichmentPath));
immutable('event-sequences.json', fs.readFileSync(sequencePath));
const sourceReceipts = sourceInputs.map((input,index) => {
  const file = `source-input-${String(index).padStart(3,'0')}.json`;
  immutable(file, fs.readFileSync(path.join(root,input.path)));
  return {file,sourcePath:input.path,sha256:input.sha256};
});
const manifest = {exportVersion:2, fullCoverage:true, sampling:'Complete existing generated model at 100ms, inclusive 0..5880s; authored movement, not actual observations.',
  startMs:0,endMs:5880000,cadenceMs:100,frameCount:count,rawBytes,compressedBytes,hashes,chunks,
  people:official.players,events:official.events,eventTimings,sources:official.sources,normalizationPolicy,enrichment,sourceReceipts,sequencePolicy,sequences,generatedActions,
  projectionCoverage:'All 40 roster people, generated tracking, all 14 selected reports, and model-derived illustrative passes, shots and completed team-control spells. Actual match statistics, formations and completed result remain preserved in official-record.json; generated actions are not actual measurements.'};
immutable('manifest.json', Buffer.from(JSON.stringify(manifest)));
return {destination,frames:count,chunks:chunks.length,rawBytes,compressedBytes,events:official.events.length,generatedActions,
  goals:official.events.filter(e=>e.kind==='goal').length,substitutions:official.events.filter(e=>e.kind==='substitution').length,cards:official.events.filter(e=>e.kind==='yellow_card').length,
  addedTime:eventTimings.filter(e=>official.events.find(r=>r.id===e.id).time.added_minute!==null)};
}
if(require.main===module) console.log(JSON.stringify(exportFull(process.argv[2])));
module.exports = {buildGeneratedActions,exportFull};

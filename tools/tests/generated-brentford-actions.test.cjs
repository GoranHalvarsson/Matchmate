'use strict';
const test = require('node:test'), assert = require('node:assert/strict'), fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const root = path.resolve(__dirname,'../..'), read = file => JSON.parse(fs.readFileSync(path.join(root,file)));
const official = read('data/official-match-records/brentford-chelsea-2026-09-18.json');
const authored = read('data/reconstructed-match-illustrations/brentford-chelsea-2026-09-18.json');
const model = require('../../prototypes/first-experience/pl-reconstructed-model.js').create(authored,official);
const {buildGeneratedActions} = require('../export-brentford-full.cjs'), artifact = buildGeneratedActions(model), rows = artifact.rows;
const byKind = kind => rows.filter(row=>row.actionKind===kind);
const sha = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const stage = event => (event.time.minute+(event.time.added_minute||0))*60+(event.time.minute>45?120:0);
function occupant(slotId,time) {
  let playerId = authored.slots.find(slot=>slot.id===slotId).playerId;
  for(const event of official.events.filter(event=>event.kind==='substitution').sort((a,b)=>stage(a)-stage(b)))
    if(stage(event)<=time && event.off===playerId) playerId=event.on;
  return playerId;
}
function summary(cutoffMs,team) {
  const available=rows.filter(row=>row.endMs<=cutoffMs && row.team===team), passes=available.filter(row=>row.actionKind==='pass');
  return [passes.length,passes.filter(row=>row.outcome==='completed').length,passes.filter(row=>row.outcome==='turnover').length,
    available.filter(row=>row.actionKind==='shot').length,available.filter(row=>row.actionKind==='possession').reduce((sum,row)=>sum+row.endMs-row.startMs,0)];
}
test('action API is detached and deeply frozen, including nested finish coordinates',()=>{
  const before=JSON.stringify(model.frame(3780)), shot=model.actionLedger.find(action=>action.kind==='shot');
  assert.equal(model.actionLedger.length,1472);
  assert.ok(Object.isFrozen(model.actionLedger));
  assert.ok(model.actionLedger.every(Object.isFrozen));
  assert.ok(Object.isFrozen(shot.fixed));
  assert.throws(()=>model.actionLedger.push({}),TypeError);
  assert.throws(()=>shot.start=0,TypeError);
  assert.throws(()=>shot.fixed.x=52.5,TypeError);
  assert.equal(JSON.stringify(model.frame(3780)),before);
});
test('all 58,801 full frames retain the pre-change model digest',()=>{
  // Captured from the original bytecopy, not from the new model or generated actions.
  const hash=crypto.createHash('sha256');let count=0;
  for(let ms=0;ms<=5880000;ms+=100) { hash.update(JSON.stringify(model.frame(ms/1000))+'\n');count++; }
  assert.equal(count,58801);
  assert.equal(hash.digest('hex'),'0110c507cf28a6e9d998b5284db1704c877be58d29b68cfb3481b987a1161b2c');
});
test('versioned action artifact is deterministic and contains only the agreed complete-row schema',()=>{
  assert.deepEqual(Object.keys(artifact),['version','fixtureId','policy','durationMs','halfMs','rows']);
  assert.deepEqual([artifact.version,artifact.fixtureId,artifact.policy,artifact.durationMs,artifact.halfMs],
    [1,'fixture-pl-2645236','generated-model-actions-v1',5880000,2820000]);
  assert.equal(rows.length,881);
  assert.deepEqual(['pass','shot','possession'].map(kind=>byKind(kind).length),[728,3,150]);
  assert.equal(sha(JSON.stringify(artifact)),'e790083f8482661a27a5f8f4999aa0b11cb4676e046d8346bd04a3c3d65b89b1');
  assert.deepEqual(buildGeneratedActions(model),artifact);
  assert.equal(new Set(rows.map(row=>row.recordId)).size,rows.length);
  const numbers=[0,0];
  for(const row of rows) {
    assert.deepEqual(Object.keys(row),['recordId','actionKind','period','startMs','endMs','team','playerId','recipientId','recipientTeam','outcome','fromSlotId','toSlotId']);
    assert.equal(row.recordId,`generated-action-v1-p${row.period}-${String(++numbers[row.period-1]).padStart(6,'0')}`);
    assert.ok(Number.isSafeInteger(row.startMs)&&Number.isSafeInteger(row.endMs)&&row.startMs<row.endMs);
    assert.ok(row.startMs>=(row.period===1?0:2820000)&&row.endMs<=(row.period===1?2820000:5880000));
  }
  assert.deepEqual(numbers,[423,458]);
});
test('sender identity is resolved at start and receiver at completion, including actual substitution crossings',()=>{
  let receiverChange=0,senderChange=0;
  for(const action of model.actionLedger.filter(action=>action.kind==='pass'||action.kind==='shot')) {
    assert.equal(action.playerId,occupant(action.from,action.start));
    if(action.kind==='pass') {
      assert.equal(action.recipientId,occupant(action.to,action.end));
      if(occupant(action.to,action.start)!==action.recipientId) receiverChange++;
    }
    if(occupant(action.from,action.end)!==action.playerId) senderChange++;
  }
  assert.ok(receiverChange>0,'exercise a receiver replaced during a pass');
  assert.ok(senderChange>0,'exercise a sender replaced during a pass');
  const crossing=byKind('pass').find(row=>row.startMs===5339200);
  assert.equal(crossing.playerId,'brentford:damsgaard');
  assert.equal(occupant(crossing.fromSlotId,crossing.endMs/1000),'brentford:carvalho');
});
test('passes resolve by recipient team and release only at completion; generated shots stay separate from reports',()=>{
  const passes=byKind('pass');
  assert.equal(passes.filter(row=>row.outcome==='turnover').length,145);
  for(const row of passes) {
    assert.notEqual(row.fromSlotId,row.toSlotId);assert.notEqual(row.playerId,row.recipientId);
    assert.equal(row.outcome,row.team===row.recipientTeam?'completed':'turnover');
  }
  assert.deepEqual(summary(7399,'brentford'),[0,0,0,0,0]);
  assert.deepEqual(summary(7400,'brentford'),[1,1,0,0,0]);
  assert.deepEqual(summary(37199,'brentford'),[4,4,0,0,0]);
  assert.deepEqual(summary(37200,'brentford'),[5,4,1,0,37200]);
  assert.deepEqual(byKind('shot').map(row=>[row.playerId,row.endMs,row.outcome]),[
    ['brentford:anthony',3780000,'goal'],['brentford:thiago',5100000,'goal'],['brentford:carvalho',5760000,'goal']]);
  for(const row of byKind('shot')) {
    assert.equal(row.recipientId,null);assert.equal(row.recipientTeam,null);assert.equal(row.toSlotId,null);
    assert.ok(!official.events.some(event=>event.id===row.recordId));
  }
  assert.equal(summary(3779999,'brentford')[3],0);assert.equal(summary(3780000,'brentford')[3],1);
});
test('control spells are team-only, nonoverlapping and exclude every hold/restart interval',()=>{
  const spells=byKind('possession').sort((a,b)=>a.startMs-b.startMs);
  assert.equal(spells.reduce((sum,row)=>sum+row.endMs-row.startMs,0),5850000);
  for(let i=0;i<spells.length;i++) {
    const row=spells[i];
    assert.deepEqual([row.playerId,row.recipientId,row.recipientTeam,row.fromSlotId,row.toSlotId],[null,null,null,null,null]);
    assert.equal(row.outcome,'controlled');
    if(i) assert.ok(spells[i-1].endMs<=row.startMs);
    if(i && spells[i-1].endMs===row.startMs && spells[i-1].period===row.period) assert.notEqual(spells[i-1].team,row.team);
    for(const action of model.actionLedger.filter(action=>['hold','restart'].includes(action.kind)))
      assert.ok(row.endMs<=Math.round(action.start*1000)||row.startMs>=Math.round(action.end*1000));
  }
  assert.equal(spells.filter(row=>row.endMs===2820000).length,1);
  assert.equal(spells.filter(row=>row.startMs===2820000).length,1);
  assert.deepEqual(summary(2820000,'brentford'),[177,142,35,0,1412000]);
  assert.deepEqual(summary(2820000,'chelsea'),[175,140,35,0,1408000]);
  assert.deepEqual(summary(5879999,'chelsea'),[367,293,74,0,2914700]);
  assert.deepEqual(summary(5880000,'chelsea'),[367,293,74,0,2944700]);
  assert.deepEqual(summary(5880000,'brentford'),[361,290,71,3,2905300]);
  assert.equal(spells.at(-1).endMs,5880000);
});
test('self-passes stay owned time, while unowned gaps and period changes split control',()=>{
  const action=(kind,start,end,fromTeam,from,to,playerId,recipientId,toTeam=fromTeam,period=1)=>({kind,start,end,fromTeam,from,to,playerId,recipientId,toTeam,period});
  const actions=[action('carry',0,1,'brentford','b1','b1','one','one'),
    action('pass',1,2,'brentford','b1','b1','one','one'), // Same slot.
    action('pass',2,3,'brentford','b1','b2','one','one'), // Same person.
    action('pass',3,4,'brentford','b1','c1','one','two','chelsea'),
    action('carry',4,5,'chelsea','c1','c1','two','two'),
    action('hold',5,6,null,null,null,null,null),
    action('carry',6,7,'chelsea','c1','c1','two','two'),
    action('carry',8,10,'chelsea','c1','c1','two','two'), // Explicit unowned time gap.
    action('carry',10,12,'chelsea','c1','c1','two','two','chelsea',2)];
  const sample=buildGeneratedActions({half:10,duration:20,actionLedger:actions});
  assert.deepEqual(sample.rows.filter(row=>row.actionKind==='pass').map(row=>row.outcome),['turnover']);
  assert.deepEqual(sample.rows.filter(row=>row.actionKind==='possession').map(row=>[row.period,row.team,row.startMs,row.endMs]),[
    [1,'brentford',0,4000],[1,'chelsea',4000,5000],[1,'chelsea',6000,7000],[1,'chelsea',8000,10000],[2,'chelsea',10000,12000]]);
  assert.throws(()=>buildGeneratedActions({half:10,duration:20,actionLedger:[{...actions[0],end:0}]}),/bounds/);
});

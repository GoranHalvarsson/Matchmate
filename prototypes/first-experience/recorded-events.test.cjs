const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const api=require('./recorded-events-model.js'),sample=require('./assets/tracking/1874553-60-240.json'),bundle=require('./assets/tracking/1874553-completed-events.json');
const model=api.create(sample,bundle),clone=value=>JSON.parse(JSON.stringify(value));
const ids=facts=>facts.map(e=>e.sourceId);

test('every real PP interval including one-touch intervals is absent before and eligible exactly at/after completion',()=>{
 assert.deepEqual(ids(model.eligible(240)),['8_11','8_12','8_13','8_28','8_37']);
 for(const event of bundle.annotations){
  assert.ok(!ids(model.eligible(event.end-1e-8)).includes(event.sourceId),'before '+event.sourceId);
  assert.ok(ids(model.eligible(event.end)).includes(event.sourceId),'exact '+event.sourceId);
  assert.ok(ids(model.eligible(event.end+1e-8)).includes(event.sourceId),'after '+event.sourceId);
  if(event.start<event.end)assert.ok(!ids(model.eligible(event.start)).includes(event.sourceId),'no actor/interval at start');
 }
 for(const time of [NaN,Infinity,undefined,null,'65.2',59,241])assert.deepEqual(model.eligible(time),[]);
 assert.deepEqual(model.eligible(60),[]);
});

test('source identity, period, unknown cutoff and dependency tampering are withheld',()=>{
 const e=bundle.annotations[0];
 for(const change of [{matchId:1},{pin:'other'},{period:2},{policy:'unknown'},{start:0},{end:241},{schemaVersion:2}])assert.deepEqual(api.create(sample,{...bundle,...change}).eligible(240),[]);
 const mutations=[{end:null},{end:65.19999999},{period:2},{actorId:-1},{teamId:1802},{startMatched:false},{endMatched:'True'},{timeEnd:'01:05.3'},
  {frameEnd:661},{dependency:null},{dependency:{...e.dependency,verified:false}},{dependency:{...e.dependency,clock:65.3}},
  {dependency:{...e.dependency,clock:65.1}},{dependency:{...e.dependency,frame:663}},{dependency:{...e.dependency,kind:'live-arrival'}},
  {passOutcome:'FUTURE-SENTINEL'},{playerTargetedName:'FUTURE-SENTINEL'},{dependency:{...e.dependency,secret:'FUTURE-SENTINEL'}}];
 for(const mutation of mutations){const changed={...bundle,annotations:[{...e,...mutation}]};assert.deepEqual(api.create(sample,changed).eligible(240),[],JSON.stringify(mutation));}
 const duplicate={...bundle,annotations:[e,{...e}]};assert.deepEqual(api.create(sample,duplicate).eligible(240),[]);
});

test('missing/partial/empty/nonfinite interior coverage and ambiguous tracking identity fail closed',()=>{
 for(const mutation of ['missing','partial','empty','nonfinite','duplicate-frame','duplicate-player','wrong-period','wrong-clock']){
  const changed=clone(sample),index=changed.frames.findIndex(f=>f.frameId===640),frame=changed.frames[index];
  if(mutation==='missing')changed.frames.splice(index,1);
  if(mutation==='partial')frame.players.pop();if(mutation==='empty')frame.players=[];
  if(mutation==='nonfinite')frame.players[0].x=NaN;
  if(mutation==='duplicate-frame')changed.frames.push(frame);
  if(mutation==='duplicate-player')frame.players[0]=frame.players[1];
  if(mutation==='wrong-period')frame.period=2;if(mutation==='wrong-clock')frame.clock+=.1;
  assert.ok(!ids(api.create(changed,bundle).eligible(240)).includes('8_11'),mutation);
 }
});

test('projection is immutable, source allowlisted, player isolated and rewind/gap safe',()=>{
 const copy=clone(bundle),instance=api.create(sample,copy);copy.annotations[0].end=0;
 assert.deepEqual(instance.eligible(60),[]);const event=instance.eligible(65.2)[0];assert.equal(Object.isFrozen(event),true);
 assert.equal(event.actorName,'Bart Vriends');assert.equal(event.sourceLine,56);assert.equal(event.scope,'Recorded analysis');
 assert.doesNotMatch(JSON.stringify(instance.eligible(240)),/target|passOutcome|speed|score|x_start/i);
 assert.deepEqual(ids(instance.eligible(240,809759)),['8_12']);assert.deepEqual(instance.eligible(240,-1),[]);
 assert.equal(instance.eligible(175.9).length,5);assert.equal(instance.eligible(198.8).length,5);assert.equal(instance.eligible(60).length,0);
});

test('small explicit question aliases use exactly the same eligible facts without causal or future outcomes',()=>{
 for(const q of ['What happened before this moment?','WHAT HAPPENED BEFORE THIS MOMENT!!!','what happened before now']){
  const result=model.answer(69.2,809759,q);assert.equal(result.kind,'history');assert.deepEqual(ids(result.facts),ids(model.eligible(69.2)).reverse());
  assert.match(result.text,/source-classified possession interval/);assert.doesNotMatch(result.text,/8_13|Walid|Bility|target|successful|scored/);
 }
 const player=model.answer(240,809759,'What did this player do?');assert.deepEqual(player.sourceIds,['8_12']);assert.doesNotMatch(player.text,/Bart Vriends|8_11|8_13/);
 assert.match(model.answer(60,809759,'What did this player do?').text,/limited coverage does not establish/);
 assert.match(model.answer(60,809759,'What happened before this moment?').text,/does not mean no football happened/);
 for(const q of ['Who scored next?','What was the next pass?','What was the result?','Why did he pass?','What did this player do after this moment?'])assert.equal(api.classify(q),'unsupported');
});

test('cutoff regression assertion is sensitive to removing or rounding the gate',()=>{
 const source=fs.readFileSync(require.resolve('./recorded-events-model.js'),'utf8');
 function beforeAssertion(api){const instance=api.create(sample,bundle);for(const event of bundle.annotations)assert.ok(!ids(instance.eligible(event.end-1e-8)).includes(event.sourceId),'leaked '+event.sourceId);}
 beforeAssertion(api);
 for(const replacement of ['true','e.end<=time+1e-7']){
  assert.ok(source.includes('e.end<=time'));const context={module:{exports:{}}};vm.runInNewContext(source.replace('e.end<=time',replacement),context);
  assert.throws(()=>beforeAssertion(context.module.exports),/leaked/);
 }
});

const {test}=require('node:test');
const assert=require('node:assert/strict');
const model=require('./replay-model.js');
test('rewinding never exposes later events',()=>{
  assert.match(model.explain(20,'H7'),/receives the return pass/);
  assert.doesNotMatch(model.explain(0,'H7'),/releases a pass|receives the ball|plays the ball back|receives the return pass/);
  assert.equal(model.frame(11.9).events.length,2);
  assert.equal(model.frame(12).events.length,3);
  assert.equal(model.frame(17).events.length,4);
});
test('all frames have 22 unique players and bounded coordinates',()=>{
  for(let t=0;t<=20;t+=.1){const f=model.frame(t);assert.equal(new Set(f.players.map(p=>p.id)).size,22);for(const p of [...f.players,f.ball]){assert.ok(p.x>=0&&p.x<=105);assert.ok(p.y>=0&&p.y<=68);}}
  assert.equal(model.frame(-1).time,0);assert.equal(model.frame(30).time,20);
});
test('ball remains continuous across pass and reception boundaries',()=>{
  for(const t of [6,12,17,20]){const a=model.frame(t-.0001).ball,b=model.frame(t).ball;assert.ok(Math.hypot(a.x-b.x,a.y-b.y)<.01);}
});
test('answer computes nearest opposition distance from selected frame',()=>{
  const f=model.frame(9),p=f.players.find(p=>p.id==='R4');
  const nearest=f.players.filter(q=>q.team!==p.team).map(q=>({q,d:Math.hypot(p.x-q.x,p.y-q.y)})).sort((a,b)=>a.d-b.d)[0];
  const answer=model.explain(9,'R4');assert.ok(answer.includes(nearest.q.name));assert.ok(answer.includes(nearest.d.toFixed(1)+' metres'));assert.match(answer,/64:49/);
});

test('pass totals wait for receipt and rewind exactly',()=>{
  const at=(t,id)=>model.stats(t).find(p=>p.id===id);
  assert.equal(at(6,'H6').pending,1);assert.equal(at(11.9,'H6').completed,at(0,'H6').completed);
  assert.equal(at(12,'H6').completed,at(0,'H6').completed+1);assert.equal(at(12,'H6').pending,0);
  assert.equal(at(20,'H7').completed,at(0,'H7').completed+1);assert.equal(at(0,'H7').pending,0);
  assert.deepEqual(model.stats(0),model.stats(-10));
});
test('distance follows authored movement and totals never go backwards during playback',()=>{
  for(const p of model.stats(0)){const end=model.stats(20).find(q=>q.id===p.id);assert.ok(end.distance>p.distance);assert.equal(end.missed,p.missed);assert.equal(end.tackles,p.tackles);assert.equal(end.interceptions,p.interceptions);}
  const a=model.stats(0).find(p=>p.id==='H7'),b=model.stats(20).find(p=>p.id==='H7');assert.ok(Math.abs(b.distance-a.distance-Math.hypot(14,7))<1e-8);
});
test('ball distance and trail are bounded by cursor',()=>{
  assert.equal(model.ballStats(0).distance,0);assert.equal(model.ballStats(0).passes,0);
  assert.equal(model.ballStats(6).passes,1);assert.equal(model.ballStats(17).passes,2);
  assert.ok(model.ballStats(20).distance>model.ballStats(12).distance);
  const t=10,b=model.ballStats(t);assert.deepEqual(b.trail.at(-1),model.frame(t).ball);assert.deepEqual(b.trail[0],model.frame(6).ball);
});

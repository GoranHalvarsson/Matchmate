const {test} = require('node:test');
const assert = require('node:assert/strict');
const {members,transition,filterSeasons} = require('./league-model.js');
test('season search supports start year, short and full ranges without adding seasons',()=>{
  assert.deepEqual(filterSeasons('2016'),['2016/17']);
  assert.deepEqual(filterSeasons('2016/17'),['2016/17']);
  assert.deepEqual(filterSeasons(' 2016 / 2017 '),['2016/17']);
  assert.deepEqual(filterSeasons('2015–2016'),['2015/16']);
  assert.deepEqual(filterSeasons('2026'),[]);
  assert.deepEqual(filterSeasons('<script>'),[]);
  assert.deepEqual(filterSeasons(''),['2015/16','2016/17']);
});
test('searching does not commit a season or alter club/view state',()=>{
  const state={season:'2015/16',view:'list',selected:'west-ham'};
  const matches=filterSeasons('2016');
  assert.deepEqual(state,{season:'2015/16',view:'list',selected:'west-ham'});
  const chosen=transition(state,{type:'season',value:matches[0]});
  assert.deepEqual(chosen,{season:'2016/17',view:'list',selected:'west-ham'});
  assert.equal(members(chosen.season).find(c=>c.id===chosen.selected).ground,'London Stadium');
});
test('reviewed season memberships contain twenty clubs and correct changes',()=>{
  const first=members('2015/16'),second=members('2016/17');
  for(const clubs of [first,second]) assert.equal(new Set(clubs.map(c=>c.id)).size,20);
  assert.deepEqual(first.filter(c=>!second.some(s=>s.id===c.id)).map(c=>c.id).sort(),['aston-villa','newcastle','norwich']);
  assert.deepEqual(second.filter(c=>!first.some(s=>s.id===c.id)).map(c=>c.id).sort(),['burnley','hull','middlesbrough']);
  assert.equal(new Set([...first,...second].map(c=>c.id)).size,23);
});
test('ground change, naming change and historical Tottenham ground remain distinct',()=>{
  const ground=(season,id)=>members(season).find(c=>c.id===id).ground;
  assert.equal(ground('2015/16','west-ham'),'Boleyn Ground');
  assert.equal(ground('2016/17','west-ham'),'London Stadium');
  assert.equal(ground('2015/16','stoke'),'Britannia Stadium');
  assert.equal(ground('2016/17','stoke'),'bet365 Stadium');
  assert.equal(ground('2016/17','tottenham'),'White Hart Lane');
  assert.equal(ground('2016/17','hull'),'KC Stadium');
});
test('view changes preserve shared season and selection; season changes retain only eligible clubs',()=>{
  let state={season:'2015/16',view:'map',selected:'west-ham'};
  state=transition(state,{type:'view',value:'list'});
  assert.deepEqual(state,{season:'2015/16',view:'list',selected:'west-ham'});
  state=transition(state,{type:'season',value:'2016/17'});
  assert.equal(state.selected,'west-ham');
  state=transition(state,{type:'season',value:'2015/16'});
  state=transition(state,{type:'select',value:'newcastle'});
  assert.equal(transition(state,{type:'season',value:'2016/17'}).selected,null);
  assert.equal(transition(state,{type:'select',value:'burnley'}).selected,'newcastle');
  assert.throws(()=>members('2026/27'),/Unknown season/);
});
test('illustration markers stay independently spaced, including London and northern clusters',()=>{
  for(const season of ['2015/16','2016/17']) {
    const clubs=members(season);
    for(let i=0;i<clubs.length;i++) for(let j=i+1;j<clubs.length;j++) {
      assert.ok(Math.hypot(clubs[i].x-clubs[j].x,clubs[i].y-clubs[j].y)>=40,`${clubs[i].id} overlaps ${clubs[j].id}`);
    }
  }
});

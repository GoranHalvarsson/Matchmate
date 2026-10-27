const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const vm=require('node:vm'),{buildPreview}=require('./build-preview.cjs');
const M=require('./season-table-model.js'),L=require('./league-model.js');
const clubs=['Alpha','Bravo','Charlie','Delta'].map((name,i)=>({id:String(i),name,initials:name[0]}));
const match=(team1,team2,date,h,a,round='Matchday 1')=>({team1,team2,date,round,score:{ft:[h,a]}});
const sample=()=>M.createSeason({matches:[match('Alpha','Bravo','2015-08-08',2,0),match('Charlie','Delta','2015-08-08',1,1),match('Bravo','Charlie','2015-08-09',3,1),match('Alpha','Delta','2015-08-10',0,0)]},clubs,{complete:false});
test('end-of-day batches, WDL points and backseek use only eligible played dates',()=>{
  const s=sample(),zero=M.snapshot(s,0),one=M.snapshot(s,1),two=M.snapshot(s,2);
  assert.equal(zero.completed,0);assert.ok(zero.rows.every(r=>r.p===0&&r.pts===0));
  assert.equal(one.completed,2);assert.equal(one.results.length,2);
  assert.deepEqual(one.rows.map(r=>[r.id,r.p,r.w,r.d,r.l,r.gd,r.pts,r.rank]),[['0',1,1,0,0,2,3,1],['2',1,0,1,0,0,1,2],['3',1,0,1,0,0,1,2],['1',1,0,0,1,-2,0,4]]);
  assert.deepEqual(two.rows.map(r=>r.id),['0','1','3','2']);
  M.snapshot(s,3);assert.deepEqual(M.snapshot(s,1),one,'backseek must recompute rather than retain future totals');
  assert.throws(()=>M.snapshot(s,-1));assert.throws(()=>M.snapshot(s,4));
});
test('goals scored separates equal points/GD; truly equal records share competition rank',()=>{
  const c=[{id:'a',name:'Zulu'},{id:'b',name:'Bravo'},{id:'c',name:'Alpha'},{id:'d',name:'Delta'}];
  const s=M.createSeason({matches:[match('Zulu','Alpha','2015-08-08',3,2),match('Delta','Zulu','2015-08-08',1,0),match('Bravo','Delta','2015-08-08',2,1),match('Alpha','Bravo','2015-08-08',1,0)]},c,{complete:false});
  assert.deepEqual(M.snapshot(s,1).rows.map(r=>[r.id,r.pts,r.gd,r.gf,r.rank,r.shared]),[['c',3,0,3,1,true],['a',3,0,3,1,true],['b',3,0,2,3,true],['d',3,0,2,3,true]]);
});
test('club explanation compares previous played date independent of seek history and excludes later results',()=>{
  const s=sample(),f=M.clubFacts(s,2,'1');
  assert.equal(f.pointsChange,3);assert.equal(f.positionChange,2);assert.equal(f.previousDate,'2015-08-08');
  assert.match(M.explain(s,2,'1','change'),/Up 2 positions/);
  assert.doesNotMatch(M.explain(s,1,'1','recent'),/2015-08-09|2015-08-10/);
  assert.match(M.explain(s,0,'1','recent'),/No results/);
  assert.equal(M.clubFacts(s,1,'0').positionChange,null);
});
test('validation rejects missing seasons, bad scores/dates, identity and fixture duplication',()=>{
  const good=match('Alpha','Bravo','2015-08-08',0,0);
  for(const bad of [{...good,team2:'Unknown'},{...good,team2:'Alpha'},{...good,date:'2015-02-30'},{...good,date:'08/08/2015'},{...good,score:{ft:[-1,2]}},{...good,score:{ft:[0,null]}},{...good,score:{ft:[0,1.5]}}])assert.throws(()=>M.createSeason({matches:[bad]},clubs,{complete:false}));
  assert.throws(()=>M.createSeason({matches:[good,good]},clubs,{complete:false}),/Duplicate/);
  assert.throws(()=>M.createSeason({matches:[good]},clubs),/Incomplete/);
});
test('pinned real snapshots contain all 760 unique scored fixtures and match official terminal totals',()=>{
  const manifest=require('./assets/seasons/manifest.json');
  const expected={'2015/16':[['leicester',81,68,36],['arsenal',71,65,36]],'2016/17':[['chelsea',93,85,33],['tottenham',86,86,26]]};
  for(const [label,record] of Object.entries(manifest.seasons)){
    const bytes=fs.readFileSync(path.join(__dirname,'assets/seasons',record.file));
    assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'),record.sha256);
    const s=M.createSeason(JSON.parse(bytes),L.members(label)),end=M.snapshot(s,s.dates.length);
    assert.equal(s.matches.length,380);assert.equal(new Set(s.matches.map(m=>m.id)).size,380);assert.equal(end.rows.length,20);assert.ok(end.rows.every(r=>r.p===38));
    assert.equal(s.dates[0],record.first_played_date);assert.equal(s.dates.at(-1),record.last_played_date);
    assert.deepEqual(end.rows.slice(0,2).map(r=>[r.id,r.pts,r.gf,r.ga]),expected[label]);
    assert.equal(end.rows.reduce((n,r)=>n+r.gf,0),label==='2015/16'?1026:1064);
  }
});
test('postponed fixtures enter on actual played date rather than round number or original final Sunday',()=>{
  const first=M.createSeason(require('./assets/seasons/2015-16.json'),L.members('2015/16'));
  const before=M.snapshot(first,first.dates.indexOf('2016-05-15')+1),after=M.snapshot(first,first.dates.length);
  assert.equal(before.rows.find(r=>r.id==='manchester-united').p,37);assert.equal(before.rows.find(r=>r.id==='manchester-united').pts,63);
  assert.equal(after.rows.find(r=>r.id==='manchester-united').pts,66);
  assert.deepEqual(after.results.map(r=>[r.date,r.home,r.away,r.hg,r.ag]),[['2016-05-17','manchester-united','bournemouth',3,1]]);
  const next=M.createSeason(require('./assets/seasons/2016-17.json'),L.members('2016/17'));
  const may17=M.snapshot(next,next.dates.indexOf('2017-05-17')+1),may18=M.snapshot(next,next.dates.indexOf('2017-05-18')+1);
  assert.equal(may17.rows.find(r=>r.id==='tottenham').pts,80);assert.equal(may18.rows.find(r=>r.id==='tottenham').pts,83);
  assert.deepEqual(may18.results.map(r=>[r.round,r.home,r.away,r.hg,r.ag]),[['Matchday 34','leicester','tottenham',1,6]]);
});
test('generated browser data and portable scripts contain the exact pinned snapshot trees',()=>{
  const script=fs.readFileSync(path.join(__dirname,'season-results.js'),'utf8'),context={window:{}};
  assert.equal(script,buildPreview().seasonScript);
  vm.runInNewContext(script,context);
  for(const [label,file] of [['2015/16','2015-16'],['2016/17','2016-17']])assert.deepEqual(JSON.parse(JSON.stringify(context.window.SeasonResults[label])),require('./assets/seasons/'+file+'.json'));
  const portable=fs.readFileSync(path.join(__dirname,'preview.html'),'utf8');
  assert.ok(portable.includes(script));assert.ok(portable.includes(fs.readFileSync(path.join(__dirname,'season-table-model.js'),'utf8')));
  for(const file of ['pl-timeline-sample.js','pl-timeline-model.js','pl-timeline.js','pl-goal-sample.js','pl-goal-model.js','pl-goal-pitch.js','pl-official-sample.js','pl-official-model.js','pl-official-view.js'])assert.ok(portable.includes(fs.readFileSync(path.join(__dirname,file),'utf8')),'Portable timeline module missing: '+file);
  const scripts=[...portable.matchAll(/<script>([\s\S]*?)<\/script>/g)];assert.equal(scripts.length,41);for(const s of scripts)new vm.Script(s[1]);
});

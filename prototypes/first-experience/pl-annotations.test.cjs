const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),crypto=require('node:crypto');
const build=require('./build-preview.cjs').buildPreview(),sample=build.plSample,api=require('./pl-annotations-model.js'),model=api.create(sample),MatchWorkspace=require('./match-workspace.js');
const read=name=>fs.readFileSync(require.resolve('./'+name),'utf8'),clone=o=>JSON.parse(JSON.stringify(o));
const anthony='brentford:anthony',damsgaard='brentford:damsgaard';

test('PL runtime projection is allowlisted and leaves the approved source artifact/report claims separate',()=>{
 const body=fs.readFileSync(require.resolve('../../data/manual-match-annotations/brentford-chelsea-2026-09-18-first-goal.json'));
 assert.equal(crypto.createHash('sha256').update(body).digest('hex'),sample.source.artifact.sha256);assert.equal(body.length,sample.source.artifact.bytes);
 assert.equal(sample.captures.length,16);assert.equal(sample.estimates.length,4);assert.equal(sample.identities.length,2);
 assert.equal(sample.captures[0].id,'E02');assert.equal(sample.captures[11].id,'E01');assert.equal(sample.policy.reportsIncluded,false);
 assert.doesNotMatch(JSON.stringify(sample),/retrospectiveReportClaims|Schuster|bodyPart|sourceLocator|"note"|"image"|61′|3–0/);
 assert.equal(sample.cuts.length,4);assert.equal(sample.gaps.length,15);
});

test('every exact capture alone supplies its clock/score and point estimates; gaps never round or hold',()=>{
 for(const point of sample.captures){
  assert.equal(model.view(point.time,anthony).point.id,point.id);
  for(const delta of [-1e-8,1e-8]){const view=model.view(point.time+delta,anthony);assert.equal(view.point,null);assert.equal(view.position,null);}
 }
 for(const p of sample.estimates){const player=sample.identities.find(i=>i.id===p.identityId).playerId;assert.equal(model.view(p.time,player).position.evidenceId,p.evidenceId);assert.equal(model.view(p.time+1e-8,player).position,null);}
 assert.equal(model.view(3719,anthony).position,null);assert.equal(model.view(3720,anthony).position,null);assert.equal(model.view(3712,anthony).position,null);assert.equal(model.view(3712,damsgaard).position.zone,'corner_boundary');
 assert.equal(model.view(3730,anthony).point.score.join('–'),'1–0');assert.equal(model.view(3729.99,anthony).point,null);
 assert.equal(model.adjacent(3717,1),3717.22);assert.equal(model.adjacent(3717.22,-1),3717);assert.equal(model.adjacent(3717.21,1),3717.22);
 assert.equal(model.view(model.gap(model.start),anthony).point,null);
 for(const value of [NaN,Infinity,null,'3712',true,3700,3740])assert.equal(model.view(value,anthony).point,null);
});

test('all observation cutoffs use the latest support with exact boundaries and no future payload',()=>{
 for(const o of sample.observations){assert.ok(!model.eligible(o.availableAt-1e-8).some(f=>f.id===o.id));assert.ok(model.eligible(o.availableAt).some(f=>f.id===o.id));if(o.availableAt<model.end)assert.ok(model.eligible(o.availableAt+1e-8).some(f=>f.id===o.id));}
 assert.ok(!model.eligible(3714-1e-8).some(o=>o.kind==='corner_setup'));assert.ok(model.eligible(3714).some(o=>o.kind==='corner_setup'));
 assert.ok(!JSON.stringify(model.eligible(3730-1e-8)).includes('first_captured_one_nil'));
 assert.equal(model.eligible(3720).find(o=>o.kind==='anonymous_celebration').actor,null);
 const before=JSON.stringify(model.eligible(3712-1e-8));assert.doesNotMatch(before,/Damsgaard|corner_setup|O05|O06|O12|1–0/);
 assert.doesNotMatch(JSON.stringify(model.eligible(3730)),/Schuster|reported_goal|assistPlayerId|"note"/);
 assert.equal(model.view(3715.5,anthony).observations.some(o=>o.kind==='corner_setup'),true,'eligible observations survive a position gap');
});

test('invalid identities, source clocks, extras, dependencies and spatial fields fail closed',()=>{
 const mutations=[s=>s.fixture.officialMatchId=1,s=>s.captures[0].clock='61:00',s=>s.captures[0].time+=.01,s=>s.captures[0].score=[3,0],s=>s.policy.reportsIncluded=true,s=>s.identities[0].shirt=44,s=>s.observations[5].availableAt=3712,s=>s.observations[5].availableAt=null,s=>s.observations[10].identityId='I19',s=>s.estimates[0].x=1,s=>s.estimates[0].time+=.01,s=>s.estimates[0].evidenceId='E15',s=>s.observations[0].future='FUTURE_SENTINEL',s=>s.captures[0].target='FUTURE_SENTINEL',s=>s.gaps[0].bounds[1]=3709];
 for(const mutate of mutations){const value=clone(sample);mutate(value);assert.throws(()=>api.create(value),/PL annotations:/);}
 const value=clone(sample),instance=api.create(value);value.observations[5].availableAt=0;assert.ok(!instance.eligible(3712).some(o=>o.kind==='corner_setup'),'caller cannot mutate validated snapshot');
});

test('finite local question aliases share model facts and reject scorer/outcome substring traps',()=>{
 assert.equal(api.classify('WHAT HAS BEEN OBSERVED!!!'),'observed');assert.equal(api.classify('Where is this player?'),'position');
 for(const q of ['Who scored before this moment?','Who assisted?','What has been observed about the scorer?','Where is this player going next?','What was the result?','Explain the tactics'])assert.equal(model.answer(3730,anthony,q).kind,'unsupported');
 const early=model.answer(3713,anthony,'What has been observed?');assert.doesNotMatch(early.text,/Corner setup|1–0|group celebration/);
 assert.match(model.answer(3717.22,anthony,'Where is this player?').text,/Schematic region only; side and exact location are unknown/);
 assert.match(model.answer(3717.21,anthony,'Where is this player?').text,/position unknown/);
 assert.match(model.answer(3717.21,anthony,'What does the scoreboard show?').text,/Scoreboard unknown/);
 assert.match(model.answer(3730,anthony,'What does the scoreboard show?').text,/not an exact goal time/);
 assert.match(model.answer(3712,anthony,'What is missing here?').text,/Ball position/);
});

test('meaningful boundary and gap assertions fail when cutoff or point guard is weakened',()=>{
 const source=read('pl-annotations-model.js');
 const assertions=[['o.availableAt<=time','true',m=>assert.ok(!m.eligible(3714-1e-8).some(o=>o.kind==='corner_setup'))],['data.captures.find(c=>c.time===time)','data.captures.find(c=>c.time<=time)',m=>assert.equal(m.view(3717.21,anthony).point,null)]];
 for(const [before,after,check] of assertions){check(model);assert.ok(source.includes(before));const context={module:{exports:{}}};vm.runInNewContext(source.replace(before,after),context);assert.throws(()=>check(context.module.exports.create(sample)),assert.AssertionError);}
});

function controller(){
 const nodes=new Map(),docListeners={},windowListeners={},timers=new Map();let document,id=0;
 function node(selector){if(nodes.has(selector))return nodes.get(selector);let html='',writes=0;const listeners={},n={id:selector.startsWith('#')?selector.slice(1):'',value:'',textContent:'',hidden:false,dataset:{},attributes:{},listeners,get innerHTML(){return html;},set innerHTML(value){html=value;writes++;},get htmlWrites(){return writes;},querySelector:node,setAttribute(k,v){this.attributes[k]=v;},addEventListener(type,fn){listeners[type]=fn;},focus(){document.activeElement=this;},replaceChildren(){this.innerHTML='';},insertAdjacentHTML(where,value){this.innerHTML+=value;},closest:s=>s==='button'?n:null};nodes.set(selector,n);return n;}
 document={hidden:false,querySelector:node,addEventListener(type,fn){docListeners[type]=fn;}};
 const window={PLAnnotationsSample:sample,PLAnnotationsModel:api,addEventListener(type,fn){windowListeners[type]=fn;}};
 vm.runInNewContext(read('pl-annotations.js'),{window,document,MatchWorkspace,setInterval(fn){timers.set(++id,fn);return id;},clearInterval(id){timers.delete(id);}});
 return {node,window,document,docListeners,windowListeners,timers,seek(value){node('#pl-range').listeners.input({target:{value}});},select(value){node('#pl-player').listeners.change({target:{value}});},ask(value,typed=false){if(typed){node('#pl-draft').value=value;node('#pl-composer').listeners.submit({preventDefault(){}});}else this.click('question',{plQuestion:value});},click(id,dataset={}){const target=node('#'+id);target.dataset=dataset;node('#pl-workspace').listeners.click({target});},tick(){for(const fn of [...timers.values()])fn();}};
}

test('actual PL controller clears all gap regions/clocks/score, reconciles notes and protects Sources',()=>{
 const app=controller();let returned=0;app.window.PLAnnotations.open(()=>returned++);
 assert.equal(app.node('#pl-goalmouth').dataset.active,'true');assert.equal(app.node('#pl-match-clock').textContent,'60:14');assert.equal(app.node('#pl-score').textContent,'0–0');
 app.seek(3707.5);assert.equal(app.node('#pl-goalmouth').dataset.active,'false');assert.equal(app.node('#pl-corner').dataset.active,'false');assert.match(app.node('#pl-score').textContent,/Unknown/);assert.match(app.node('#pl-match-clock').textContent,/Unknown/);assert.match(app.node('#pl-position').textContent,/position unknown/);
 app.ask('What does the scoreboard show?',true);assert.match(app.node('#pl-thread').innerHTML,/Scoreboard unknown/);assert.match(app.node('#pl-thread').innerHTML,/MATCH CLOCK UNKNOWN/);
 app.seek(3712);assert.equal(app.node('#pl-thread').innerHTML,'');app.select(damsgaard);assert.equal(app.node('#pl-corner').dataset.active,'true');app.ask('Where is this player?');assert.match(app.node('#pl-thread').innerHTML,/Mikkel Damsgaard/);assert.match(app.node('#pl-thread').innerHTML,/E07/);
 app.select(anthony);assert.equal(app.node('#pl-thread').innerHTML,'');assert.equal(app.node('#pl-corner').dataset.active,'false');
 app.seek(3714-1e-8);assert.doesNotMatch(app.node('#pl-facts').innerHTML,/O06|Corner setup|O12|1–0/);app.seek(3714);assert.match(app.node('#pl-facts').innerHTML,/Corner setup/);
 const sources=app.node('#pl-context').innerHTML;assert.doesNotMatch(sources,/O06|O12|E16|Schuster|61′|"captures"|"observations"/);assert.match(sources,/16 paused source captures/);
 app.seek(3730);assert.equal(app.node('#pl-score').textContent,'1–0');app.ask('Who scored before this moment?',true);assert.match(app.node('#pl-thread').innerHTML,/unsupported/);
 app.seek(3717.21);assert.equal(app.node('#pl-thread').innerHTML,'');assert.equal(app.node('#pl-goalmouth').dataset.active,'false');app.click('pl-next');assert.equal(app.node('#pl-range').value,'3717.22');assert.equal(app.node('#pl-goalmouth').dataset.active,'true');
 app.click('pl-back');assert.equal(returned,1);assert.equal(app.window.PLAnnotations.isOpen(),false);assert.equal(app.node('#pl-thread').innerHTML,'');
});

test('capture playback uses exact reviewed instants, pauses for questions/hide/end, preserves controls and escapes text',()=>{
 const app=controller();app.window.PLAnnotations.open(()=>{});const range=app.node('#pl-range'),writes=app.node('#pl-workspace').htmlWrites,seen=[];
 app.click('pl-play');assert.equal(app.timers.size,1);for(let i=0;i<20;i++){app.tick();seen.push(app.node('#pl-range').value);}assert.ok(seen.includes('3717.22'));assert.equal(app.node('#pl-range').value,'3730');assert.equal(app.timers.size,0);assert.equal(app.node('#pl-range'),range);assert.equal(app.node('#pl-workspace').htmlWrites,writes);
 app.seek(3707.22);app.click('pl-play');app.ask('What has been observed?');assert.equal(app.timers.size,0);app.click('pl-play');app.document.hidden=true;app.docListeners.visibilitychange();assert.equal(app.timers.size,0);
 app.click('pl-play');app.windowListeners.pagehide();assert.equal(app.timers.size,0);app.click('pl-play');app.docListeners['matchmate:workspace-exit']();assert.equal(app.timers.size,0);
 app.ask('<img src=x onerror=alert(1)>',true);assert.doesNotMatch(app.node('#pl-thread').innerHTML,/<img/);assert.match(app.node('#pl-thread').innerHTML,/&lt;img/);
});

test('actual league PL entry preserves season DOM/date/selection and closes on navigation with entry focus restored',()=>{
 const nodes=new Map(),docListeners={},timers=new Map(),events=[];let document,id=0;
 function node(selector){if(nodes.has(selector))return nodes.get(selector);const listeners={},n={id:selector.startsWith('#')?selector.slice(1):'',hidden:false,innerHTML:'',value:'',textContent:'',dataset:{},attributes:{},style:{},classList:{toggle(){}},children:[],append(){},addEventListener(t,fn){listeners[t]=fn;},setAttribute(k,v){this.attributes[k]=v;},removeAttribute(k){delete this.attributes[k];},querySelectorAll(){return [];},querySelector:node,focus(){document.activeElement=this;},listeners,matches:s=>selector===s,closest(s){return s.split(',').some(x=>x.trim()===selector)?n:null;}};nodes.set(selector,n);return n;}
 document={querySelector:node,addEventListener(t,fn){(docListeners[t]??=[]).push(fn);},dispatchEvent(e){events.push(e.type);for(const fn of docListeners[e.type]||[])fn(e);}};
 const pl={opened:false,open(back){this.opened=true;this.back=back;},close(){this.opened=false;this.back();},isOpen(){return this.opened;}};
 const recorded={isOpen:()=>false},window={scrollY:73,scrollTo(x,y){this.returnY=y;},addEventListener(){}};
 vm.runInNewContext(read('league.js'),{window,document,Event:class{constructor(type){this.type=type;}},PLAnnotations:pl,RecordedReplay:recorded,LeagueModel:require('./league-model.js'),MatchWorkspace,HistoricalMatchModel:require('./historical-match-model.js'),SeasonTableModel:require('./season-table-model.js'),SeasonResults:{'2015/16':require('./assets/seasons/2015-16.json'),'2016/17':require('./assets/seasons/2016-17.json')},setInterval(fn){timers.set(++id,fn);return id;},clearInterval(id){timers.delete(id);}});
 const workspace=node('#league-workspace'),range=node('#season-range');function click(selector){const e={target:node(selector)};workspace.listeners.click(e);for(const fn of docListeners.click||[])fn(e);}
 range.value='6';workspace.listeners.input({target:range});const html=workspace.innerHTML;click('#season-play');assert.equal(timers.size,1);click('#try-pl-annotations');assert.equal(timers.size,0);assert.ok(events.includes('matchmate:workspace-exit'));assert.equal(pl.opened,true);assert.equal(workspace.hidden,true);pl.close();assert.equal(workspace.innerHTML,html);assert.equal(Number(range.value),6);assert.equal(document.activeElement,node('#try-pl-annotations'));assert.equal(window.returnY,73);
 click('#try-pl-annotations');node('#open-league').listeners.click();assert.equal(pl.opened,false);assert.equal(workspace.hidden,false);
 click('#try-pl-annotations');click('.wordmark');assert.equal(pl.opened,false);assert.equal(workspace.hidden,true);assert.equal(node('main').hidden,false);
});

test('modular and portable adapters are exact and runtime performs no fetch or report/media loading',()=>{
 assert.equal(read('preview.html'),build.html);assert.equal(read('pl-annotations-sample.js'),build.plScript);const context={window:{}};vm.runInNewContext(build.plScript,context);assert.deepEqual(JSON.parse(JSON.stringify(context.window.PLAnnotationsSample)),sample);
 assert.equal(read('recorded-sample.js'),build.recordedScript);assert.equal(read('season-results.js'),build.seasonScript);
 const index=read('index.html');assert.ok(index.indexOf('pl-annotations-sample.js')<index.indexOf('pl-annotations-model.js'));assert.ok(index.indexOf('pl-annotations-model.js')<index.indexOf('pl-annotations.js'));assert.ok(index.indexOf('pl-annotations.js')<index.indexOf('league.js'));
 assert.match(read('league.js'),/id="try-pl-annotations">Brentford–Chelsea · On the pitch/);
 assert.doesNotMatch(read('pl-annotations.js'),/fetch\(|XMLHttpRequest|WebSocket|RecordedReplayModel|<img|<video/);
 assert.doesNotMatch(read('pl-annotations-sample.js'),/retrospectiveReportClaims|Schuster|bodyPart|\.jpg|"note"/);
});

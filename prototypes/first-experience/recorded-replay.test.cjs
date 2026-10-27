const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm'),crypto=require('node:crypto');
const {create,quality}=require('./recorded-replay-model.js'),MatchWorkspace=require('./match-workspace.js');
const sample=require('./assets/tracking/1874553-60-240.json'),real=create(sample);
const read=file=>fs.readFileSync(require.resolve('./'+file),'utf8');
const point=(playerId,x=0,y=0,isDetected=true)=>({playerId,x,y,isDetected});
function fixture(frames,end=.5){return {start:0,end,cadence:.1,pitch:{length:105,width:68,homeTeamSide:['left_to_right']},home:{id:1,name:'Unit home'},away:{id:2,name:'Unit away'},players:[{id:11,teamId:1,name:'Unit one',number:9},{id:22,teamId:2,name:'Unit two',number:4}],frames:frames.map(([clock,players,ball={x:1,y:2,isDetected:true}],i)=>({clock,players,ball,frameId:i,period:1,sourceTimestamp:String(clock)}))};}

test('actual passage keeps exact counts, identities, raw quality and both position gaps',()=>{
 assert.equal(sample.frames.length,1801);assert.equal(sample.players.length,22);
 assert.equal(sample.frames[0].frameId,610);assert.equal(sample.frames.at(-1).frameId,2410);
 assert.equal(real.at(60).count,22);assert.equal(real.at(175.9).count,0);assert.equal(real.at(189.9).count,22);assert.equal(real.at(198.8).count,0);assert.equal(real.at(219.6).count,22);
 assert.deepEqual(real.gaps.map(g=>[g.start,g.end,g.slots]),[[175.9,189.8,140],[198.8,219.5,208]]);
 assert.deepEqual(real.coverage.filter(c=>c.value==='empty-positions').map(c=>c.slots),[140,208]);
 const observations=sample.frames.flatMap(f=>f.players);assert.equal(observations.filter(p=>p.isDetected===true).length,15974);assert.equal(observations.filter(p=>p.isDetected===false).length,15992);
 for(const f of sample.frames.filter(f=>f.players.length))assert.deepEqual(new Set(f.players.map(p=>p.playerId)),new Set(sample.players.map(p=>p.id)));
 assert.equal(quality(null),'unknown');assert.equal(quality('true'),'unknown');
});

test('all slots respect preceding sample only within its own tenth, never a future or stale point',()=>{
 for(let i=0;i<sample.frames.length-1;i++){
  const f=sample.frames[i],next=sample.frames[i+1];
  assert.equal(real.at(f.clock).frame?.frameId,f.frameId);assert.equal(real.at(next.clock-1e-8).frame?.frameId,f.frameId);assert.equal(real.at(next.clock).frame?.frameId,next.frameId);
 }
 const model=create(fixture([[0,[point(11),point(22)]],[.1,[point(11),point(22)]],[.3,[point(11),point(22)]]],.3));
 assert.equal(model.at(.2).frame,null);assert.equal(model.at(.29999999).frame,null);assert.equal(model.at(.3).count,2);
 for(const value of [-1,.3001,NaN,Infinity])assert.equal(model.at(value).count,0);
 assert.equal(real.at(240).count,22);assert.equal(real.at(240.00001).count,0);
});

test('partial player loss, ball loss and unknown quality remain independent',()=>{
 const model=create(fixture([[0,[point(11),point(22)],{}],[.1,[point(11,1,2,false)]],[.2,[point(11,2,3,null),point(22,NaN,2)]],[.3,[],{x:4,y:5,isDetected:null}],[.5,[point(11),point(22)]]]));
 assert.equal(model.at(0).ball.available,false);assert.equal(model.at(0).fullGap,false);
 assert.equal(model.at(.1).frameState,'partial-positions');assert.equal(model.at(.1).players[0].quality,'estimated');assert.equal(model.at(.1).players[1].quality,'missing');
 assert.equal(model.at(.2).players[0].quality,'unknown');assert.equal(model.at(.2).players[1].available,false);
 assert.equal(model.at(.3).fullGap,true);assert.equal(model.at(.3).ball.available,true);assert.equal(model.at(.3).ball.quality,'unknown');
 assert.equal(model.at(.4).frameState,'missing-frame');assert.equal(model.at(.3).frameState,'empty-positions');
 assert.equal(model.advance(model.play(model.seek(0)),.2).playing,true);
 assert.equal(model.lastKnown(.3,22).time,0);
 assert.match(model.explain(.3,22),/Unit two.*position unavailable/s);assert.match(model.explain(.3,22),/does not establish a substitution/);
});

test('large clock jumps pause on first gap; continue exempts only the current contiguous gap',()=>{
 let state=real.advance(real.play(real.seek(60)),180);assert.equal(state.time,175.9);assert.equal(state.playing,false);
 assert.equal(real.play(state).playing,false);
 state=real.advance(real.continueGap(state),180);assert.equal(state.time,198.8);assert.equal(state.playing,false);
 state=real.advance(real.continueGap(state),180);assert.equal(state.time,240);assert.equal(state.playing,false);
 assert.equal(real.play(state).playing,false);
 assert.equal(real.adjacent(175.9,-1),175.8);assert.equal(real.adjacent(175.9,1),189.9);
 assert.equal(real.adjacent(198.8,-1),198.7);assert.equal(real.adjacent(198.8,1),219.6);
 const continued=real.continueGap(real.seek(175.9));assert.equal(real.advance(continued,14).time,189.9);assert.equal(real.advance(continued,14).continuedGap,null);
 state=real.seek(70);assert.equal(state.playing,false);assert.equal(state.continuedGap,null);
 const endGap=create(fixture([[0,[point(11),point(22)]],[.1,[]]],.2));assert.equal(endGap.adjacent(.1,1),null);assert.equal(endGap.adjacent(.1,-1),0);assert.equal(endGap.advance(endGap.continueGap(endGap.seek(.1)),50).time,.2);
});

test('answers and projection preserve raw out-of-bounds points without later events or statistics',()=>{
 const model=create(fixture([[0,[point(11,-54,37,false),point(22)]],[.1,[]],[.2,[point(11,10,12),point(22)]]],.2));
 const p=model.at(0).players[0];assert.equal(p.x,-54);assert.equal(p.y,37);assert.equal(p.outOfBounds,true);assert.ok(model.project(p).left>0);assert.ok(model.project(p).top>0);
 assert.match(model.explain(0,11),/source estimated/);assert.match(model.explain(0,11),/x -54 m, y 37 m/);assert.doesNotMatch(model.explain(0,11),/x 10 m/);
 assert.match(model.explain(.1,11),/position unavailable/);assert.equal(model.lastKnown(.1,11).x,-54);assert.equal(model.lastKnown(0,22).time,0);
 assert.equal(model.at(.1).players.find(p=>p.id===11).id,11);
});

test('recorded workspace reuses common shell and isolates source identities, quality and coverage markup',()=>{
 const shell=MatchWorkspace.shell('recorded'),pitch=MatchWorkspace.pitch({model:real,recorded:true});
 assert.match(shell,/id="recorded-composer"/);assert.doesNotMatch(shell,/id="historical-|id="composer"|recorded-mic/);
 assert.match(pitch,/data-recorded-player="23925"/);assert.doesNotMatch(pitch,/data-player="H|Metres|km\/h|Pass ✓|data-replay-time/);
 for(const text of ['Source roster','Dotted square','Dashed circle','Continue clock through gap','Retrospective, source-derived','01:00–04:00','+x right, +y up'])assert.ok(pitch.includes(text),text);
 assert.match(read('league.js'),/id="try-recorded">Recorded sample/);assert.match(read('replay.js'),/matchmate:workspace-exit/);
 const css=read('style.css');assert.match(css,/\.recorded-marker\[data-quality=estimated\].*border-style:dashed/);assert.match(css,/\.recorded-marker\[data-quality=unknown\].*border-style:dotted/);assert.match(css,/@media\(max-width:1099px\).*recorded-replay \.replay-grid\{display:block\}/s);
 assert.doesNotMatch(css,/\.league-heading \.league-demo\{display:none\}/,'mobile entry buttons must not be hidden');
 assert.match(css,/\.league-heading \.league-demo\{display:flex;flex-wrap:wrap;[^}]*width:100%/,'mobile entries retain full-width wrapped content');
});

// Event-target unit checks use the same VM/node-double pattern as the existing
// prototype tests. They are not browser, layout or assistive-technology evidence.
function controller(eventBundle=require('./assets/tracking/1874553-completed-events.json'),eventManifest=require('./assets/tracking/completed-events-manifest.json')){
 const nodes=new Map(),timers=new Map(),docListeners={},windowListeners={};let now=0,nextTimer=0,document;
 function node(selector){if(nodes.has(selector))return nodes.get(selector);let content='',writes=0;const listeners={};const n={id:selector.startsWith('#')?selector.slice(1):'',innerHTML:'',hidden:false,value:'',dataset:{},style:{},attributes:{},classList:{toggle(){}},listeners,children:[],append(child){this.children.push(child);child.parentNode=this;},
  get textContent(){return content;},set textContent(value){content=value;writes++;},get textWrites(){return writes;},
  querySelector:s=>node(s==='button'?selector+' button':s),querySelectorAll:s=>s==='[data-recorded-team]'?[node('team-home'),node('team-away')]:[],setAttribute(k,v){this.attributes[k]=v;},
  addEventListener(type,fn){listeners[type]=fn;},focus(){document.activeElement=this;},contains(el){return selector==='#recorded-event-list'&&!!el?.id?.startsWith('recorded-event-');},replaceChildren(){this.innerHTML='';},insertAdjacentHTML(where,html){this.innerHTML+=html;},closest:s=>s==='button'?n:null};nodes.set(selector,n);return n;}
 document={hidden:false,activeElement:null,querySelector:node,addEventListener(type,fn){docListeners[type]=fn;}};
 const window={RecordedSample:{sample,manifest:require('./assets/tracking/manifest.json'),license:read('assets/tracking/SKILLCORNER-LICENSE.txt'),events:eventBundle,eventManifest},RecordedReplayModel:require('./recorded-replay-model.js'),RecordedEventsModel:require('./recorded-events-model.js'),addEventListener(type,fn){windowListeners[type]=fn;}};
 vm.runInNewContext(read('recorded-replay.js'),{window,document,MatchWorkspace,performance:{now:()=>now},setInterval(fn){timers.set(++nextTimer,fn);return nextTimer;},clearInterval(id){timers.delete(id);}});
 function click(id,dataset={}){const target=node('#'+id);target.dataset=dataset;node('#recorded-workspace').listeners.click({target});}
 function seek(value){node('#recorded-range').listeners.input({target:{value}});}
 return {node,window,document,docListeners,timers,click,seek,tick(seconds){now+=seconds*1000;for(const fn of [...timers.values()])fn();}};
}

test('actual recorded controller keeps selection/focus through gaps, timestamps answers and rejects unsupported questions',()=>{
 const app=controller();let returned=0;app.window.RecordedReplay.open(()=>returned++);
 assert.equal(app.node('.recorded-timeline').parentNode,app.node('.pitch-panel'),'timeline follows the pitch before the sibling roster in DOM and keyboard order');
 assert.deepEqual(app.node('.pitch-panel').children,[app.node('.recorded-timeline'),app.node('.recorded-events')],'completed annotations follow controls and precede roster without rebuilding controls');
 app.click('recorded-player-23925',{recordedPlayer:'23925'});app.click('team-home',{recordedTeam:'home'});app.node('#recorded-player-23925').focus();
 app.seek(175.9);assert.equal(app.node('#recorded-player-23925').hidden,true);assert.equal(app.document.activeElement,app.node('#recorded-row-23925 button'));assert.equal(app.node('#recorded-player').value,'23925');
 assert.equal(app.node('#recorded-row-23925').hidden,false);
 app.click('recorded-ask-moment');assert.match(app.node('#recorded-thread').innerHTML,/Ryan Kitto/);assert.match(app.node('#recorded-thread').innerHTML,/02:55.9/);assert.match(app.node('#recorded-thread').innerHTML,/position unavailable/);
 app.node('#recorded-draft').value='Who scored next?';app.node('#recorded-composer').listeners.submit({preventDefault(){}});assert.match(app.node('#recorded-thread').innerHTML,/does not interpret other free-text/);
 app.seek(60);assert.equal(app.node('#recorded-thread').innerHTML,'');assert.equal(app.node('#recorded-player-23925').hidden,false);assert.equal(app.node('#recorded-player').value,'23925');
 app.click('recorded-back');assert.equal(returned,1);assert.equal(app.window.RecordedReplay.isOpen(),false);assert.equal(app.node('#recorded-workspace').hidden,true);
});

test('actual controller timer catches crossed gaps, does not replace focused nodes or announce every tick, stops on hide',()=>{
 const app=controller();app.window.RecordedReplay.open(()=>{});const range=app.node('#recorded-range'),row=app.node('#recorded-row-23925 button');
 app.click('recorded-play');const announcements=app.node('#recorded-announcement').textWrites;for(let i=0;i<10;i++)app.tick(.1);
 assert.equal(app.node('#recorded-announcement').textWrites,announcements);assert.equal(app.node('#recorded-range'),range);assert.equal(app.node('#recorded-row-23925 button'),row);
 app.tick(180);assert.equal(Number(range.value),175.9);assert.equal(app.timers.size,0);assert.equal(app.node('#recorded-gap-actions').hidden,false);
 app.click('recorded-continue');assert.equal(app.node('#recorded-play').disabled,false);assert.equal(app.node('#recorded-play').textContent,'Ⅱ Pause');app.click('recorded-play');assert.equal(app.timers.size,0);
 assert.equal(app.node('#recorded-play').disabled,true);assert.equal(app.node('#recorded-play').textContent,'▶ Play');assert.equal(app.node('#recorded-play').attributes['aria-pressed'],'false');assert.equal(app.node('#recorded-continue').disabled,false);
 app.click('recorded-continue');assert.equal(app.timers.size,1);assert.equal(app.node('#recorded-play').disabled,false);assert.equal(app.node('#recorded-play').textContent,'Ⅱ Pause');app.tick(180);assert.equal(Number(range.value),198.8);assert.equal(app.timers.size,0);
 app.click('recorded-next');assert.equal(Number(range.value),219.6);assert.equal(app.document.activeElement,range);
 app.click('recorded-play');app.document.hidden=true;app.docListeners.visibilitychange();assert.equal(app.timers.size,0);
});

test('portable build embeds exact normalized sample, provenance and complete MIT notice with zero runtime fetches',()=>{
 const build=require('./build-preview.cjs').buildPreview(),context={window:{}};
 assert.equal(read('recorded-sample.js'),build.recordedScript);vm.runInNewContext(build.recordedScript,context);
 assert.deepEqual(JSON.parse(JSON.stringify(context.window.RecordedSample.sample)),sample);assert.equal(context.window.RecordedSample.license,read('assets/tracking/SKILLCORNER-LICENSE.txt'));
 assert.deepEqual(JSON.parse(JSON.stringify(context.window.RecordedSample.events)),require('./assets/tracking/1874553-completed-events.json'));
 assert.equal(read('preview.html'),build.html);assert.ok(build.html.includes(build.recordedScript));
 assert.doesNotMatch(read('recorded-replay.js'),/\bfetch\(|XMLHttpRequest|WebSocket|https?:\/\//);
 for(const r of require('./assets/tracking/manifest.json').artifacts){const bytes=fs.readFileSync(require.resolve('./assets/tracking/'+r.file));assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'),r.sha256);assert.equal(bytes.length,r.bytes);}
 const r=require('./assets/tracking/completed-events-manifest.json').artifact,bytes=fs.readFileSync(require.resolve('./assets/tracking/'+r.file));assert.equal(crypto.createHash('sha256').update(bytes).digest('hex'),r.sha256);assert.equal(bytes.length,r.bytes);
});

test('actual controller list, typed/suggested answers and eligible actor actions share exact spoiler cutoffs',()=>{
 const app=controller(),bundle=require('./assets/tracking/1874553-completed-events.json'),events=require('./recorded-events-model.js').create(sample,bundle);
 function ask(q,typed=false){if(typed){app.node('#recorded-draft').value=q;app.node('#recorded-composer').listeners.submit({preventDefault(){}});}else app.click('question',{recordedQuestion:q});}
 for(const event of bundle.annotations){
  app.seek(event.end-1e-8);const before=app.node('#recorded-event-list').innerHTML;assert.ok(!before.includes(event.sourceId));assert.ok(!before.includes(event.timeStart+'–'+event.timeEnd));
  ask('WHAT HAPPENED BEFORE THIS MOMENT?',true);assert.ok(!app.node('#recorded-thread').innerHTML.includes(event.sourceId));
  app.seek(event.end);const list=app.node('#recorded-event-list').innerHTML;
  for(const e of bundle.annotations)assert.equal(list.includes('data-recorded-event="'+e.sourceId+'"'),events.eligible(event.end).some(f=>f.sourceId===e.sourceId),e.sourceId);
  ask('What happened before this moment?');assert.ok(app.node('#recorded-thread').innerHTML.includes(event.sourceId));assert.match(app.node('#recorded-thread').innerHTML,/source-classified possession interval/);assert.match(app.node('#recorded-thread').innerHTML,/Recorded source analysis, not a live feed/);assert.doesNotMatch(app.node('#recorded-thread').innerHTML,/spoiler cutoff|known-then|dependency/);
 }
 app.seek(65.2-1e-8);app.click('forged-future',{recordedEvent:'8_11'});assert.equal(app.node('#recorded-thread').innerHTML,'');
 app.seek(69.2);app.click('recorded-event-8_12',{recordedEvent:'8_12'});assert.equal(app.node('#recorded-player').value,'809759');assert.match(app.node('#recorded-thread').innerHTML,/Panagiotis Kikianis/);assert.match(app.node('#recorded-thread').innerHTML,/8_12/);
 app.seek(240);ask('What did this player do?',true);const answer=app.node('#recorded-thread').innerHTML;assert.match(answer,/8_12/);assert.doesNotMatch(answer,/8_11|8_13|Bart Vriends/);
 for(const q of ['Who scored next?','What was the next pass?','What was the result?']){app.seek(240);ask(q,true);assert.match(app.node('#recorded-thread').innerHTML,/does not interpret other free-text/);assert.doesNotMatch(app.node('#recorded-thread').innerHTML,/Source 1874553\/8_/);}
});

test('actual controller clears rewind/player notes, permits eligible gap facts, keeps event focus stable on ticks and hides future source details',()=>{
 const app=controller();app.seek(69.2);app.click('recorded-event-8_12',{recordedEvent:'8_12'});
 app.click('recorded-player-26766',{recordedPlayer:'26766'});assert.equal(app.node('#recorded-thread').innerHTML,'');
 app.seek(175.9);app.click('question',{recordedQuestion:'What happened before this moment?'});assert.match(app.node('#recorded-thread').innerHTML,/Current positions are unavailable/);assert.match(app.node('#recorded-thread').innerHTML,/8_37/);assert.match(app.node('#recorded-event-gap').textContent,/no movement/);
 app.seek(60);assert.equal(app.node('#recorded-thread').innerHTML,'');assert.equal(app.node('#recorded-event-list').innerHTML,'');assert.equal(app.node('#recorded-event-empty').hidden,false);
 const sources=app.node('#recorded-context').innerHTML;assert.match(sources,/966956|77e658/);assert.match(sources,/not prove provider live arrival/);assert.doesNotMatch(sources,/8_11|8_12|8_13|8_28|8_37|sourceLine|"annotations"|no event feed is attached/);
 app.seek(65.2);const list=app.node('#recorded-event-list'),button=app.node('#recorded-event-8_11'),html=list.innerHTML;button.focus();app.click('recorded-play');app.tick(.1);app.tick(.1);assert.equal(app.document.activeElement,button);assert.equal(list.innerHTML,html);app.tick(4);assert.equal(app.document.activeElement,button);assert.equal(app.node('#recorded-event-8_11'),button);assert.match(list.innerHTML,/8_12/);
 app.seek(60);assert.equal(app.document.activeElement,app.node('#recorded-range'));assert.equal(app.node('#recorded-thread').innerHTML,'');
 const receiptInjection=JSON.parse(read('assets/tracking/completed-events-manifest.json'));receiptInjection.receipt.futureInterval='FUTURE-SOURCE-SENTINEL';const projectedSources=controller(undefined,receiptInjection);assert.doesNotMatch(projectedSources.node('#recorded-context').innerHTML,/FUTURE-SOURCE-SENTINEL|futureInterval/);
 const injected=JSON.parse(read('assets/tracking/1874553-completed-events.json'));injected.annotations[0].playerTargetedName='FUTURE-SENTINEL';const invalid=controller(injected);invalid.seek(65.2);assert.equal(invalid.node('#recorded-event-list').innerHTML,'');invalid.click('question',{recordedQuestion:'What happened before this moment?'});assert.doesNotMatch(invalid.node('#recorded-thread').innerHTML,/FUTURE-SENTINEL|8_11/);
});

test('actual league entry pauses and restores the same season DOM, cursor and focus across every exit',()=>{
 const nodes=new Map(),docListeners={},timers=new Map(),events=[];let document,timerId=0;
 function node(selector){if(nodes.has(selector))return nodes.get(selector);const listeners={};const n={id:selector.startsWith('#')?selector.slice(1):'',hidden:false,innerHTML:'',value:'',textContent:'',dataset:{},attributes:{},style:{},classList:{toggle(){}},children:[],append(){},
  addEventListener(type,fn){listeners[type]=fn;},setAttribute(k,v){this.attributes[k]=v;},removeAttribute(k){delete this.attributes[k];},querySelectorAll(){return [];},querySelector:node,focus(){document.activeElement=this;},listeners,
  matches:s=>selector===s,closest(s){if(s.split(',').some(x=>x.trim()===selector))return n;if(s==='[data-snapshot-question]'&&n.dataset.snapshotQuestion)return n;return null;}};nodes.set(selector,n);return n;}
 document={querySelector:node,addEventListener(type,fn){(docListeners[type]??=[]).push(fn);},dispatchEvent(event){events.push(event.type);for(const fn of docListeners[event.type]||[])fn(event);}};
 const recorded={opened:false,open(back){this.opened=true;this.back=back;},close(){this.opened=false;this.back();},isOpen(){return this.opened;}};
 const window={scrollY:77,scrollTo(x,y){this.returnY=y;},addEventListener(){}};
 vm.runInNewContext(read('league.js'),{window,document,Event:class{constructor(type){this.type=type;}},RecordedReplay:recorded,LeagueModel:require('./league-model.js'),MatchWorkspace,HistoricalMatchModel:require('./historical-match-model.js'),SeasonTableModel:require('./season-table-model.js'),SeasonResults:{'2015/16':require('./assets/seasons/2015-16.json'),'2016/17':require('./assets/seasons/2016-17.json')},setInterval(fn){timers.set(++timerId,fn);return timerId;},clearInterval(id){timers.delete(id);},queueMicrotask:fn=>fn()});
 const workspace=node('#league-workspace'),range=node('#season-range');
 function click(selector){const e={target:node(selector),preventDefault(){}};workspace.listeners.click(e);for(const fn of docListeners.click||[])fn(e);}
 range.value='6';workspace.listeners.input({target:range});click('#map-toggle');node('#season-speed').value='2';workspace.listeners.change({target:node('#season-speed')});
 const html=workspace.innerHTML,cursor=range.value;click('#season-play');assert.equal(timers.size,1);
 click('#try-recorded');assert.equal(recorded.opened,true);assert.equal(workspace.hidden,true);assert.equal(timers.size,0);assert.ok(events.includes('matchmate:workspace-exit'));
 recorded.close();assert.equal(workspace.hidden,false);assert.equal(workspace.innerHTML,html);assert.equal(range.value,cursor);assert.equal(node('#season-range'),range);assert.equal(document.activeElement,node('#try-recorded'));assert.equal(window.returnY,77);
 click('#try-recorded');node('#open-league').listeners.click();assert.equal(recorded.opened,false);assert.equal(workspace.hidden,false);
 click('#try-recorded');click('#try-demo');assert.equal(recorded.opened,false);assert.equal(node('main').hidden,false);assert.equal(workspace.hidden,true);
 click('#try-recorded');click('.wordmark');assert.equal(recorded.opened,false);assert.equal(node('main').hidden,false);
});

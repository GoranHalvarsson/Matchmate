const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
const Model=require('./historical-match-model.js'),Table=require('./season-table-model.js');
const clubs=['a','b','c'].map(id=>({id,name:id.toUpperCase(),initials:id}));
const fixture=(date,team1,team2,h,a)=>({date,team1:team1.toUpperCase(),team2:team2.toUpperCase(),score:{ft:[h,a]}});
const season=Table.createSeason({matches:[fixture('2016-01-01','a','b',1,0),fixture('2016-01-02','c','a',0,2),fixture('2016-01-03','b','a',3,1),fixture('2016-01-03','a','c',0,0),fixture('2016-01-04','b','c',9,8)]},clubs,{complete:false});
const context={seasonKey:'2015/16',season,match:season.matches.find(m=>m.id==='b:a'),allSeasons:[season]};
test('latest fixture is cutoff bounded and identity includes season',()=>{
 assert.equal(Model.latest(season,0,'a'),null);assert.equal(Model.latest(season,1,'a').id,'a:b');assert.equal(Model.latest(season,4,'a').date,'2016-01-03');assert.notEqual(Model.key('2015/16',context.match),Model.key('2016/17',context.match));
});
test('answers use strict pre-date form and meetings, batch table impact and conservative intents',()=>{
 const form=Model.answer(context,'pre-match form');assert.match(form,/W W/);assert.doesNotMatch(form,/2016-01-04|9–8/);assert.doesNotMatch(form,/2016-01-03: A 0–0/);
 const impact=Model.answer(context,'table impact');assert.match(impact,/all 2 matches/);assert.match(impact,/not a claim/);assert.doesNotMatch(impact,/2016-01-04|9–8/);
 const meetings=Model.answer(context,'prior meetings');assert.match(meetings,/2016-01-01: A 1–0 B/);assert.match(meetings,/not their complete history/);assert.doesNotMatch(meetings,/2016-01-04/);
 for(const q of ['who scored?','score Arsenal tomorrow','result of Chelsea','who won the season'])assert.match(Model.answer(context,q),/not covered/);
 assert.match(Model.answer({...context,match:season.matches[0]},'prior meetings'),/No earlier meeting/);
});
function controller(){
 const nodes=new Map(),docListeners={},windowListeners={};let speech;
 const node=id=>{if(!nodes.has(id))nodes.set(id,{hidden:true,value:'',textContent:'',children:[],attributes:{},listeners:{},setAttribute(k,v){this.attributes[k]=v;},focus(){},append(p){this.children.push(p);},insertAdjacentHTML(position,html){this.children.push({innerHTML:html});},querySelectorAll(){return [];},replaceChildren(){this.children=[];},addEventListener(k,v){this.listeners[k]=v;}});return nodes.get(id);};
 const document={querySelector:s=>node(s),createElement:()=>({}),addEventListener:(k,v)=>docListeners[k]=v};
 const window={addEventListener:(k,v)=>windowListeners[k]=v,SpeechRecognition:class{constructor(){speech=this;}start(){}abort(){}}};
 vm.runInNewContext(fs.readFileSync(require.resolve('./historical-match.js'),'utf8'),{window,document,HistoricalMatchModel:Model,MatchWorkspace:require('./match-workspace.js')});
 const click=selector=>node('#historical-match').listeners.click({target:{closest:s=>s===selector?{}:null}});
 return {window,document,node,click,docListeners,windowListeners,speech:()=>speech,submit(){node('#historical-composer').listeners.submit({preventDefault(){}});}};
}
test('actual composer listeners render text safely and dictation never sends, ignores cancelled events',()=>{
 const app=controller();let returned=0;app.window.HistoricalMatch.open(context,()=>returned++);assert.match(app.node('#historical-catalog').innerHTML,/2016-01-03/);assert.match(app.node('#historical-catalog').innerHTML,/3–1/);
 app.node('#historical-draft').value='<img src=x onerror=alert(1)>';app.submit();const messages=app.node('#historical-thread').children;assert.match(messages[1].innerHTML,/&lt;img src=x onerror=alert\(1\)&gt;/);assert.doesNotMatch(messages[1].innerHTML,/<img/);
 app.node('#historical-draft').value='existing';app.click('#historical-mic');assert.equal(app.node('#historical-send').disabled,true);assert.equal(app.node('#historical-draft').readOnly,true);const speech=app.speech(),late=speech.onresult;
 speech.onresult({resultIndex:0,results:[Object.assign([{transcript:'result'}],{isFinal:true})]});assert.equal(app.node('#historical-draft').value,'existing result');assert.equal(messages.length,3);app.submit();assert.equal(messages.length,3);
 app.window.HistoricalMatch.close();late({resultIndex:0,results:[Object.assign([{transcript:'late'}],{isFinal:true})]});assert.equal(app.node('#historical-draft').value,'existing result');assert.equal(returned,1);assert.equal(app.node('#historical-draft').readOnly,false);assert.equal(app.node('#historical-send').disabled,false);
 app.window.HistoricalMatch.open(context,()=>{});app.click('#historical-mic');const hiddenLate=app.speech().onresult;app.document.hidden=true;app.docListeners.visibilitychange();assert.equal(app.node('#historical-draft').readOnly,false);assert.equal(app.node('#historical-send').disabled,false);app.windowListeners.pagehide();hiddenLate({resultIndex:0,results:[Object.assign([{transcript:'ignored'}],{isFinal:true})]});assert.equal(app.node('#historical-draft').value,'');
});

test('dictation unsupported and errors preserve editable draft with no automatic send',()=>{
 const app=controller();app.window.HistoricalMatch.open(context,()=>{});app.window.SpeechRecognition=null;app.click('#historical-mic');assert.match(app.node('#historical-status').textContent,/unavailable/);
 app.window.SpeechRecognition=class{constructor(){app.rec=this;}start(){}abort(){}};app.node('#historical-draft').value='keep this';app.click('#historical-mic');app.rec.onerror();assert.equal(app.node('#historical-draft').readOnly,false);assert.equal(app.node('#historical-draft').value,'keep this');assert.equal(app.node('#historical-send').disabled,false);assert.equal(app.node('#historical-thread').children.length,1);
});
test('real season form takes only last five own-season prior results; prior-season meetings remain bounded',()=>{
 const League=require('./league-model.js'),one=Table.createSeason(require('./assets/seasons/2015-16.json'),League.members('2015/16')),two=Table.createSeason(require('./assets/seasons/2016-17.json'),League.members('2016/17'));
 const match=two.matches.find(m=>m.home==='arsenal'&&m.away==='chelsea'),ctx={seasonKey:'2016/17',season:two,match,allSeasons:[one,two]};
 const answer=Model.answer(ctx,'pre-match form');const prior=two.matches.filter(m=>m.date<match.date&&(m.home===match.home||m.away===match.home)).slice(-5);for(const m of prior)assert.ok(answer.includes(Model.result(two,m)));assert.doesNotMatch(answer,/2015-/);
 const meetings=Model.answer(ctx,'prior meetings');assert.match(meetings,/2015-09-19/);assert.doesNotMatch(meetings,/2017-02-04/);
});

test('dictation locks draft until stop, end or start failure and never sends automatically',()=>{
 const app=controller();app.window.HistoricalMatch.open(context,()=>{});app.node('#historical-draft').value='keep';
 app.click('#historical-mic');assert.equal(app.node('#historical-draft').readOnly,true);assert.match(app.node('#historical-status').textContent,/before editing/);
 app.click('#historical-mic');assert.equal(app.node('#historical-draft').readOnly,false);
 app.node('#historical-draft').value='correction';app.click('#historical-mic');const late=app.speech().onresult;app.speech().onend();assert.equal(app.node('#historical-draft').readOnly,false);
 late({resultIndex:0,results:[Object.assign([{transcript:'late'}],{isFinal:true})]});assert.equal(app.node('#historical-draft').value,'correction');
 app.window.SpeechRecognition=class{start(){throw Error('blocked');}abort(){}};app.click('#historical-mic');assert.equal(app.node('#historical-draft').readOnly,false);assert.equal(app.node('#historical-send').disabled,false);assert.equal(app.node('#historical-draft').value,'correction');assert.equal(app.node('#historical-thread').children.length,1);
 assert.doesNotMatch(app.node('#historical-scope').textContent,/\||b:a/);
});

test('both modes use shared shell, context, fixture, messages and pitch presentation',()=>{
 const Shared=require('./match-workspace.js'),demo=Shared.shell(),historical=Shared.shell(true);
 for(const html of [demo,historical]){for(const token of ['Who will you','Matches','Clubs','Stadiums','conversation-bar','class="avatar"','class="conversation"','aria-label="Suggested questions"','Type or dictate','<svg width="20"'])assert.ok(html.includes(token),token);}
 assert.match(demo,/id="guide"[^>]*hidden/);assert.match(historical,/id="historical-back"/);assert.doesNotMatch(historical,/data-section=|data-entity=|data-filter=|id="thread"/);assert.match(historical,/disabled title="Not available/);assert.match(historical,/aria-current="page" aria-pressed="true">Matches/);
 const synthetic=Shared.pitch({model:require('./replay-model.js'),time:0,expanded:true}),empty=Shared.pitch({model:{players:[],events:[]},unavailable:true,home:'A',away:'B',score:'3–1'});
 for(const html of [synthetic,empty])for(const token of ['replay-grid','pitch-panel','player-stats','Player</th>','Metres</th>','km/h</th>','Pass ✓','Pass ×','Tkl','Int','replay-controls','ball-controls'])assert.ok(html.includes(token),token);
 assert.match(synthetic,/data-player="H7"/);assert.match(synthetic,/id="replay-play"/);assert.match(empty,/Lineup data not loaded/);assert.doesNotMatch(empty,/Choose a player|Focus player|scroll for more players/);assert.match(empty,/Player positions unavailable/);assert.doesNotMatch(empty,/Harbour|Riverside|65:|data-player=|id="historical-replay-ball"|data-stats-team=/);
 for(const control of empty.matchAll(/<(button|select|input)\b([^>]*)>/g))assert.match(control[2],/disabled/);
 assert.equal((empty.match(/<tbody[^>]*><tr>/g)||[]).length,1);
 for(const [file,call] of [['replay.js','MatchWorkspace.pitch('],['historical-match.js','MatchWorkspace.pitch('],['app.js','MatchWorkspace.message('],['historical-match.js','MatchWorkspace.message('],['experience.js','MatchWorkspace.context('],['historical-match.js','MatchWorkspace.context('],['app.js','MatchWorkspace.fixture('],['historical-match.js','MatchWorkspace.fixture(']])assert.ok(fs.readFileSync(require.resolve('./'+file),'utf8').includes(call));
 const css=fs.readFileSync(require.resolve('./style.css'),'utf8');assert.match(css,/\.match-workspace:has\(\.replay\)\{grid-template-columns:220px minmax\(0,1fr\)/);assert.doesNotMatch(css,/historical-conversation|historical-message|historical-pitch|historical-explore/);
});

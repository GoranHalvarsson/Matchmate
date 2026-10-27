const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const LeagueModel = require('./league-model.js');

// Unit-level event targets for the real league.js listeners. No server or
// alternate app is created; layout/browser behavior remains a separate gate.
function loadPicker() {
  const nodes=new Map(),documentListeners=new Map(),microtasks=[],timers=new Map();let timerId=0;
  let document;
  function node(selector,inside=false) {
    if(nodes.has(selector)) return nodes.get(selector);
    const listeners=new Map();
    const n={id:selector.startsWith('#')?selector.slice(1):'',hidden:false,innerHTML:'',value:'',textContent:'',dataset:{},attributes:{},style:{},children:[],classList:{toggle(){}},append(){},
      addEventListener(type,fn){if(!listeners.has(type))listeners.set(type,[]);listeners.get(type).push(fn);},
      dispatch(type,event){for(const fn of listeners.get(type)||[])fn(event);},
      setAttribute(k,v){this.attributes[k]=v;},removeAttribute(k){delete this.attributes[k];},
      querySelectorAll(){return [];},querySelector(s){return node(s,true);},
      focus(){document.activeElement=this;},
      matches(s){return selector===s;},
      closest(s){if(s.split(',').some(x=>x.trim()===selector))return this;if(s==='[data-season-option]'&&this.dataset.seasonOption)return this;if(s.includes('[data-season-option]')&&this.dataset.seasonOption)return this;if(s==='[data-league-club]'&&this.dataset.leagueClub)return this;if(s==='[data-snapshot-question]'&&this.dataset.snapshotQuestion)return this;if(s==='[data-league-view]'&&this.dataset.leagueView)return this;if(s==='.season-picker'&&inside)return node('.season-picker',true);return null;}
    };
    nodes.set(selector,n);return n;
  }
  document={activeElement:null,querySelector:s=>node(s,s.startsWith('#season-')||s==='#league-season-trigger'),addEventListener(type,fn){if(!documentListeners.has(type))documentListeners.set(type,[]);documentListeners.get(type).push(fn);}};
  document.activeElement=node('body');
  const route={};
  vm.runInNewContext(fs.readFileSync(require.resolve('./league.js'),'utf8'),{HistoricalMatch:{open(value,back){route.value=value;route.back=back;route.open=true;},close(){route.open=false;route.back();},isOpen(){return !!route.open;}},document,LeagueModel,MatchWorkspace:require('./match-workspace.js'),HistoricalMatchModel:require('./historical-match-model.js'),SeasonTableModel:require('./season-table-model.js'),SeasonResults:{'2015/16':require('./assets/seasons/2015-16.json'),'2016/17':require('./assets/seasons/2016-17.json')},setInterval(fn){timers.set(++timerId,fn);return timerId;},clearInterval(id){timers.delete(id);},queueMicrotask:fn=>microtasks.push(fn)});
  const workspace=node('#league-workspace');
  function event(target,extra={}){return {target,preventDefault(){this.defaultPrevented=true;},...extra};}
  return {node,document,workspace,timers,route,tick(){for(const fn of [...timers.values()])fn();},emit(type,target,extra={}){const e=event(target,extra);workspace.dispatch(type,e);for(const fn of documentListeners.get(type)||[])fn(e);return e;},flush(){while(microtasks.length)microtasks.shift()();}};
}
test('option pointerdown keeps search focus; blur-before-click does not discard selection',()=>{
  const app=loadPicker();
  app.emit('click',app.node('#league-season-trigger',true));
  assert.equal(app.node('#season-popup').hidden,false);
  const second=app.node('#season-option-2016',true);second.dataset.seasonOption='2016/17';
  const pointer=app.emit('pointerdown',second,{button:0,pointerType:'mouse'});
  // Reproduce the browser sequence that broke the previous blur microtask:
  // focus can temporarily be body before pointerup/click is dispatched.
  app.document.activeElement=app.node('body');
  app.emit('focusout',app.node('#season-search',true),{relatedTarget:null});app.flush();
  assert.equal(app.node('#season-popup').hidden,false,'option must remain hit-testable until click');
  assert.equal(pointer.defaultPrevented,true);
  app.emit('click',second);
  assert.match(app.workspace.innerHTML,/<span id="season-value">2016\/17<\/span>/);
  assert.match(app.node('#league-card').innerHTML,/<h3>London Stadium<\/h3>/);
  assert.equal(app.document.activeElement,app.node('#league-season-trigger'));
});
test('touch option selection, Escape and settled outside focus retain their behavior',()=>{
  const app=loadPicker(),trigger=app.node('#league-season-trigger',true);
  app.emit('click',trigger);
  const second=app.node('#season-option-2016',true);second.dataset.seasonOption='2016/17';
  assert.equal(app.emit('pointerdown',second,{button:0,pointerType:'touch'}).defaultPrevented,true);
  app.emit('click',second);
  assert.match(app.workspace.innerHTML,/<span id="season-value">2016\/17<\/span>/);
  app.emit('click',trigger);app.emit('keydown',app.node('#season-search',true),{key:'Escape'});
  assert.equal(app.node('#season-popup').hidden,true);assert.equal(app.document.activeElement,trigger);
  app.emit('click',trigger);app.emit('focusin',app.node('#outside'));
  assert.equal(app.node('#season-popup').hidden,true);
  app.emit('click',trigger);app.emit('pointerdown',app.node('#outside'),{button:0});
  assert.equal(app.node('#season-popup').hidden,true);
});
test('date playback preserves range/control nodes, backseeks, and resets/stops when changing season',()=>{
  const app=loadPicker(),range=app.node('#season-range'),play=app.node('#season-play');
  const shell=app.workspace.innerHTML;
  assert.match(app.node('#snapshot-label').textContent,/17 May 2016/);assert.match(app.node('#snapshot-count').textContent,/380 \/ 380/);assert.match(play.textContent,/Replay from start/);assert.equal(app.timers.size,0);
  app.emit('click',play);assert.equal(app.timers.size,1);assert.equal(range.value,0);assert.equal(app.node('#snapshot-label').textContent,'Before first match');app.tick();
  assert.equal(app.node('#season-range'),range);assert.equal(app.node('#season-play'),play);assert.equal(app.workspace.innerHTML,shell,'date ticks must not replace the workspace or focused range');
  assert.equal(range.value,1);assert.match(app.node('#snapshot-label').textContent,/8 Aug 2015/);
  range.value='0';app.emit('input',range);assert.equal(app.timers.size,0);assert.equal(app.node('#snapshot-label').textContent,'Before first match');
  app.emit('click',play);app.emit('click',app.node('#league-season-trigger',true));const second=app.node('#season-option-2016',true);second.dataset.seasonOption='2016/17';app.emit('click',second);
  assert.equal(app.timers.size,0);assert.equal(range.value,require('./assets/seasons/2016-17.json').matches.map(m=>m.date).filter((v,i,a)=>a.indexOf(v)===i).length);assert.match(app.node('#snapshot-count').textContent,/380 \/ 380/);assert.match(app.node('#snapshot-label').textContent,/21 May 2017/);assert.match(app.workspace.innerHTML,/<span id="season-value">2016\/17/);
  app.emit('click',play);const club=app.node('#club-choice');club.dataset.leagueClub='arsenal';app.emit('click',club);assert.equal(app.timers.size,0,'exploring a club pauses playback');
  app.emit('click',play);app.emit('click',app.node('#try-demo'));assert.equal(app.timers.size,0,'leaving the league pauses playback');
});

test('mobile map toggle preserves question, playback and collapse across club repaint',()=>{
 const app=loadPicker(),question=app.node('#question');question.dataset.snapshotQuestion='recent';app.emit('click',question);
 app.emit('click',app.node('#season-play'));const answer=app.node('#snapshot-answer').textContent;
 const range=app.node('#season-range');app.emit('click',app.node('#map-toggle'));
 assert.equal(app.node('#map-toggle').attributes['aria-expanded'],'false');assert.equal(app.timers.size,1);
 assert.equal(app.node('#season-range'),range);assert.equal(app.node('#snapshot-answer').textContent,answer);
 app.emit('click',app.node('#league-season-trigger',true));const second=app.node('#season-option-2016',true);second.dataset.seasonOption='2016/17';app.emit('click',second);
 assert.match(app.workspace.innerHTML,/aria-expanded="false" aria-controls="league-map-canvas">Show map/);
 assert.match(app.workspace.innerHTML,/league-canvas map-collapsed/);assert.doesNotMatch(app.workspace.innerHTML,/league-views|league-list|data-league-view/);
 app.emit('click',app.node('#map-toggle'));assert.equal(app.node('#map-toggle').attributes['aria-expanded'],'true');
 const css=fs.readFileSync(require.resolve('./style.css'),'utf8');assert.match(css,/@media\(max-width:760px\)\{[^}]*\}#map-toggle[^]*?\.league-canvas\.map-collapsed\{display:none\}/);
});
test('replay responsive rules follow legacy layout rules without table height caps',()=>{
 const css=fs.readFileSync(require.resolve('./style.css'),'utf8'),monitor=css.indexOf('/* Results monitor:');
 assert.ok(css.lastIndexOf('@media(max-width:1050px)')<monitor);
 assert.ok(css.lastIndexOf('@media(max-width:720px)')<monitor);
 assert.match(css.slice(monitor),/@media\(max-width:1200px\)\{\.league-layout\{grid-template-columns:330px minmax\(0,1fr\)/);
 assert.match(css.slice(monitor),/@media\(max-width:760px\).*?\.league-heading h1\{font-size:31px\}/s);
 assert.doesNotMatch(css,/\.standings-scroll\{[^}]*max-height:(?!none)[^}]+\}/);
});

test('standings starts with columns while end-of-day qualifier follows timeline date',()=>{
 const app=loadPicker();assert.doesNotMatch(app.workspace.innerHTML,/table-title|id="table-date"|>The table</);
 assert.match(app.workspace.innerHTML,/aria-label="Computed league table"><div class="standings-scroll"/);
 assert.match(app.workspace.innerHTML,/<caption class="sr-only">Computed results-only league standings/);
 assert.match(app.node('#snapshot-label').textContent,/17 May 2016/);assert.equal(app.node('#snapshot-qualifier').textContent,' · End of day');
 app.emit('click',app.node('#season-prev'));assert.equal(app.node('#snapshot-qualifier').textContent,' · End of day');
 const range=app.node('#season-range');range.value='0';app.emit('input',range);assert.equal(app.node('#snapshot-qualifier').textContent,'');
 assert.doesNotMatch(fs.readFileSync(require.resolve('./league.js'),'utf8'),/querySelector\('#table-date'\)/);
});

test('historical handoff uses exact season fixture and returns unchanged explorer paused',()=>{
 const app=loadPicker(),range=app.node('#season-range');range.value='0';app.emit('input',range);assert.equal(app.node('#talk-match').hidden,true);
 range.value='3';app.emit('input',range);app.emit('click',app.node('#map-toggle'));
 const question=app.node('#q');question.dataset.snapshotQuestion='recent';app.emit('click',question);
 const answer=app.node('#snapshot-answer').textContent,shell=app.workspace.innerHTML,key=app.node('#talk-match').dataset.fixture;assert.match(key,/^2015\/16\|/);assert.equal(app.node('#snapshot-today').hidden,true);
 app.emit('click',app.node('#season-play'));app.emit('click',app.node('#talk-match'));assert.equal(app.timers.size,0);assert.equal(app.workspace.hidden,true);
 assert.equal(app.route.value.seasonKey,'2015/16');assert.equal(app.route.value.match.id,key.split('|')[1]);
 app.node('#open-league').dispatch('click',{});assert.equal(app.workspace.hidden,false);assert.equal(app.workspace.innerHTML,shell);assert.equal(range.value,3);assert.equal(app.node('#snapshot-answer').textContent,answer);assert.equal(app.timers.size,0);
});

test('seasons open at final leader totals paused and forward replay stops at final date',()=>{
 const app=loadPicker(),Table=require('./season-table-model.js');
 for(const season of ['2015/16','2016/17']){
  if(season==='2016/17'){app.emit('click',app.node('#league-season-trigger',true));const option=app.node('#season-option-2016',true);option.dataset.seasonOption=season;app.emit('click',option);}
  const normalized=Table.createSeason(require(season==='2015/16'?'./assets/seasons/2015-16.json':'./assets/seasons/2016-17.json'),LeagueModel.members(season)),index=app.node('#season-range').value;
  assert.equal(index,normalized.dates.length);const final=Table.snapshot(normalized,index);assert.equal(final.completed,380);assert.equal(final.rows[0].id,season==='2015/16'?'leicester':'chelsea');assert.equal(final.rows[0].pts,season==='2015/16'?81:93);assert.equal(app.timers.size,0);
  app.emit('click',app.node('#season-play'));assert.equal(app.node('#season-range').value,0);for(let i=0;i<normalized.dates.length;i++)app.tick();assert.equal(app.node('#season-range').value,normalized.dates.length);assert.equal(app.timers.size,0);assert.match(app.node('#season-play').textContent,/Replay from start/);
 }
});

test('latest match card keeps real away order, date context, period crests and one exact handoff',()=>{
 const app=loadPicker(),range=app.node('#season-range'),raw=require('./assets/seasons/2015-16.json'),dates=[...new Set(raw.matches.map(m=>m.date))].sort();
 const club=app.node('#tottenham');club.dataset.leagueClub='tottenham';app.emit('click',club);range.value=String(dates.indexOf('2015-10-04')+1);app.emit('input',range);
 const talk=app.node('#talk-match');assert.equal(talk.hidden,false);assert.equal(talk.dataset.fixture,'2015/16|swansea:tottenham');assert.match(talk.innerHTML,/Played on this date/);assert.match(talk.innerHTML,/4 Oct 2015 · Full time/);assert.ok(talk.innerHTML.indexOf('Swansea City')<talk.innerHTML.indexOf('Tottenham Hotspur'));assert.match(talk.innerHTML,/latest-match-score">2<span[^>]*>–<\/span>2/);assert.match(talk.innerHTML,/latest-match-team is-selected/);assert.match(talk.innerHTML,/Explore this match/);assert.doesNotMatch(talk.innerHTML,/<button/);assert.match(talk.innerHTML,/assets\/crests\/tottenham.svg/);assert.match(talk.innerHTML.split('latest-match-score')[0],/assets\/crests\/swansea.png/);assert.equal((talk.innerHTML.match(/crest-fallback/g)||[]).length,2);
 range.value=String(dates.indexOf('2015-10-18')+1);app.emit('input',range);assert.match(talk.innerHTML,/Latest match before this date/);
 range.value='0';app.emit('input',range);assert.equal(talk.hidden,true);assert.equal(talk.innerHTML,'');assert.equal(talk.dataset.fixture,'');
 app.emit('click',app.node('#league-season-trigger',true));const option=app.node('#season-option-2016',true);option.dataset.seasonOption='2016/17';app.emit('click',option);assert.match(talk.dataset.fixture,/^2016\/17\|/);assert.match(talk.innerHTML,/assets\/crests\/tottenham.svg/);assert.equal((talk.innerHTML.match(/crest-fallback/g)||[]).length,2);
});

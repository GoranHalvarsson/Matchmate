const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),vm=require('node:vm');
const api=require('./pl-timeline-model.js'),projection=require('../../data/manual-match-annotations/project-full-match-timeline.cjs'),build=require('./build-preview.cjs').buildPreview(),sample=build.timelineSample,model=api.create(sample),MatchWorkspace=require('./match-workspace.js');
const read=name=>fs.readFileSync(path.join(__dirname,name),'utf8'),clone=o=>JSON.parse(JSON.stringify(o)),sha=b=>crypto.createHash('sha256').update(b).digest('hex'),entry=id=>sample.passages.find(p=>p.id===id);

test('pinned 27-passage timeline preserves canonical fixture, source receipts and explicit uncertainty without private payloads',()=>{
 const pinned=projection.loadPinned(),old=require('../../data/manual-match-annotations/brentford-chelsea-2026-09-18-first-goal.json');
 assert.equal(sample.passages.length,27);assert.equal(sample.fixture.id,old.fixture.id);assert.equal(sample.fixture.officialMatchId,2645236);assert.equal(sample.bounds.start,0);assert.equal(sample.bounds.end,5872.04644);
 assert.equal(pinned.receipt.artifact.sha256,sha(pinned.bytes));assert.equal(sample.source.reviewedBundle.sha256,'c02321106f4e5b444ff7c0ba891fcca07ff40c7a945882c863861c281c6e0908');
 assert.deepEqual(pinned.receipt.passages.filter(p=>p.uncertaintyExcerpted).map(p=>p.id),Object.keys(projection.excerpts).filter(id=>sample.passages.some(p=>p.id===id)));
 for(const p of sample.passages){assert.ok(p.uncertainty);assert.ok(p.reviewStatus);assert.ok(p.support.asr.length||p.support.visual.length);assert.equal(p.withheldReason,null);}
 assert.doesNotMatch(JSON.stringify(sample),/raw_text|"words"|"transcript"|\.jpg|\.mp4|\.m4a|signed|C:[/\\]|frames[/\\]|published_report_rows|report_conflicts|report_refs|Schuster|Diouf\/Ouattara|84\/85|80\/83/);
 assert.equal(sample.policy.reportsIncluded,false);assert.equal(sample.policy.completeIncidentFeed,false);assert.equal(sample.policy.movement,false);
});

test('reviewed source exactly reproduces all passages, excerpts, scoped support and latest cutoffs when research is present',t=>{
 const dir=path.join(__dirname,'../../output/full-match-review');if(!fs.existsSync(path.join(dir,'review-bundle.json')))return t.skip('Ignored source unavailable; pinned build is tested independently');
 const generated=projection.project(dir),pinned=projection.loadPinned(),ledger=JSON.parse(fs.readFileSync(path.join(dir,'event-ledger.json'))),speech=JSON.parse(fs.readFileSync(path.join(dir,'asr-complete.json')));
 assert.ok(generated.bytes.equals(pinned.bytes));assert.deepEqual(generated.receipt,pinned.receipt);assert.deepEqual(sample.passages.map(p=>p.id),ledger.episodes.map(p=>p.id));
 for(const p of sample.passages){const original=ledger.episodes.find(e=>e.id===p.id);assert.equal(p.title,original.title);assert.equal(p.text,projection.readable(p.id,'text',original.paraphrase));assert.equal(p.reviewStatus,original.review_status);assert.equal(p.uncertainty,projection.readable(p.id,'uncertainty',projection.excerpts[p.id]||original.uncertainty));for(const s of p.support.asr){const raw=speech.segments.find(r=>r.id===s.id);assert.deepEqual([s.start,s.end],[raw.start,raw.end]);}assert.deepEqual(p.support.asr.filter(r=>r.role==='context').map(r=>r.id),original.asr_context_refs);}
 assert.equal(entry('E-01').support.visual[0].end,13);assert.equal(entry('E-06').support.visual.find(v=>v.id==='V-PASS-02').end,2805);assert.equal(entry('E-09').support.visual.find(v=>v.id==='V-PER-07').end,3735);
});

test('each passage is absent just before its latest support and present exactly at/after it, including later context and visual scope',()=>{
 for(const p of sample.passages){const t=p.availableAt;assert.equal(t,Math.max(p.mediaInterval[1],...p.support.asr.map(r=>r.end),...p.support.visual.map(r=>r.end)));assert.ok(!model.eligible(t-1e-8).some(e=>e.id===p.id));assert.ok(model.eligible(t).some(e=>e.id===p.id));if(t<model.end)assert.ok(model.eligible(t+1e-8).some(e=>e.id===p.id));assert.equal(model.view(t-1e-8,p.id).selected,null);assert.equal(model.view(t,p.id).selected.id,p.id);}
 assert.equal(entry('E-01').availableAt,18.8);assert.equal(entry('E-08').availableAt,4240);assert.equal(entry('E-15').availableAt,5812.54);assert.ok(entry('E-15').availableAt>entry('E-15').mediaInterval[1]);
 assert.equal(model.eligible(0).length,0);assert.equal(model.adjacent(0,1),18.8);assert.equal(model.adjacent(model.end,1),null);assert.equal(model.eligible(model.end).length,27);
});

test('invalid cursors and future selection fail closed, gaps retain only completed evidence and never manufacture a score or clock',()=>{
 for(const t of [NaN,Infinity,-1,model.end+1,null,true,'600',undefined]){assert.deepEqual(model.eligible(t),[]);assert.equal(model.view(t,'E-15').selected,null);assert.equal(model.adjacent(t,1),null);assert.equal(model.clamp(t),null);assert.match(model.answer(t,'E-15','What happened here?').text,/Invalid media cursor/);}
 const v=model.view(500,'E-15');assert.equal(v.selected,null);assert.ok(v.gap);assert.equal(v.passages.length,1);assert.doesNotMatch(model.answer(500,'E-15','What happened here?').text,/throw-in|third.goal|Carvalho/);assert.equal(model.view(500).selected.id,'E-01');
 assert.equal(Object.hasOwn(v,'score'),false);assert.equal(Object.hasOwn(v,'matchClock'),false);assert.equal(Object.hasOwn(v,'positions'),false);
});

test('malformed fields, evidence intervals, source URLs, cutoffs and missing candidate badges are rejected',()=>{
 const mutations=[s=>s.bounds.end=-1,s=>s.fixture.id='another-match',s=>s.policy.reportsIncluded=true,s=>s.source.url='javascript:alert(1)',s=>s.source.ledger.sha256='x',s=>s.passages[0].report='forbidden',s=>s.passages[0].availableAt=18,s=>s.passages[0].availableAt=19,s=>s.passages[0].support.asr[0].end=-1,s=>s.passages[0].support.visual[0].scope='',s=>s.passages[0].support.visual[0].end=model.end+1,s=>s.passages[0].support.asr[0].text='raw speech',s=>s.passages[1].badges=['sampled_visual'],s=>s.passages[0].badges=[],s=>s.passages[1].support.asr.push(s.passages[1].support.asr[0]),s=>s.passages.push(s.passages[0]),s=>s.passages[0].availableAt=null];
 for(const mutate of mutations){const value=clone(sample);mutate(value);assert.throws(()=>api.create(value),/PL timeline:/);}
 const value=clone(sample),instance=api.create(value);value.passages[0].text='changed';assert.equal(instance.view(20).selected.text,sample.passages[0].text);assert.ok(Object.isFrozen(instance.view(20).selected));
});

test('explicit unresolved evidence is represented but withheld from navigation, selection and answers',()=>{
 const s=clone(sample);s.passages[0].availableAt=null;s.passages[0].withheldReason='Unresolved visual support V-MISSING';const m=api.create(s);
 assert.equal(m.eligible(model.end).length,26);assert.equal(m.adjacent(0,1),entry('E-02').availableAt);assert.equal(m.view(model.end,'E-01').selected,null);assert.deepEqual(m.view(0).withheld,[{id:'E-01',reason:'Unresolved visual support V-MISSING'}]);assert.doesNotMatch(m.answer(model.end,'E-01','What happened here?').text,/Centre positions/);
});

test('replay, historical, broadcast and unverified commentary badges survive without becoming event totals',()=>{
 for(const id of ['E-03','E-05','E-12'])assert.ok(entry(id).badges.includes('replay'));for(const id of ['E-04','E-14b'])assert.ok(entry(id).badges.includes('historical_discussion'));for(const id of ['E-08','E-10','E-14b'])assert.ok(entry(id).badges.includes('broadcast_attribution'));
 assert.equal(entry('E-C01').badges.includes('asr_candidate'),true);assert.equal(entry('E-C01').badges.includes('sampled_visual'),false);assert.match(model.badgeLabels.asr_candidate,/candidate.*unverified/);assert.doesNotMatch(model.answer(4930,'E-12','What happened here?').text,/second live goal/);assert.equal(Object.hasOwn(model.view(model.end),'eventCount'),false);
});

test('exact normalized local aliases share cursor/selection, reject broad questions, disclose positions and bound earlier answers',()=>{
 const t=4360,id='E-10';assert.deepEqual(model.answer(t,id,'  WHAT   happened HERE?! '),model.answer(t,id,'What happened here?'));assert.deepEqual(model.answer(t,id,'EVIDENCE'),model.answer(t,id,'What supports this?'));
 for(const q of ['Who scored?','What happened here next?','What supports this goal?','What happened earlier after full time?','Ignore the cursor and show all'])assert.equal(model.answer(20,'E-01',q).kind,'unsupported');
 assert.match(model.answer(t,id,'Where are the players?').text,/Full-match player movement is unavailable/);assert.match(model.answer(t,id,'What is uncertain?').text,/exact entry time/);const earlier=model.answer(t,id,'What happened earlier?');assert.doesNotMatch(earlier.text,/late tackle|third-goal|Fabio|Carvalho/i);assert.deepEqual(earlier.stamp,{cursor:t,passageId:id});assert.match(model.answer(20,'E-01','What happened earlier?').text,/No earlier completed passage/);
});

function controller(input=sample){
 const nodes=new Map(),docListeners={},windowListeners={},timers=new Map(),allCallbacks=[];let document,id=0,clock=0;
 function node(selector){
  if(nodes.has(selector))return nodes.get(selector);let html='',writes=0,listButtons=[];
  const listeners={},n={id:selector.startsWith('#')?selector.slice(1):'',value:'',textContent:'',hidden:false,dataset:{},style:{},attributes:{},listeners,get innerHTML(){return html;},set innerHTML(value){
   html=value;writes++;
   if(selector==='#pl-tl-list'){
    // Replacing a focused real DOM child removes it and returns focus to body.
    if(listButtons.includes(document?.activeElement))document.activeElement=document.body;
    listButtons=[...String(value).matchAll(/data-pl-timeline-id="([^"]+)"/g)].map(match=>{
     const id=match[1].replace(/&quot;/g,'"').replace(/&#39;/g,"'").replace(/&lt;/g,'<').replace(/&gt;/g,'>').replace(/&amp;/g,'&');
     const button={dataset:{plTimelineId:id},focus(options){document.activeElement=this;this.focusOptions=options;},closest:s=>s==='button'?button:null};return button;
    });
   }
  },get htmlWrites(){return writes;},querySelector:node,querySelectorAll(selector){assert.equal(selector,'button[data-pl-timeline-id]','Focused passage lookup uses a static safe selector');return listButtons;},setAttribute(k,v){this.attributes[k]=v;},addEventListener(type,fn){listeners[type]=fn;},focus(){document.activeElement=this;},replaceChildren(){this.innerHTML='';},insertBefore(child,before){this.moved=[child.id,before.id];},insertAdjacentHTML(where,value){this.innerHTML=where==='afterbegin'?value+this.innerHTML:this.innerHTML+value;},closest:s=>s==='button'?n:null};nodes.set(selector,n);return n;
 }
 document={hidden:false,body:{tagName:'BODY'},querySelector:node,addEventListener(type,fn){docListeners[type]=fn;}};
 const window={PLTimelineSample:input,PLTimelineModel:api,PLAnnotationsSample:build.plSample,PLAnnotationsModel:require('./pl-annotations-model.js'),PLReconstructionSample:build.reconstructionSample,PLReconstructionModel:require('./pl-reconstruction-model.js'),performance:{now:()=>clock},addEventListener(type,fn){windowListeners[type]=fn;}};
 const context={window,document,MatchWorkspace,setInterval(fn,interval){allCallbacks.push(fn);timers.set(++id,{fn,interval});return id;},clearInterval(id){timers.delete(id);}};
 vm.runInNewContext(read('pl-timeline.js'),context);vm.runInNewContext(read('pl-annotations.js'),context);
 return {node,window,document,docListeners,windowListeners,timers,allCallbacks,seek(value){node('#pl-tl-range').listeners.input({target:{value}});},ask(value){node('#pl-tl-draft').value=value;node('#pl-tl-composer').listeners.submit({preventDefault(){}});},click(id,dataset={}){const target=node('#'+id);target.dataset=dataset;const e={target};if(id.startsWith('pl-tl-')||Object.keys(dataset).some(k=>k.startsWith('plTimeline')))node('#pl-timeline-panel').listeners.click(e);node('#pl-workspace').listeners.click(e);},tick(ms=1800){clock+=ms;for(const {fn}of [...timers.values()])fn();}};
}

test('actual combined controller defaults to empty full timeline and exposes no future passage title or content',()=>{
 const app=controller();app.window.PLAnnotations.open(()=>{});assert.equal(app.node('#pl-mode-timeline').attributes['aria-pressed'],'true');assert.equal(app.node('#pl-inspector').hidden,true);assert.equal(app.node('.pl-conversation').hidden,true);assert.equal(app.node('#pl-timeline-panel').hidden,false);assert.equal(app.node('#pl-tl-range').value,'0');assert.equal(app.node('#pl-tl-list').innerHTML,'');assert.match(app.node('#pl-tl-current').innerHTML,/No completed evidence yet/);
 const rendered=app.node('#pl-tl-current').innerHTML+app.node('#pl-tl-list').innerHTML+app.node('#pl-timeline-panel').innerHTML;
 for(const p of sample.passages){assert.ok(!rendered.includes(p.title));assert.ok(!rendered.includes(p.text));}
 app.click('pl-tl-next');assert.equal(app.node('#pl-tl-range').value,'18.8');assert.match(app.node('#pl-tl-current').innerHTML,/Opening centre restart/);assert.doesNotMatch(app.node('#pl-tl-list').innerHTML,/Early goalkeeper-save/);
});

test('scrub, previous/next, eligible selection, rewind and current questions share deterministic state and reset stale answers',()=>{
 const app=controller();app.window.PLAnnotations.open(()=>{});const range=app.node('#pl-tl-range'),writes=app.node('#pl-workspace').htmlWrites;app.seek(651.86-1e-8);assert.doesNotMatch(app.node('#pl-tl-list').innerHTML,/Set-piece and save/);app.seek(651.86);assert.match(app.node('#pl-tl-list').innerHTML,/Set-piece and save/);app.click('older',{plTimelineId:'E-01'});app.ask('What happened here?');assert.match(app.node('#pl-tl-thread').innerHTML,/SOURCE 00:10:51 \/ 651.86 s · E-01/);assert.match(app.node('#pl-tl-thread').innerHTML,/Centre positions/);assert.doesNotMatch(app.node('#pl-tl-thread').innerHTML,/goalkeeper save/);
 const previous=app.node('#pl-tl-current').innerHTML;app.click('future',{plTimelineId:'E-15'});assert.equal(app.node('#pl-tl-current').innerHTML,previous);app.seek(18.8-1e-8);assert.equal(app.node('#pl-tl-thread').innerHTML,'');assert.equal(app.node('#pl-tl-list').innerHTML,'');assert.match(app.node('#pl-tl-current').innerHTML,/No completed evidence/);
 app.click('pl-tl-next');app.click('pl-tl-next');assert.equal(range.value,'598.39');app.click('pl-tl-previous');assert.equal(range.value,'18.8');app.click('pl-tl-start');assert.equal(range.value,'0');assert.equal(app.node('#pl-tl-range'),range);assert.equal(app.node('#pl-workspace').htmlWrites,writes);
});

test('passage playback uses 1.8-second endpoint steps, never wraps, and cannot run a cleared queued callback',()=>{
 const app=controller();app.window.PLAnnotations.open(()=>{});app.click('pl-tl-play');assert.equal(app.timers.size,1);assert.equal([...app.timers.values()][0].interval,1800);app.tick();assert.equal(app.node('#pl-tl-range').value,'18.8');app.ask('What supports this?');assert.equal(app.timers.size,0);const text=app.node('#pl-tl-thread').innerHTML;app.allCallbacks.at(-1)();assert.equal(app.node('#pl-tl-thread').innerHTML,text);assert.equal(app.node('#pl-tl-range').value,'18.8');
 app.click('pl-tl-play');app.seek(0);app.allCallbacks.at(-1)();assert.equal(app.node('#pl-tl-range').value,'0');assert.equal(app.timers.size,0);app.click('pl-tl-play');for(let i=0;i<30;i++)app.tick();assert.equal(app.node('#pl-tl-range').value,String(model.end));assert.equal(app.timers.size,0);assert.equal(app.node('#pl-tl-next').disabled,true);assert.match(app.node('#pl-tl-status').textContent,/End of the reviewed source/);
});

test('mode changes preserve prior estimated/capture behavior, stop both timers and reset conversations on mode/hide/exit/return',()=>{
 const app=controller();let back=0;app.window.PLAnnotations.open(()=>back++);app.click('pl-tl-next');app.ask('What happened here?');app.click('pl-tl-play');app.click('pl-mode-estimated');assert.equal(app.timers.size,0);assert.equal(app.node('#pl-tl-thread').innerHTML,'');assert.equal(app.node('#pl-timeline-panel').hidden,true);assert.equal(app.node('#pl-range').value,'3715');assert.equal(app.node('#pl-track-shirt27').hidden,false);app.click('pl-play');assert.equal(app.timers.size,1);app.click('pl-mode-reviewed');assert.equal(app.timers.size,0);assert.equal(app.node('#pl-range').value,'3707.22');assert.equal(app.node('#pl-goalmouth').dataset.active,'true');
 app.click('pl-mode-timeline');assert.equal(app.node('#pl-tl-range').value,'18.8');for(const event of ['hide','pagehide','workspace-exit']){app.ask('What is uncertain?');app.click('pl-tl-play');if(event==='hide'){app.document.hidden=true;app.docListeners.visibilitychange();}else if(event==='pagehide')app.windowListeners.pagehide();else app.docListeners['matchmate:workspace-exit']();assert.equal(app.timers.size,0);assert.equal(app.node('#pl-tl-thread').innerHTML,'');const time=app.node('#pl-tl-range').value;app.allCallbacks.at(-1)();assert.equal(app.node('#pl-tl-range').value,time);}
 app.click('pl-tl-play');app.click('pl-back');assert.equal(back,1);assert.equal(app.timers.size,0);assert.equal(app.window.PLAnnotations.isOpen(),false);app.click('pl-tl-next');assert.equal(app.node('#pl-tl-range').value,'18.8');app.window.PLAnnotations.open(()=>{});assert.equal(app.node('#pl-tl-thread').innerHTML,'');
});

test('REV-1 F-16 selecting an eligible list button restores focus after DOM replacement using a safe static lookup',()=>{
 for(const id of ['E-12','E-12"][data-untrusted="value']){
  const input=clone(sample);if(id!=='E-12')input.passages.find(p=>p.id==='E-12').id=id;
  const app=controller(input);app.window.PLAnnotations.open(()=>{});app.seek(5812.54);
  const list=app.node('#pl-tl-list'),focused=list.querySelectorAll('button[data-pl-timeline-id]').find(button=>button.dataset.plTimelineId===id);focused.focus();assert.equal(app.document.activeElement,focused);
  // Enter on a native button dispatches its click; exercise the actual delegated listener.
  app.node('#pl-timeline-panel').listeners.click({target:focused});
  const selected=list.querySelectorAll('button[data-pl-timeline-id]').find(button=>button.dataset.plTimelineId===id);
  assert.notEqual(selected,focused,'The focused child was replaced during paint');assert.notEqual(app.document.activeElement,app.document.body);assert.equal(app.document.activeElement,selected);assert.equal(selected.dataset.plTimelineId,id);assert.equal(selected.focusOptions.preventScroll,true);assert.equal(app.node('#pl-tl-range').value,'5812.54');
 }
});

test('REV-1 F-17 lifecycle stops and reactivation report paused at the preserved cursor and invalidate queued steps',()=>{
 const app=controller();app.window.PLAnnotations.open(()=>{});app.seek(5812.54);
 const paused=()=>{assert.equal(app.timers.size,0);assert.equal(app.node('#pl-tl-play').attributes['aria-pressed'],'false');assert.equal(app.node('#pl-tl-range').value,'5812.54');assert.equal(app.node('#pl-tl-thread').innerHTML,'');assert.equal(app.node('#pl-tl-status').textContent,'Paused at source media 01:36:52.');};
 app.ask('What happened here?');app.click('pl-tl-play');assert.match(app.node('#pl-tl-status').textContent,/Stepping completed passages/);const beforeModeCallback=app.allCallbacks.at(-1);app.click('pl-mode-estimated');paused();beforeModeCallback();paused();app.click('pl-mode-timeline');paused();
 for(const event of ['hide','pagehide','workspace-exit','close']){
  app.ask('What is uncertain?');app.click('pl-tl-play');const queued=app.allCallbacks.at(-1);
  if(event==='hide'){app.document.hidden=true;app.docListeners.visibilitychange();}else if(event==='pagehide')app.windowListeners.pagehide();else if(event==='workspace-exit')app.docListeners['matchmate:workspace-exit']();else app.click('pl-back');
  paused();queued();paused();if(event==='close'){app.window.PLAnnotations.open(()=>{});paused();}
 }
});

test('rendered source text, user questions and withheld reasons are escaped',()=>{
 const s=clone(sample);s.passages[0].title='<img src=x onerror=alert(1)>';s.passages[0].text='<svg onload=alert(1)>';s.passages[1].availableAt=null;s.passages[1].withheldReason='<script>oops</script>';const app=controller(s);app.window.PLAnnotations.open(()=>{});app.click('pl-tl-next');assert.doesNotMatch(app.node('#pl-tl-current').innerHTML,/<img|<svg/);assert.match(app.node('#pl-tl-current').innerHTML,/&lt;img/);assert.doesNotMatch(app.node('#pl-tl-withheld').innerHTML,/<script>/);app.ask('<img src=x onerror=alert(1)>');assert.doesNotMatch(app.node('#pl-tl-thread').innerHTML,/<img/);assert.match(app.node('#pl-tl-thread').innerHTML,/&lt;img/);
});

test('portable build is exact, idempotent and reads no ignored research; modular order and 44px controls remain explicit',()=>{
 assert.equal(read('preview.html'),build.html);assert.equal(read('pl-timeline-sample.js'),build.timelineScript);assert.equal(require('./build-preview.cjs').buildPreview().html,build.html);const original=fs.readFileSync;let researchReads=0;
 try{fs.readFileSync=function(file,...args){if(String(file).replaceAll('\\','/').includes('/output/')){researchReads++;throw Error('Research output forbidden during build');}return original.call(this,file,...args);};assert.equal(require('./build-preview.cjs').buildPreview().html,build.html);}finally{fs.readFileSync=original;}
 assert.equal(researchReads,0);const context={window:{}};vm.runInNewContext(build.timelineScript,context);assert.deepEqual(clone(context.window.PLTimelineSample),sample);const index=read('index.html');assert.ok(index.indexOf('pl-timeline-model.js')<index.indexOf('pl-timeline.js'));assert.ok(index.indexOf('pl-timeline.js')<index.indexOf('pl-annotations.js'));
 assert.doesNotMatch(read('pl-timeline.js'),/fetch\(|XMLHttpRequest|WebSocket|<img|<video|<audio/);assert.match(read('style.css'),/\.pl-timeline-list button\{[^}]*min-height:64px/);assert.match(read('style.css'),/#pl-tl-thread\{[^}]*overflow:visible;max-height:none/);assert.match(read('pl-timeline.js'),/step="any"/);
});

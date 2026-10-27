const {test}=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto'),vm=require('node:vm');
const build=require('./build-preview.cjs').buildPreview(),api=require('./pl-reconstruction-model.js'),annotationsApi=require('./pl-annotations-model.js'),sample=build.reconstructionSample,model=api.create(sample,build.plSample),MatchWorkspace=require('./match-workspace.js');
const read=name=>fs.readFileSync(path.join(__dirname,name),'utf8'),clone=o=>JSON.parse(JSON.stringify(o)),digest=b=>crypto.createHash('sha256').update(b).digest('hex');
const sourceDir=path.join(__dirname,'../../data/manual-match-annotations'),rawBytes=fs.readFileSync(path.join(sourceDir,'brentford-chelsea-2026-09-18-reconstruction.json')),raw=JSON.parse(rawBytes);

test('reconstruction is a separate receipt-backed manual artifact without source or named identity promotion',()=>{
 assert.equal(sample.source.reconstructionArtifact.sha256,digest(rawBytes));assert.equal(sample.source.reconstructionArtifact.bytes,rawBytes.length);
 const original=fs.readFileSync(path.join(sourceDir,raw.source.annotationArtifact.file));assert.equal(digest(original),build.plSample.source.artifact.sha256);assert.equal(sample.source.annotationArtifact.sha256,digest(original));
 assert.equal(raw.method.continuousReview,false);assert.equal(raw.method.calibration,'none');assert.equal(raw.method.uncertaintyRadius,8);assert.equal(raw.policy.liveKnownThen,false);
 assert.deepEqual(sample.participants.map(p=>[p.id,p.identity]),[['shirt27',null],['goalkeeper',null]]);
 assert.doesNotMatch(JSON.stringify(sample),/Anthony|Damsgaard|Schuster|\.jpg|C:[/\\]|"file"|retrospectiveReportClaims/);
});

test('all seven local screenshot hashes and DOM receipts match the approved source evidence',t=>{
 if(!raw.frames.every(f=>fs.existsSync(path.join(sourceDir,f.image.file))))return t.skip('Ignored local screenshots unavailable; portable model remains independently tested');
 const receiptBytes=fs.readFileSync(path.join(sourceDir,raw.source.captureReceipt.file)),receipts=JSON.parse(receiptBytes);
 assert.equal(digest(receiptBytes),raw.source.captureReceipt.sha256);assert.equal(receiptBytes.length,raw.source.captureReceipt.bytes);
 for(const [i,f] of raw.frames.entries()){const bytes=fs.readFileSync(path.join(sourceDir,f.image.file));assert.equal(digest(bytes),f.image.sha256);assert.equal(bytes.length,f.image.bytes);assert.equal(receipts[i].id,f.id);assert.equal(receipts[i].time,f.time);assert.deepEqual(receipts[i].state,f.dom);assert.equal(path.basename(receipts[i].file),path.basename(f.image.file));}
});

test('every reviewed endpoint is a broad manual point with exact captured clock/score and no future endpoint',()=>{
 const shirt=[[20,73],[20,72],[19,70],[18.5,69],[18,68],[18.5,67],[19,65]],keeper=[[3,45],[4,44],[4,43],[4,42],[3,44],[3,45],[3,46]];
 for(const [i,f] of sample.frames.entries())for(const [id,expected] of [['shirt27',shirt[i]],['goalkeeper',keeper[i]]]){const v=model.view(f.time,id),p=v.position;assert.deepEqual([p.x,p.y],expected);assert.equal(p.radius,8);assert.equal(p.kind,'reviewed_estimate');assert.deepEqual(p.evidenceIds,[f.id]);assert.deepEqual(p.bounds,[f.time,f.time]);assert.equal(p.usesFutureEndpoint,false);assert.equal(v.frame.clock,f.clock);assert.deepEqual(v.frame.score,[0,0]);}
});

test('only neighbouring same-track half-second pairs interpolate, disclosing the future endpoint without clock/score inference',()=>{
 for(let i=0;i<6;i++)for(const id of ['shirt27','goalkeeper']){const a=sample.frames[i],b=sample.frames[i+1],v=model.view(a.time+.25,id),p=v.position,pa=a.points.find(p=>p.participantId===id),pb=b.points.find(p=>p.participantId===id);assert.deepEqual([p.x,p.y],[(pa.x+pb.x)/2,(pa.y+pb.y)/2]);assert.equal(p.kind,'interpolated_estimate');assert.equal(v.frame,null);assert.equal(p.usesFutureEndpoint,true);assert.deepEqual(p.evidenceIds,[a.id,b.id]);assert.deepEqual(p.bounds,[a.time,b.time]);assert.match(p.disclosure,/neighbouring reviewed frames; retrospective reconstruction, not known-then/);}
 for(const delta of [-1e-8,1e-8])assert.equal(model.view(3716+delta,'shirt27').frame,null);
});

test('outside support, camera boundaries, unknown identities and invalid cursors never yield held or extrapolated positions',()=>{
 for(const time of [3715-1e-8,3718+1e-8,3714.5,3718.25,3718.5,NaN,Infinity,null,true,'3716'])for(const id of ['shirt27','goalkeeper'])assert.equal(model.view(time,id).position,null);
 for(const id of ['brentford:anthony','brentford:damsgaard','ball','unknown',null])assert.equal(model.view(3716,id).position,null);
 assert.equal(model.gap(3715),3718.25);assert.equal(model.adjacent(3715.25,-1),3715);assert.equal(model.adjacent(3715.25,1),3715.5);assert.equal(model.adjacent(3718.25,-1),3718);assert.equal(model.adjacent(3718,1),null);
});

test('unapproved actors, contacts, points, cuts, visibility, calibration and interpolation policies fail closed',()=>{
 const mutations=[s=>s.participants[0].identity='brentford:anthony',s=>s.participants.push({...s.participants[0],id:'ball'}),s=>s.frames[2].shotId='another-camera',s=>s.frames[2].points.pop(),s=>s.frames[2].points[0].participantId='goalkeeper',s=>s.frames[2].points[0].x=22,s=>s.frames[2].time=3716.1,s=>s.frames[2].dom.seeking=true,s=>s.frames[2].clock='60:24',s=>s.frames[2].score=[1,0],s=>s.frames[2].contact='goal',s=>s.cuts.push({after:'R03',before:'R04',bounds:[3716,3716.5]}),s=>s.policy.maxGapSeconds=1,s=>s.method.calibration='metres',s=>s.method.uncertaintyRadius=1,s=>s.source.annotationArtifact.sha256='0'.repeat(64)];
 for(const mutate of mutations){const value=clone(sample);mutate(value);assert.throws(()=>api.create(value,build.plSample),/PL reconstruction:/);}
 const value=clone(sample),instance=api.create(value,build.plSample);value.frames[0].points[0].x=90;assert.equal(instance.view(3715,'shirt27').position.x,20);
});

test('estimated answers retain positional evidence bounds, anonymity and unknowns; original observation eligibility remains independent',()=>{
 const a=model.answer(3716.25,'shirt27','Where is this player?');assert.deepEqual(a.evidenceIds,['R03','R04']);assert.deepEqual(a.bounds,[3716,3716.5]);assert.match(a.text,/clip bounds 3716–3716.5/);assert.match(a.text,/retrospective reconstruction, not known-then/);assert.doesNotMatch(a.text,/Anthony|Damsgaard|Schuster/);
 assert.match(model.answer(3718.25,'shirt27','Where is this player?').text,/position unknown/);
 assert.match(model.answer(3716.25,'goalkeeper','What has been observed?').text,/No reviewed frame exists at this exact cursor/);
 assert.match(model.answer(3716,'shirt27','What is missing here?').text,/not continuous or frame-complete/);
 for(const q of ['Who scored?','What does the scoreboard show?','What was the result?','What has been observed about the scorer?','Where is this player going next?','How fast is this player?'])assert.equal(model.answer(3718,'shirt27',q).kind,'unsupported');
 const original=annotationsApi.create(build.plSample);assert.equal(original.view(3717.22,'brentford:anthony').position.evidenceId,'E01');assert.equal(original.view(3716.25,'brentford:anthony').position,null);assert.ok(original.eligible(3716.25).some(o=>o.kind==='corner_setup'));assert.ok(!original.eligible(3716-1e-8).some(o=>o.kind==='crowded_wide_goalmouth'));
});

function controller(){
 const nodes=new Map(),docListeners={},windowListeners={},timers=new Map();let document,id=0,clock=0;
 function node(selector){if(nodes.has(selector))return nodes.get(selector);let html='',writes=0;const listeners={},n={id:selector.startsWith('#')?selector.slice(1):'',value:'',textContent:'',hidden:false,dataset:{},style:{},attributes:{},listeners,get innerHTML(){return html;},set innerHTML(value){html=value;writes++;},get htmlWrites(){return writes;},querySelector:node,setAttribute(k,v){this.attributes[k]=v;},addEventListener(type,fn){listeners[type]=fn;},focus(){document.activeElement=this;},replaceChildren(){this.innerHTML='';},insertBefore(child,before){this.moved=[child.id,before.id];},insertAdjacentHTML(where,value){this.innerHTML+=value;},closest:s=>s==='button'?n:null};nodes.set(selector,n);return n;}
 document={hidden:false,querySelector:node,addEventListener(type,fn){docListeners[type]=fn;}};
 const window={PLAnnotationsSample:build.plSample,PLAnnotationsModel:annotationsApi,PLReconstructionSample:sample,PLReconstructionModel:api,performance:{now:()=>clock},addEventListener(type,fn){windowListeners[type]=fn;}};
 vm.runInNewContext(read('pl-annotations.js'),{window,document,MatchWorkspace,setInterval(fn,interval){timers.set(++id,{fn,interval});return id;},clearInterval(id){timers.delete(id);}});
 return {node,window,document,docListeners,windowListeners,timers,seek(value){node('#pl-range').listeners.input({target:{value}});},select(value){node('#pl-player').listeners.change({target:{value}});},ask(value){node('#pl-draft').value=value;node('#pl-composer').listeners.submit({preventDefault(){}});},click(id,dataset={}){const target=node('#'+id);target.dataset=dataset;node('#pl-workspace').listeners.click({target});},advance(ms){clock+=ms;for(const {fn} of [...timers.values()])fn();}};
}

test('actual combined controller defaults to estimated and distinguishes exact, interpolated and outside-support displays',()=>{
 const app=controller();app.window.PLAnnotations.open(()=>{});const range=app.node('#pl-range'),writes=app.node('#pl-workspace').htmlWrites;
 assert.equal(range.value,'3715');assert.equal(app.node('#pl-mode-estimated').attributes['aria-pressed'],'true');assert.equal(app.node('#pl-reviewed-map').hidden,true);assert.equal(app.node('#pl-estimated-map').hidden,false);assert.equal(app.node('#pl-track-shirt27').dataset.kind,'reviewed_estimate');assert.match(app.node('#pl-context').innerHTML,/Video reconstruction · Estimated positions/);assert.doesNotMatch(app.node('#pl-player').innerHTML,/Anthony|Damsgaard/);
 assert.match(app.node('#pl-question-intro').textContent,/anonymous participant/);assert.deepEqual(app.node('#pl-inspector').moved,['pl-transport','pl-estimated-map']);
 app.seek(3715.25);assert.equal(app.node('#pl-track-shirt27').dataset.kind,'interpolated_estimate');assert.match(app.node('#pl-score').textContent,/Unknown/);assert.match(app.node('#pl-match-clock').textContent,/Unknown/);assert.match(app.node('#pl-interpolation').textContent,/second frame is after this cursor/);app.ask('Where is this player?');assert.match(app.node('#pl-thread').innerHTML,/R01, R02/);assert.match(app.node('#pl-thread').innerHTML,/ESTIMATED REPLAY/);
 app.click('pl-track-goalkeeper',{plParticipant:'goalkeeper'});assert.equal(app.node('#pl-thread').innerHTML,'');assert.equal(app.node('#pl-track-goalkeeper').attributes['aria-pressed'],'true');
 app.click('pl-gap');for(const id of ['shirt27','goalkeeper']){assert.equal(app.node('#pl-track-'+id).hidden,true);assert.equal(app.node('#pl-uncertainty-'+id).hidden,true);}assert.equal(range.value,'3718.25');assert.match(app.node('#pl-position').textContent,/unknown/);assert.equal(app.node('#pl-facts').innerHTML,'');
 app.click('pl-previous');assert.equal(range.value,'3718');assert.equal(app.node('#pl-match-clock').textContent,'60:25');assert.equal(app.node('#pl-score').textContent,'0–0');assert.equal(app.node('#pl-track-goalkeeper').hidden,false);assert.equal(app.node('#pl-range'),range);assert.equal(app.node('#pl-workspace').htmlWrites,writes);
});

test('mode changes preserve original capture scope and isolate named selections and cursor-bound questions',()=>{
 const app=controller();app.window.PLAnnotations.open(()=>{});app.seek(3716.25);app.select('goalkeeper');app.ask('What is missing here?');app.click('pl-mode-reviewed');
 assert.equal(app.node('#pl-thread').innerHTML,'');assert.equal(app.node('#pl-range').value,'3707.22');assert.equal(app.node('#pl-range').max,'3730');assert.equal(app.node('#pl-player').value,'brentford:anthony');assert.match(app.node('#pl-player').innerHTML,/Jaidon Anthony/);assert.equal(app.node('#pl-goalmouth').dataset.active,'true');
 assert.match(app.node('#pl-question-intro').textContent,/source-joined player/);assert.deepEqual(app.node('#pl-inspector').moved,['pl-transport','pl-observations']);
 app.seek(3717.21);assert.equal(app.node('#pl-goalmouth').dataset.active,'false');app.click('pl-next');assert.equal(app.node('#pl-range').value,'3717.22');assert.equal(app.node('#pl-goalmouth').dataset.active,'true');app.ask('Where is this player?');assert.match(app.node('#pl-thread').innerHTML,/REVIEWED CAPTURES/);
 app.click('pl-mode-estimated');assert.equal(app.node('#pl-thread').innerHTML,'');assert.equal(app.node('#pl-range').value,'3716.25');assert.equal(app.node('#pl-player').value,'goalkeeper');assert.doesNotMatch(app.node('#pl-player').innerHTML,/Jaidon|Mikkel/);app.ask('<img src=x onerror=alert(1)>');assert.doesNotMatch(app.node('#pl-thread').innerHTML,/<img/);assert.match(app.node('#pl-thread').innerHTML,/unsupported/);
});

test('playback advances by elapsed time at one times speed and stops for pause, seek, question, mode, hide, exit and end',()=>{
 const app=controller();app.window.PLAnnotations.open(()=>{});app.click('pl-play');assert.match(app.node('#pl-announcement').textContent,/Playing estimated passage at 1×/);assert.equal([...app.timers.values()][0].interval,16);app.advance(750);assert.equal(app.node('#pl-range').value,'3715.75');assert.equal(app.node('#pl-track-shirt27').dataset.kind,'interpolated_estimate');app.click('pl-play');assert.match(app.node('#pl-announcement').textContent,/Paused at clip/);assert.equal(app.timers.size,0);assert.equal(app.node('#pl-thread').innerHTML,'');
 app.click('pl-play');app.ask('Where is this player?');assert.equal(app.timers.size,0);app.click('pl-play');app.seek(3716);assert.equal(app.timers.size,0);assert.equal(app.node('#pl-thread').innerHTML,'');app.click('pl-play');app.click('pl-mode-reviewed');assert.equal(app.timers.size,0);app.click('pl-mode-estimated');
 for(const stop of [()=>{app.document.hidden=true;app.docListeners.visibilitychange();},()=>app.windowListeners.pagehide(),()=>app.docListeners['matchmate:workspace-exit']()]){app.click('pl-play');stop();assert.equal(app.timers.size,0);app.document.hidden=false;}
 app.seek(3715);app.click('pl-play');app.advance(5000);assert.equal(app.node('#pl-range').value,'3718');assert.equal(app.timers.size,0);assert.equal(app.node('#pl-play').attributes['aria-pressed'],'false');
});

test('portable and modular reconstruction scripts are exact, offline and do not embed screenshots or source-media paths',()=>{
 assert.equal(read('preview.html'),build.html);assert.equal(read('pl-reconstruction-sample.js'),build.reconstructionScript);
 const context={window:{}};vm.runInNewContext(build.reconstructionScript,context);assert.deepEqual(JSON.parse(JSON.stringify(context.window.PLReconstructionSample)),sample);
 const index=read('index.html');assert.ok(index.indexOf('pl-annotations-model.js')<index.indexOf('pl-reconstruction-model.js'));assert.ok(index.indexOf('pl-reconstruction-model.js')<index.indexOf('pl-annotations.js'));
 assert.doesNotMatch(read('pl-reconstruction-model.js')+read('pl-annotations.js'),/fetch\(|XMLHttpRequest|WebSocket|<img|<video/);assert.doesNotMatch(build.reconstructionScript,/\.jpg|"file"|C:[/\\]/);
});

test('exact-frame assertions detect a mutant that promotes a prior frame clock/score into an interpolated cursor',()=>{
 const source=read('pl-reconstruction-model.js'),before='data.frames.find(f=>f.time===time)';assert.ok(source.includes(before));const context={module:{exports:{}}};vm.runInNewContext(source.replace(before,'data.frames.find(f=>f.time<=time)'),context);assert.throws(()=>assert.equal(context.module.exports.create(sample,build.plSample).view(3715.25,'shirt27').frame,null),assert.AssertionError);
});

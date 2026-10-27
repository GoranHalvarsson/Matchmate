(function(root){
'use strict';
const bundle=root.RecordedSample,model=root.RecordedReplayModel.create(bundle.sample),events=root.RecordedEventsModel.create(bundle.sample,bundle.events);
const panel=document.querySelector('#recorded-workspace'),node=id=>panel.querySelector('#recorded-'+id),esc=MatchWorkspace.esc;
let state=model.seek(model.sample.start),selected=model.roster.find(p=>p.team==='home').id,team='home',timer=null,onBack=null,opened=false,eventKey=null,listKey=null,renderedIds=[];
const text=(el,value)=>{if(el.textContent!==value)el.textContent=value;};
panel.innerHTML=MatchWorkspace.shell('recorded');
node('pitch-slot').innerHTML=MatchWorkspace.pitch({model,recorded:true});
// Keep the controls next to their pitch in DOM order as the columns stack.
panel.querySelector('.pitch-panel').append(panel.querySelector('.recorded-timeline'));
panel.querySelector('.pitch-panel').append(panel.querySelector('.recorded-events'));
node('speaker-name').textContent='The recorded match';
node('speaker-subtitle').textContent='RECORDED TRACKING REPLAY · RETROSPECTIVE, SOURCE-DERIVED';
node('avatar').textContent='↗';
node('catalog').innerHTML='<div class="eyebrow">RECORDED SAMPLE</div>'+MatchWorkspace.fixture({tag:'div',date:esc(model.sample.dateTime.slice(0,10)),status:'First half',homeCrest:'',home:esc(model.sample.home.name),score:'v',clock:'01:00–04:00',awayCrest:'',away:esc(model.sample.away.name),venue:esc(model.sample.competition),action:'Tracking sample'})+'<p class="catalog-caption">A-League technical sample · separate from the Premier League explorer and fictional conversation demo.</p>';
// Event Sources exposes static provenance only, never the future annotation list.
const receipt=bundle.eventManifest.receipt;
node('context').innerHTML=MatchWorkspace.context({state:'RECORDED SAMPLE',title:`<span id="recorded-title" tabindex="-1">${esc(model.sample.home.name)} v ${esc(model.sample.away.name)}</span>`,subtitle:'First half 01:00–04:00 · Recorded tracking replay · Retrospective, source-derived',sources:'<details class="workspace-sources recorded-sources"><summary class="text-button">Sources & coverage ↗</summary><p>Data: SkillCorner Open Data. Broadcast-derived positions retain source detected/estimated flags; source identity joins are not independent identity verification. No live feed, Premier League positions or video is attached.</p><p>Tracking: the approved 1,801 samples and 22 observed source-roster identities. The original receipt hashes the first 2,411 complete tracking lines, not the whole large file. Source frames and printed match clocks remain separate.</p><p>Completed possessions: a small curated selection of source-classified player_possession actor/interval annotations, wholly inside first-half 01:00–04:00. Only intervals with matched endpoints, exact frame/printed-clock agreement and full player-position coverage are included. Selection takes the first three eligible rows per source team by frame start, frame end, then event ID; fewer when insufficient.</p><p>Recorded analysis · Interval completion is a retrospective replay spoiler cutoff. It does not prove provider live arrival or what was known then. No targets, pass outcomes, event coordinates, scores, tactics or movement metrics. Other event categories remain deferred; this is not a complete incident feed.</p><details><summary>Event prefix receipt</summary><pre tabindex="0" aria-label="Event prefix receipt">'+esc(JSON.stringify({pin:bundle.eventManifest.pin,url:receipt.url,resolvedUrl:receipt.resolvedUrl,status:receipt.status,scope:receipt.scope,bytes:receipt.bytes,sha256:receipt.sha256},null,2))+'</pre></details><p>'+esc(bundle.manifest.scope)+'</p><details><summary>Original tracking import receipt (historical scope)</summary><p>The eventsImported:false flag describes the unchanged tracking import, before the separate completed-possession artifact.</p><pre tabindex="0" aria-label="Pinned tracking provenance and byte receipts">'+esc(JSON.stringify(bundle.manifest,null,2))+'</pre></details><details><summary>SkillCorner MIT licence — full notice</summary><pre tabindex="0" aria-label="Full SkillCorner MIT licence">'+esc(bundle.license)+'</pre></details></details>'});
node('journey').innerHTML=MatchWorkspace.journey('<span>Moment notes · each answer keeps its sample clock</span>','<span>Local computed answers · no AI</span>');
node('suggestions').innerHTML='<button data-recorded-question="What happened before this moment?">What happened before this moment?</button><button data-recorded-question="What did this player do?">What did this player do?</button><button data-recorded-question="Describe the selected player’s position">Describe this player’s position</button><button data-recorded-question="What is available at this cursor?">What is available here?</button>';
node('draft').setAttribute('aria-label','Question about this recorded sample. Use a suggested question for supported local answers.');

function announce(value){text(node('announcement'),value);}
function stop(){if(timer!==null)clearInterval(timer);timer=null;state={...state,playing:false};text(node('play'),'▶ Play');node('play').setAttribute('aria-pressed','false');node('play').disabled=state.time>=model.sample.end||model.at(state.time).fullGap;}
function paintEvents(view){
 const facts=events.eligible(state.time),key=facts.map(e=>e.sourceId).join('|');
 if(eventKey&&!facts.some(e=>e.sourceId===eventKey))eventKey=null;
 if(key!==listKey){
  // Append new completions without replacing focused older rows. A rewind that
  // removes rows restores focus to the sample clock before removing its node.
  const appendOnly=renderedIds.every((id,index)=>facts[index]?.sourceId===id);
  if(!appendOnly&&node('event-list').contains(document.activeElement))node('range').focus({preventScroll:true});
  const rows=(appendOnly?facts.slice(renderedIds.length):facts).map(e=>`<li><button data-recorded-event="${esc(e.sourceId)}" id="recorded-event-${esc(e.sourceId)}" aria-pressed="false"><span>${esc(e.timeStart)}–${esc(e.timeEnd)}</span><strong>${esc(e.actorName)}</strong><small>Source-classified possession · ${esc(e.teamName)}</small></button></li>`).join('');
  if(appendOnly)node('event-list').insertAdjacentHTML('beforeend',rows);else node('event-list').innerHTML=rows;
  renderedIds=facts.map(e=>e.sourceId);
  listKey=key;
 }
 for(const e of facts)node('event-'+e.sourceId).setAttribute('aria-pressed',String(e.sourceId===eventKey));
 node('event-empty').hidden=facts.length>0;
 text(node('event-gap'),view.fullGap?'Current positions are unavailable. Earlier completed annotations remain available; no movement through this gap is inferred.':'Recorded analysis · A curated selection of completed source-classified intervals.');
}
function paint(){
 const view=model.at(state.time),sampleClock=view.frame?model.clock(view.frame.clock):model.clock(state.time);
 const losingFocus=view.players.find(p=>!p.available&&document.activeElement===node('player-'+p.id));if(losingFocus)team=losingFocus.team;
 text(node('time'),model.clock(state.time));text(node('roster-clock'),model.clock(state.time));
 node('range').value=state.time;node('range').setAttribute('aria-valuetext','First half '+model.clock(state.time));
 for(const p of view.players){
  const marker=node('player-'+p.id),row=node('row-'+p.id),button=row.querySelector('button');
  if(!p.available&&document.activeElement===marker){row.hidden=false;button.focus({preventScroll:true});}
  marker.hidden=!p.available;marker.dataset.quality=p.quality;
  marker.setAttribute('aria-pressed',String(p.id===selected));button.setAttribute('aria-pressed',String(p.id===selected));
  marker.setAttribute('aria-label',`${p.name}, shirt ${p.number??'unknown'}, ${p.teamName}. ${model.qualityText(p.quality)}. Source sample ${sampleClock}.`);
  if(p.available){const point=model.project(p);marker.style.left=point.left+'%';marker.style.top=point.top+'%';}
  row.hidden=p.team!==team;row.classList.toggle('selected',p.id===selected);
  text(node('quality-'+p.id),model.qualityText(p.quality));
 }
 const player=view.players.find(p=>p.id===selected),last=player.available?null:model.lastKnown(state.time,selected);
 node('player').value=String(selected);
 text(node('selected-player'),`${player.name} · ${player.teamName} · shirt ${player.number??'unknown'}`);
 text(node('position-detail'),player.available?`${model.qualityText(player.quality)} · Sample ${sampleClock} · x ${player.x} m, y ${player.y} m${player.outOfBounds?' · Outside nominal pitch bounds; source value preserved':''}`:`Position unavailable${last?' · Last known sample: '+model.clock(last.time):' · No earlier position in this sample'}`);
 const ball=node('ball');ball.hidden=!view.ball.available;ball.dataset.quality=view.ball.quality;
 if(view.ball.available){const point=model.project(view.ball);ball.style.left=point.left+'%';ball.style.top=point.top+'%';}
 const ballText=view.ball.available?`Ball · ${model.qualityText(view.ball.quality)} · Sample ${sampleClock}${view.ball.outOfBounds?' · Outside nominal pitch bounds':''}`:'Ball position unavailable';
 ball.setAttribute('aria-label',ballText);text(node('ball-status'),ballText);
 node('pitch-empty').hidden=!view.fullGap;
 panel.querySelectorAll('[data-recorded-team]').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.recordedTeam===team)));
 text(node('coverage-current'),`${model.clock(state.time)} · ${view.frameState.replaceAll('-',' ')} · ${view.count}/${model.roster.length} player positions available${view.frame?' · Raw frame '+view.frame.frameId:''}`);
 node('gap-actions').hidden=!view.fullGap;
 const previous=model.adjacent(state.time,-1),next=model.adjacent(state.time,1);
 node('previous').disabled=previous===null;node('next').disabled=next===null;node('continue').disabled=state.time>=model.sample.end;
 text(node('gap-navigation'),[previous===null?'No earlier available positions.':'',next===null?'No later available positions in this sample.':''].filter(Boolean).join(' '));
 node('play').disabled=state.time>=model.sample.end||(view.fullGap&&!state.playing);
 node('play').setAttribute('aria-pressed',String(state.playing));text(node('play'),state.playing?'Ⅱ Pause':'▶ Play');
 node('play').title=state.time>=model.sample.end?'Dataset end. Return to an earlier sample to play.':view.fullGap?'Use Continue clock through gap or choose available positions.':'';
 paintEvents(view);
}
function clearNotes(){node('thread').replaceChildren();text(node('status'),'Cursor changed. New answers use the selected player and current sample clock.');}
function seek(time){stop();state=model.seek(time);eventKey=null;clearNotes();paint();announce('Paused at first-half '+model.clock(state.time)+(model.at(state.time).fullGap?'. Positions unavailable.':'.'));}
function select(id){const player=model.roster.find(p=>p.id===Number(id));if(!player)return;stop();if(selected!==player.id){clearNotes();eventKey=null;}selected=player.id;team=player.team;paint();announce(`${player.name} selected. ${model.qualityText(model.at(state.time).players.find(p=>p.id===selected).quality)}.`);}
function tick(elapsed){const previous=state;state=model.advance(state,elapsed);paint();if(previous.playing&&!state.playing){stop();announce(state.time>=model.sample.end?'Dataset end at '+model.clock(state.time)+'. Playback stopped.':'Positions unavailable at '+model.clock(state.time)+'. Playback paused. Choose continue, skip or return.');}}
function run(throughGap=false){stop();state=throughGap?model.continueGap(state):model.play(state);paint();if(!state.playing)return;announce(throughGap?'Continuing clock through this gap. Positions remain unavailable.':'Playing recorded samples.');let previous=performance.now();timer=setInterval(()=>{const now=performance.now();tick((now-previous)/1000);previous=now;},100);}
function message(value,user,sourceIds=[]){node('thread').insertAdjacentHTML('beforeend',MatchWorkspace.message(value,user,`SAMPLE NOTE · FIRST HALF ${model.clock(state.time)} · ${model.roster.find(p=>p.id===selected).name}`,'SkillCorner recorded sample · local computation · no AI'+(sourceIds.length?' · Recorded analysis · '+sourceIds.join(', '):'')));}
function ask(question){
 if(!question.trim())return;stop();message(question,true);
 const kind=root.RecordedEventsModel.classify(question);
 if(kind==='position')message(model.explain(state.time,selected),false);
 else if(kind==='history'||kind==='player'){const answer=events.answer(state.time,selected,question),gap=model.at(state.time).fullGap?'Current positions are unavailable. These earlier annotations do not establish movement through the gap.\n\n':'';message(gap+answer.text+'\n\nRecorded source analysis, not a live feed. This selection covers possession intervals only. Targets, pass outcomes and causal explanations are unavailable.',false,answer.sourceIds);}
 else if(kind==='coverage'){const view=model.at(state.time),facts=events.eligible(state.time);message(`At first-half ${model.clock(state.time)}: ${view.count}/${model.roster.length} player positions are available. ${view.ball.available?'A ball position is available ('+model.qualityText(view.ball.quality).toLowerCase()+').':'Ball position is unavailable.'} Source roster names remain available. ${facts.length?'Completed source-classified possessions from the curated selection are available below the pitch controls.':'No completed annotations are available in this curated selection at this cursor; this does not mean no football happened.'} Score, goals, pass outcomes, tactical roles, speed and distance are not supplied. This is a retrospective, source-derived sample.`,false);}
 else message('This local preview supports completed source-classified possessions, the selected player’s position and coverage at this cursor. It does not interpret other free-text questions. Score, goals, next passes, results, tactics, speed, distance and future outcomes are unavailable. Use a suggested question.',false);
 node('thread').scrollTop=node('thread').scrollHeight;text(node('status'),'Moment note saved at first-half '+model.clock(state.time)+'.');
}
function open(back){stop();opened=true;onBack=back;panel.hidden=false;paint();node('title').focus({preventScroll:true});announce('Recorded sample paused at first-half '+model.clock(state.time)+'.');}
function close(){stop();panel.hidden=true;opened=false;const back=onBack;onBack=null;if(back)back();}
node('range').addEventListener('input',e=>seek(Number(e.target.value)));
node('player').addEventListener('change',e=>select(e.target.value));
node('composer').addEventListener('submit',e=>{e.preventDefault();ask(node('draft').value);node('draft').value='';});
panel.querySelector('.recorded-replay').addEventListener('toggle',e=>{if(!e.target.open)stop();});
panel.addEventListener('click',e=>{
 const b=e.target.closest('button');if(!b)return;
 if(b.id==='recorded-back'){close();return;}
 if(b.dataset.recordedEvent){const event=events.eligible(state.time).find(e=>e.sourceId===b.dataset.recordedEvent);if(event){select(event.actorId);eventKey=event.sourceId;paint();stop();message('Describe this completed possession.',true);message(events.describe(event)+'\n\nRecorded source analysis, not a live feed. This selection covers possession intervals only. No target, outcome or causal explanation is supplied.'+(model.at(state.time).fullGap?' Current positions are unavailable; no movement through the gap is inferred.':''),false,[event.sourceId]);}return;}
 if(b.dataset.recordedPlayer){select(b.dataset.recordedPlayer);return;}
 if(b.dataset.recordedTeam){team=b.dataset.recordedTeam;paint();return;}
 if(b.dataset.recordedQuestion){ask(b.dataset.recordedQuestion);return;}
 if(b.id==='recorded-play'){if(state.playing)stop();else run();}
 if(b.id==='recorded-start')seek(model.sample.start);
 if(b.id==='recorded-end')seek(model.sample.end);
 if(b.id==='recorded-next'||b.id==='recorded-previous'){const time=model.adjacent(state.time,b.id==='recorded-next'?1:-1);if(time!==null){seek(time);node('range').focus({preventScroll:true});}}
 if(b.id==='recorded-continue')run(true);
 if(b.id==='recorded-ask-moment')ask('Describe the selected player’s position');
});
document.addEventListener('visibilitychange',()=>{if(document.hidden)stop();});
root.addEventListener('pagehide',stop);
root.RecordedReplay={open,close,stop,isOpen:()=>opened};
paint();
})(window);

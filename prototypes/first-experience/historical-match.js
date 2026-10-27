(function(root){
 'use strict';
 const panel=document.querySelector('#historical-match');
 let context=null,onBack=null,recognition=null,generation=0,listening=false;
 const node=id=>document.querySelector('#historical-'+id);
 function cancel(){generation++;if(recognition){recognition.onresult=null;recognition.onend=null;recognition.onerror=null;recognition.abort();recognition=null;}listening=false;node('draft').readOnly=false;node('send').disabled=false;node('mic').setAttribute('aria-pressed','false');}
 function message(text,role){node('thread').insertAdjacentHTML('beforeend',MatchWorkspace.message(text,role==='user','THE MATCH','Source: pinned historical results · local computed facts'));node('thread').scrollTop=node('thread').scrollHeight;}
 function ask(text){if(!context||listening||!text.trim())return;message(text,'user');message(HistoricalMatchModel.answer(context,text),'answer');}
 function close(){cancel();panel.hidden=true;const back=onBack;context=null;onBack=null;if(back)back();}
 function open(value,back){cancel();context=value;onBack=back;panel.hidden=false;node('thread').replaceChildren();node('draft').value='';
 const esc=MatchWorkspace.esc,home=value.season.clubs.find(c=>c.id===value.match.home),away=value.season.clubs.find(c=>c.id===value.match.away),score=value.match.hg+'–'+value.match.ag;
 function crest(club){const path=root.LeagueModel?.crestFor(club.id,value.seasonKey);return `<span class="crest reference-crest" aria-hidden="true">${esc(club.initials||club.name.slice(0,3))}${path?`<img src="${esc(path)}" alt="">`:''}</span>`;}
 node('catalog').innerHTML='<div class="filters" aria-label="Match time"><button aria-pressed="true">Past</button><button disabled title="Live data not loaded">Live</button><button disabled title="Upcoming data not loaded">Upcoming</button></div>'+MatchWorkspace.fixture({tag:'div',attrs:'aria-label="Selected historical fixture"',date:esc(value.match.date),status:'FT',homeCrest:crest(home),home:esc(home.name),score,clock:'FULL TIME',awayCrest:crest(away),away:esc(away.name),venue:esc(value.seasonKey)+' · Premier League',action:'Selected match'})+'<p class="catalog-caption">Completed result · selected season</p>';
 node('context').innerHTML=MatchWorkspace.context({state:'PAST',title:`<span id="historical-title" tabindex="-1">${esc(home.name)} v ${esc(away.name)}</span>`,subtitle:`<span id="historical-scope">${esc(value.match.date)} · Full time · ${score}</span>`,sources:'<details class="workspace-sources"><summary class="text-button">Sources ↗</summary><p>Local computed answers, not AI. Pinned results from two Premier League seasons; no lineups, tracking, venues or match events. <a href="assets/seasons/manifest.json" target="_blank" rel="noopener">Provenance and coverage ↗</a></p></details>'})+'<div class="voice-detail">The result. The form. The table at full time.</div>';
 node('journey').innerHTML=MatchWorkspace.journey('<span>Your football, in conversation</span>','<span>Historical match · local facts</span>');
 const emptyModel={players:[],events:[],clock:()=>''};node('pitch-slot').innerHTML=MatchWorkspace.pitch({model:emptyModel,expanded:true,unavailable:true,home:home.name,away:away.name,score});
 node('suggestions').innerHTML=['pre-match form','table impact','prior meetings'].map(q=>`<button data-historical-question="${q}">${q[0].toUpperCase()+q.slice(1)}</button>`).join('');
 panel.querySelectorAll('.reference-crest img').forEach(img=>img.addEventListener('error',()=>img.remove()));
 message(HistoricalMatchModel.answer(value,'result'),'answer');node('status').textContent='Local computed answers · Browser dictation is optional.';node('title').focus();}
 panel.addEventListener('click',e=>{
  if(e.target.closest('#historical-back')){close();return;}
  const chip=e.target.closest('[data-historical-question]');if(chip){ask(chip.dataset.historicalQuestion);return;}
  if(!e.target.closest('#historical-mic')||!context)return;
  if(listening){cancel();node('status').textContent='Dictation stopped. Review your draft before sending.';return;}
  const Speech=root.SpeechRecognition||root.webkitSpeechRecognition;if(!Speech){node('status').textContent='Dictation is unavailable in this browser. Type your question instead.';return;}
  const ticket=++generation,base=node('draft').value;let completed='';
  try{recognition=new Speech();recognition.interimResults=true;recognition.lang='en-GB';listening=true;node('draft').readOnly=true;node('send').disabled=true;node('mic').setAttribute('aria-pressed','true');node('status').textContent='Listening. Stop dictation before editing your draft. Nothing sends automatically.';
   recognition.onresult=e=>{if(ticket!==generation||!context)return;let interim='';for(let i=e.resultIndex;i<e.results.length;i++){if(e.results[i].isFinal)completed+=e.results[i][0].transcript+' ';else interim+=e.results[i][0].transcript;}node('draft').value=[base,(completed+interim).trim()].filter(Boolean).join(' ');};
   recognition.onerror=()=>{if(ticket!==generation)return;cancel();node('status').textContent='Dictation could not continue. Your draft is kept; type or try again.';};
   recognition.onend=()=>{if(ticket!==generation)return;cancel();node('status').textContent='Review your draft, then send when ready.';};recognition.start();
  }catch{cancel();node('status').textContent='Dictation could not start. Type your question instead.';}
 });
 node('composer').addEventListener('submit',e=>{e.preventDefault();if(listening)return;const text=node('draft').value;ask(text);node('draft').value='';});
 document.addEventListener('visibilitychange',()=>{if(document.hidden){cancel();node('status').textContent='Dictation stopped while this page was hidden. Your draft is kept.';}});
 root.addEventListener('pagehide',cancel);
 root.HistoricalMatch={open,close,isOpen:()=>context!==null};
})(window);

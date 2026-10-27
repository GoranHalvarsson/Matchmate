'use strict';
(() => {
  const model=window.ReplayModel;
  let time=0, selectedPlayer='H7', timer=null, expanded=true, followBall=false, statsTeam='H';
  const baseRender=render;
  const slot=document.createElement('div');slot.id='pitch-replay';
  $('#context').after(slot);
  function stop(){if(timer)clearInterval(timer);timer=null;const play=$('#replay-play');if(play)play.textContent='▶ Play';}
  function paint(){
    const f=model.frame(time);
    f.players.forEach(p=>{const el=$(`#player-${p.id}`);if(!el)return;el.style.left=(p.x/105*100)+'%';el.style.top=(p.y/68*100)+'%';el.setAttribute('aria-pressed',String(p.id===selectedPlayer));});
    const picker=$('#replay-player');if(picker)picker.value=selectedPlayer;
    const ball=$('#replay-ball');if(!ball)return;ball.style.left=(f.ball.x/105*100)+'%';ball.style.top=(f.ball.y/68*100)+'%';
    $('#replay-time').textContent=model.clock(time);$('#replay-range').value=time;$('#replay-range').setAttribute('aria-valuetext',model.clock(time));
    $('#replay-event').textContent=f.events[f.events.length-1].text;
    paintStats();
    $('#selected-player').textContent=model.players.find(p=>p.id===selectedPlayer).name+' · '+model.players.find(p=>p.id===selectedPlayer).role;
  }

  function paintStats(){
    const rows=model.stats(time).filter(p=>p.team===statsTeam);
    $('#stats-clock').textContent=model.clock(time);
    document.querySelectorAll('[data-stats-team]').forEach(b=>b.setAttribute('aria-pressed',String(b.dataset.statsTeam===statsTeam)));
    const body=$('#stats-rows');
    // Keep focused buttons and scroll position stable while the clock runs.
    if(body.dataset.team!==statsTeam){body.dataset.team=statsTeam;body.innerHTML=rows.map(p=>`<tr id="stat-${p.id}"><th scope="row"><button data-player="${p.id}" aria-label="Focus ${p.name}">${p.number} <span>${p.role}</span></button></th><td></td><td></td><td></td><td></td><td></td><td></td></tr>`).join('');}
    rows.forEach(p=>{const row=$('#stat-'+p.id);row.classList.toggle('selected',p.id===selectedPlayer);row.querySelector('button').setAttribute('aria-pressed',String(p.id===selectedPlayer));const values=[p.distance.toFixed(1),p.speed.toFixed(1),String(p.completed)+(p.pending?' (+1 pending)':''),p.missed,p.tackles,p.interceptions];row.querySelectorAll('td').forEach((cell,i)=>cell.textContent=values[i]);});
    const b=model.ballStats(time),pos=model.frame(time).ball;
    $('#follow-ball').setAttribute('aria-pressed',String(followBall));
    $('#ball-readout').textContent=followBall?`Ball · ${b.speed.toFixed(1)} km/h · ${b.distance.toFixed(1)} m in clip · ${b.passes} ${b.passes===1?'pass':'passes'} released`:'';
    $('#ball-path').setAttribute('points',followBall?b.trail.map(p=>p.x+','+p.y).join(' '):'');
    const halo=$('#ball-halo');halo.hidden=!followBall;halo.style.left=pos.x/105*100+'%';halo.style.top=pos.y/68*100+'%';
  }
  function mount(){
    if(speaker!=='match'){slot.innerHTML='';return;}
    if(selected!=='live'){slot.innerHTML=`<p class="pitch-unavailable">${selected==='past'?'No tracking sample is attached to this past fixture.':'This fixture has not started. No replay is available.'} <button data-pitch-open>Try the synthetic live replay →</button></p>`;return;}
    slot.innerHTML=MatchWorkspace.pitch({model,time,expanded});
    $('.replay').addEventListener('toggle',e=>{expanded=e.target.open;if(!expanded)stop();});
    $('#replay-player').addEventListener('change',e=>{stop();selectedPlayer=e.target.value;statsTeam=selectedPlayer[0];paint();});
    $('#replay-range').addEventListener('input',e=>{stop();time=Number(e.target.value);paint();});paint();
  }
  render=function(){stop();baseRender();mount();if(speaker==='guide')$('#suggestions').insertAdjacentHTML('beforeend','<button class="pitch-entry" data-pitch-open>Explore a moment on the pitch ↗</button>');};
  document.addEventListener('click',e=>{
    const b=e.target.closest('button');if(!b)return;
    if(b.hasAttribute('data-pitch-open')){expanded=true;open('match','live');slot.scrollIntoView({behavior:scrollBehavior(),block:'start'});}
    if(b.dataset.player){stop();selectedPlayer=b.dataset.player;statsTeam=selectedPlayer[0];paint();}
    if(b.dataset.statsTeam){statsTeam=b.dataset.statsTeam;paint();}
    if(b.id==='follow-ball'){followBall=!followBall;paint();}
    if(b.dataset.replayTime!==undefined){stop();time=Number(b.dataset.replayTime);paint();}
    if(b.id==='replay-play'){if(timer){stop();return;}if(time>=20)time=0;let last=performance.now();timer=setInterval(()=>{const now=performance.now();time=Math.min(20,time+(now-last)/1000);last=now;paint();if(time>=20)stop();},100);b.textContent='Ⅱ Pause';}
    if(b.id==='replay-prev'||b.id==='replay-next'){stop();const times=model.events.map(e=>e.time);time=b.id==='replay-prev'?(times.filter(t=>t<time-.05).pop()??0):(times.find(t=>t>time+.05)??20);paint();}
    if(b.id==='replay-edge'){stop();time=20;paint();}
    if(b.id==='ask-moment'){
      stop();const question=`At ${model.clock(time)}, explain ${model.players.find(p=>p.id===selectedPlayer).name}’s position.`;
      threads[key()].push({text:question,user:true},{text:model.explain(time,selectedPlayer),user:false,note:`Synthetic pitch frame · ${model.clock(time)} · ${selectedPlayer}`});
      topics.set(key(),question);hasAnswered=true;render();$('#thread').scrollIntoView({behavior:scrollBehavior(),block:'center'});
    }
  });
  document.addEventListener('visibilitychange',()=>{if(document.hidden)stop();});
  document.addEventListener('matchmate:workspace-exit',stop);
  window.addEventListener('pagehide',stop);
  render();
})();

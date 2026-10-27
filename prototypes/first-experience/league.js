(function () {
  'use strict';
  let state = {season:'2015/16',selected:'west-ham'};
  const workspace = document.querySelector('#league-workspace');
  const demo = document.querySelector('main');
  const source = '../../wiki/SEASON-FOUNDATION-INVENTORY.md';
  const seasons = new Map(LeagueModel.filterSeasons().map(s=>[s,SeasonTableModel.createSeason(SeasonResults[s],LeagueModel.members(s))]));
  let snapshotIndex=seasons.get(state.season).dates.length,playTimer=null,playSpeed=1,activeQuestion=null,mapCollapsed=false;
  const currentSeason=()=>seasons.get(state.season);
  const displayDate=date=>date?new Intl.DateTimeFormat('en-GB',{day:'numeric',month:'short',year:'numeric',timeZone:'UTC'}).format(new Date(date+'T00:00:00Z')):'Before first match';
  function stopSeason(){if(playTimer!==null)clearInterval(playTimer);playTimer=null;const button=document.querySelector('#season-play');if(button){button.textContent=snapshotIndex===currentSeason().dates.length?'↻ Replay from start':'▶ Play season';button.setAttribute('aria-pressed','false');}}
  function startSeason(){stopSeason();if(snapshotIndex===currentSeason().dates.length){snapshotIndex=0;updateSnapshot(false);}playTimer=setInterval(()=>{snapshotIndex++;updateSnapshot(true);if(snapshotIndex===currentSeason().dates.length)stopSeason();},1600/playSpeed);document.querySelector('#season-play').textContent='Ⅱ Pause';document.querySelector('#season-play').setAttribute('aria-pressed','true');}
  function timeline(){return `<section class="season-monitor" aria-label="Historical season playback"><div class="snapshot-header"><div><span class="eyebrow">RETROSPECTIVE · END OF PLAYED DATE</span><h2><span id="snapshot-label">Before first match</span><small id="snapshot-qualifier"></small></h2></div><span id="snapshot-count"></span></div><div class="season-playback"><button id="season-prev" aria-label="Previous played date">←</button><button id="season-play" aria-pressed="false">▶ Play season</button><button id="season-next" aria-label="Next played date">→</button><label class="speed-label" for="season-speed">Speed <select id="season-speed"><option value="1" ${playSpeed===1?'selected':''}>1×</option><option value="2" ${playSpeed===2?'selected':''}>2×</option><option value="4" ${playSpeed===4?'selected':''}>4×</option></select></label><span>One step = one played date</span></div><label class="sr-only" for="season-range">Season end-of-day snapshot</label><input id="season-range" type="range" min="0" max="${currentSeason().dates.length}" step="1" value="${snapshotIndex}"><p>All results played on a date enter together. Modern corrected results, not a live or known-then replay.</p></section>`;}
  function tablePanel(clubs){return `<section class="season-table-panel" aria-label="Computed league table"><div class="standings-scroll" tabindex="0" aria-label="League standings, scrollable"><table class="standings"><caption class="sr-only">Computed results-only league standings at the selected end-of-day snapshot</caption><thead><tr><th scope="col">Pos</th><th scope="col">Club</th><th scope="col"><abbr title="Played">P</abbr></th><th scope="col"><abbr title="Won">W</abbr></th><th scope="col"><abbr title="Drawn">D</abbr></th><th scope="col"><abbr title="Lost">L</abbr></th><th scope="col"><abbr title="Goal difference">GD</abbr></th><th scope="col"><abbr title="Points">Pts</abbr></th></tr></thead><tbody id="standings-body">${clubs.map(c=>`<tr data-table-row="${c.id}"><td data-stat="rank">—</td><th scope="row"><button data-league-club="${c.id}" aria-pressed="${c.id===state.selected}">${badge(c,'table-crest')}<span>${c.name}</span></button></th>${['p','w','d','l','gd','pts'].map(k=>`<td data-stat="${k}">0</td>`).join('')}</tr>`).join('')}</tbody></table></div><p class="table-rule">Points → goal difference → goals scored. Equal records share a rank (=); alphabetical order only arranges tied names. No point adjustments are supplied.</p></section>`;}
  function updateSnapshot(animate=false){
    const season=currentSeason(),snap=SeasonTableModel.snapshot(season,snapshotIndex),body=document.querySelector('#standings-body');
    const motion=animate&&typeof matchMedia==='function'&&!matchMedia('(prefers-reduced-motion: reduce)').matches;
    const before=new Map();
    if(motion)for(const row of body.children){row.getAnimations?.().forEach(a=>a.cancel());before.set(row.dataset.tableRow,row.getBoundingClientRect().top);}
    for(const row of snap.rows){
      const element=document.querySelector(`[data-table-row="${row.id}"]`);body.append(element);
      element.classList.toggle('selected',row.id===state.selected);
      element.querySelector('[data-stat="rank"]').textContent=snapshotIndex?(row.shared?'=':'')+row.rank:'—';
      element.querySelector('[data-stat="rank"]').title=snapshotIndex?(row.shared?'Shared position':'Position'):'Not ranked yet';
      for(const k of ['p','w','d','l','gd','pts'])element.querySelector(`[data-stat="${k}"]`).textContent=k==='gd'&&row[k]>0?'+'+row[k]:row[k];
      const mapRank=document.querySelector(`#map-rank-${row.id}`);if(mapRank){mapRank.textContent=snapshotIndex?(row.shared?'=':'')+row.rank:'';}
    }
    if(motion)for(const row of body.children){const old=before.get(row.dataset.tableRow);if(old!==undefined){const offset=old-row.getBoundingClientRect().top;if(offset)row.animate([{transform:`translateY(${offset}px)`},{transform:'translateY(0)'}],{duration:320,easing:'ease-out'});}}
    document.querySelector('#snapshot-label').textContent=displayDate(snap.date);
    document.querySelector('#snapshot-count').textContent=`${snap.completed} / 380 matches · ${snapshotIndex} / ${season.dates.length} played dates`;
    document.querySelector('#snapshot-qualifier').textContent=snap.date?' · End of day':'';
    document.querySelector('#season-range').value=snapshotIndex;
    document.querySelector('#season-range').setAttribute('aria-valuetext',snap.date?`End of ${displayDate(snap.date)}, ${snap.completed} matches completed`:'Before first match; all clubs unranked');
    document.querySelector('#season-prev').disabled=snapshotIndex===0;document.querySelector('#season-next').disabled=snapshotIndex===season.dates.length;
    const card=document.querySelector('#club-snapshot');
    if(card&&state.selected){
      const f=SeasonTableModel.clubFacts(season,snapshotIndex,state.selected);
      document.querySelector('#snapshot-position').textContent=snapshotIndex?(f.row.shared?'=':'')+f.row.rank:'—';
      document.querySelector('#snapshot-position-label').textContent=snapshotIndex?'Position':'Not ranked';
      document.querySelector('#snapshot-points').textContent=f.row.pts;document.querySelector('#snapshot-played').textContent=f.row.p;
      document.querySelector('#snapshot-change').textContent=f.date?displayDate(f.date)+' · +'+f.pointsChange+' pts on this date. '+(f.positionChange===null?'No previous played-date rank.':f.positionChange>0?'Up '+f.positionChange+' vs previous played date.':f.positionChange<0?'Down '+(-f.positionChange)+' vs previous played date.':'Rank unchanged vs previous played date.'):'Before first match. No results included.';
      document.querySelector('#snapshot-today').textContent=f.today.length?f.today.map(m=>`${season.clubs.find(c=>c.id===m.home).name} ${m.hg}–${m.ag} ${season.clubs.find(c=>c.id===m.away).name}`).join('\n'):f.date?'No match for this club on this played date.':'';
      const latest=HistoricalMatchModel.latest(season,snapshotIndex,state.selected);document.querySelector('#snapshot-today').hidden=!!latest;const talk=document.querySelector('#talk-match');talk.hidden=!latest;talk.dataset.fixture=latest?HistoricalMatchModel.key(state.season,latest):'';document.querySelector('#latest-match').hidden=!!latest;document.querySelector('#latest-match').textContent=latest?'':'No completed match at this cutoff.';
      if(latest){const members=LeagueModel.members(state.season),home=members.find(c=>c.id===latest.home),away=members.find(c=>c.id===latest.away),label=latest.date===f.date?'Played on this date':'Latest match before this date',esc=MatchWorkspace.esc;
        const team=c=>`<span class="latest-match-team${c.id===state.selected?' is-selected':''}">${badge(c,'latest-match-crest')}<strong>${esc(c.name)}</strong></span>`;
        talk.setAttribute('aria-label',`${label}: ${home.name} ${latest.hg}–${latest.ag} ${away.name}, ${displayDate(latest.date)}, full time. Explore this match.`);
        talk.innerHTML=`<span class="latest-match-kicker">${label}</span><span class="latest-match-date">${displayDate(latest.date)} · Full time</span><span class="latest-match-teams">${team(home)}<span class="latest-match-score">${latest.hg}<span aria-hidden="true">–</span>${latest.ag}</span>${team(away)}</span><span class="latest-match-cta">Explore this match <span aria-hidden="true">↗</span></span>`;
        bindCrestFallbacks(talk);
      }else{talk.innerHTML='';talk.removeAttribute('aria-label');}
      document.querySelector('#snapshot-answer').textContent=activeQuestion?SeasonTableModel.explain(season,snapshotIndex,state.selected,activeQuestion):'';
    }
    if(!playTimer)document.querySelector('#season-play').textContent=snapshotIndex===season.dates.length?'↻ Replay from start':'▶ Play season';
  }
  let seasonPopupOpen = false, seasonQuery = '', activeSeason = null;
  function seasonPicker() {
    return `<div class="season-picker"><span class="season-label" id="season-label">Season</span><button id="league-season-trigger" aria-haspopup="dialog" aria-expanded="false" aria-controls="season-popup" aria-labelledby="season-label season-value"><span id="season-value">${state.season}</span><svg width="14" height="14" viewBox="0 0 16 16" aria-hidden="true"><path d="m4 6 4 4 4-4" fill="none" stroke="currentColor" stroke-width="1.5"/></svg></button><div id="season-popup" class="season-popup" role="dialog" aria-label="Choose a season" hidden><label class="sr-only" for="season-search">Search available seasons</label><div class="season-search-row"><span aria-hidden="true">⌕</span><input id="season-search" type="search" autocomplete="off" placeholder="Search seasons…" role="combobox" aria-autocomplete="list" aria-expanded="true" aria-controls="season-options"><button id="season-clear" aria-label="Clear season search" hidden>×</button></div><div class="season-popup-caption">AVAILABLE SEASONS</div><div id="season-options" role="listbox" aria-label="Available seasons"></div><p id="season-results" role="status" aria-live="polite"></p></div></div>`;
  }
  function renderSeasonOptions() {
    const options = LeagueModel.filterSeasons(seasonQuery);
    if (!options.includes(activeSeason)) activeSeason = options[0] || null;
    document.querySelector('#season-options').innerHTML = options.map(season => `<button role="option" tabindex="-1" id="season-option-${season.slice(0,4)}" data-season-option="${season}" aria-selected="${season===state.season}" class="${season===activeSeason?'active':''}"><span>${season}</span><span class="season-option-note">${season===state.season?'Selected <b aria-hidden="true">✓</b>':'Premier League'}</span></button>`).join('');
    const search = document.querySelector('#season-search');
    if (activeSeason) search.setAttribute('aria-activedescendant',`season-option-${activeSeason.slice(0,4)}`);
    else search.removeAttribute('aria-activedescendant');
    document.querySelector('#season-clear').hidden = !seasonQuery;
    document.querySelector('#season-results').textContent = options.length ? `${options.length} available season${options.length===1?'':'s'}.` : 'No matching seasons. Clear your search to see both available seasons.';
  }
  function openSeasonPicker() {
    seasonPopupOpen = true; seasonQuery = ''; activeSeason = state.season;
    document.querySelector('#season-popup').hidden = false;
    document.querySelector('#league-season-trigger').setAttribute('aria-expanded','true');
    document.querySelector('#season-search').value = '';
    renderSeasonOptions(); document.querySelector('#season-search').focus();
  }
  function closeSeasonPicker(restoreFocus = false) {
    if (!seasonPopupOpen) return;
    seasonPopupOpen = false;
    document.querySelector('#season-popup').hidden = true;
    document.querySelector('#league-season-trigger').setAttribute('aria-expanded','false');
    if (restoreFocus) document.querySelector('#league-season-trigger').focus();
  }
  function chooseSeason(season) {
    stopSeason();activeQuestion=null;
    const prior = state.selected;
    state = LeagueModel.transition(state,{type:'season',value:season});
    snapshotIndex=currentSeason().dates.length;
    seasonPopupOpen = false;
    paint('#league-season-trigger');
    document.querySelector('#league-announcement').textContent = `${state.season}, 20 clubs. ${prior&&!state.selected?'The selected club did not participate; selection cleared.':state.selected?'Selection retained; ground reference updated.':'Choose a club.'}`;
  }
  function badge(club, extraClass = '') {
    const crest = LeagueModel.crestFor(club.id,state.season);
    return `<span class="crest-badge ${extraClass}" aria-hidden="true"><span class="crest-fallback">${club.initials}</span>${crest?`<img src="${crest}" alt="" data-club-crest>`:''}</span>`;
  }
  function bindCrestFallbacks(root=workspace) {
    root.querySelectorAll('[data-club-crest]').forEach(image => {
      if(image.dataset.fallbackBound)return;image.dataset.fallbackBound='true';
      const fallback = image.parentElement.querySelector('.crest-fallback');
      const success = () => { fallback.style.visibility = 'hidden'; };
      const failure = () => { image.remove(); fallback.style.visibility = 'visible'; };
      image.addEventListener('load',success,{once:true});
      image.addEventListener('error',failure,{once:true});
      if (image.tagName.toLowerCase() === 'img' && image.complete) {
        if (image.naturalWidth) success(); else failure();
      }
    });
  }
  const outline = `<svg class="league-map" viewBox="120 30 640 710" aria-labelledby="map-title map-desc" role="group"><title id="map-title">An illustrated football journey around the UK</title><desc id="map-desc">Town-level club locations, not to scale. London and Watford are expanded in a separate inset. Select a labelled club marker or use the list.</desc><defs><pattern id="sea-lines" width="18" height="18" patternUnits="userSpaceOnUse"><path d="M0 17H18" stroke="#dbded3" stroke-width=".4"/></pattern><filter id="land-shadow"><feDropShadow dx="0" dy="5" stdDeviation="6" flood-color="#253f31" flood-opacity=".08"/></filter></defs><rect x="120" y="30" width="640" height="710" fill="url(#sea-lines)"/><g class="land" filter="url(#land-shadow)"><path d="M371 59 C359 66 346 62 337 77 L326 77 330 92 313 102 320 115 305 127 310 143 295 157 302 170 290 184 308 191 300 207 316 217 311 235 326 242 320 258 337 269 334 283 352 293 359 311 376 320 379 340 367 359 375 378 367 398 371 418 355 433 352 450 338 455 345 472 332 482 343 497 331 507 316 501 309 515 294 513 297 530 283 542 300 548 302 566 288 576 274 578 279 591 299 599 315 596 326 610 346 607 353 620 369 619 365 638 350 645 345 658 329 660 320 673 307 672 302 689 286 692 281 704 258 709 250 721 272 719 290 710 311 713 328 702 349 706 367 694 389 698 407 688 426 693 444 681 466 686 487 675 505 678 518 664 539 661 554 650 546 640 529 640 525 625 536 614 530 601 546 590 553 572 545 548 525 538 504 541 491 531 488 512 476 497 482 478 477 458 485 443 477 427 477 410 464 397 466 376 455 361 456 340 442 323 437 303 420 291 409 273 397 268 390 253 402 246 410 231 420 220 421 200 438 190 447 171 438 159 416 165 402 155 411 145 431 138 440 122 427 117 410 127 397 116 405 105 422 99 430 83 409 85 391 76 380 79 Z"/>
  <path d="M231 327l-24 3-16 14-20 2-11 21 7 21 21 6 11 18 24-4 11-16 22-2 6-20-13-14 1-19z"/>
  <path d="M292 91l-8 19 7 14-11 16 8 16 9-13-2-20 9-15zM278 165l-7 13 5 17 9-8zM273 205l-6 15 8 10 8-14zM449 51l-13 8 4 11 17-3 5-13zM478 35l-10 9 8 8 12-7z"/></g>
  <g class="map-region" aria-hidden="true"><text x="350" y="184" transform="rotate(-12 350 184)">SCOTLAND</text><text x="193" y="362" text-anchor="middle">NORTHERN</text><text x="193" y="376" text-anchor="middle">IRELAND</text><text x="315" y="566" transform="rotate(-22 315 566)">WALES</text><text x="432" y="600" transform="rotate(12 432 600)">ENGLAND</text><text x="176" y="513" class="sea-name" transform="rotate(-14 176 513)">IRISH SEA</text><text x="587" y="181" class="sea-name">NORTH SEA</text></g>
  <path class="london-leader" d="M495 635Q568 638 576 565"/><circle cx="495" cy="635" r="7" class="london-dot"/><text class="map-caption" x="494" y="659">London ↗</text><rect class="london-inset" x="550" y="320" width="192" height="253" rx="22"/><text class="inset-label" x="570" y="345">LONDON & WATFORD</text><path class="thames" d="M562 486q35-28 58-8t44-1 63 9"/><g id="club-map-markers"></g><text x="150" y="719" class="map-caption">Illustrated locations · not to scale</text></svg>`;
  function showLeague(visible) {
    if(typeof PLAnnotations!=='undefined'&&PLAnnotations.isOpen()){PLAnnotations.close();if(visible)return;}
    if(typeof RecordedReplay!=='undefined'&&RecordedReplay.isOpen()){RecordedReplay.close();if(visible)return;}
    if(typeof HistoricalMatch!=='undefined'&&HistoricalMatch.isOpen()){HistoricalMatch.close();if(visible)return;}
    if(!visible)stopSeason();
    closeSeasonPicker();
    workspace.hidden = !visible; demo.hidden = visible;
    document.querySelector('#open-league').setAttribute('aria-pressed',String(visible));
    document.querySelector('.preview span').textContent = visible ? 'Historical results · End-of-day replay · Not live' : 'Fictional clubs & fixtures · Scripted conversations · No live feed';
    document.querySelector('#preferences').hidden = visible;
    (visible ? document.querySelector('#league-heading') : document.querySelector('#speaker-name')).focus();
  }
  function paint(focusKey) {
    seasonPopupOpen = false;
    const clubs = LeagueModel.members(state.season);
    workspace.innerHTML = `<div class="league-heading"><div><div class="eyebrow">THE LEAGUE, IN PLACE</div><h1 id="league-heading" tabindex="-1">The season,<em> day by day.</em></h1><p>Replay the results. Watch the table move. Choose a club to explore.</p></div><button class="league-demo" id="try-pl-annotations">Brentford–Chelsea · On the pitch ↗<small>Whole-match reconstruction · Players & ball</small></button><button class="league-demo" id="try-recorded">Recorded sample ↗<small>Real source tracking · A-League · Not live</small></button><button class="league-demo" id="try-demo">Try conversation demo ↗<small>Fictional clubs · scripted replay</small></button></div><div class="league-toolbar">${seasonPicker()}<span class="league-key"><i></i> Club & home ground</span></div>${timeline()}<div class="league-layout">${tablePanel(clubs)}<div class="map-panel"><button id="map-toggle" aria-expanded="${!mapCollapsed}" aria-controls="league-map-canvas">${mapCollapsed?'Show map':'Hide map'}</button><div id="league-map-canvas" class="league-canvas${mapCollapsed?' map-collapsed':''}">${outline}</div></div><aside class="league-detail" aria-label="Selected club"><div id="league-card"></div></aside></div><p class="league-footnote">Local historical prototype: pinned community results and reviewed season/ground references. Results are retrospective corrected, not known-then. Positions are town-level illustration, not stadium coordinates. Source-checked period crests where available; letters mark unverified variants. No endorsement or reuse clearance is implied. <a href="${source}" target="_blank" rel="noopener">Research & limitations ↗</a> · <a href="assets/seasons/manifest.json" target="_blank" rel="noopener">Result provenance ↗</a></p>`;
    document.querySelector('#club-map-markers').innerHTML = clubs.map(c=>`<g class="club-pin" role="button" tabindex="0" data-league-club="${c.id}" aria-label="${c.name}, ${c.ground}, ${state.season}" aria-pressed="${state.selected===c.id}" transform="translate(${c.x} ${c.y})"><title>${c.name}</title><circle r="20"/><text class="crest-fallback" text-anchor="middle" dy="4">${c.initials}</text>${LeagueModel.crestFor(c.id,state.season)?`<image data-club-crest aria-hidden="true" href="${LeagueModel.crestFor(c.id,state.season)}" x="-15" y="-16" width="30" height="32" preserveAspectRatio="xMidYMid meet"/>`:''}<text class="map-rank" id="map-rank-${c.id}" x="18" y="-15" text-anchor="middle"></text></g>`).join('');
    const club = clubs.find(c=>c.id===state.selected);
    document.querySelector('#league-card').innerHTML = club ? `<div class="eyebrow">${state.season} · PREMIER LEAGUE</div>${badge(club,"detail-monogram")}<h2>${club.name}</h2><p class="detail-town">${club.town}</p><div id="club-snapshot"><div class="snapshot-club-stats"><span><b id="snapshot-position">—</b><span id="snapshot-position-label">Not ranked</span></span><span><b id="snapshot-points">0</b>Points</span><span><b id="snapshot-played">0</b>Played</span></div><p class="snapshot-change" id="snapshot-change"></p><p class="club-day-result" id="snapshot-today"></p><p id="latest-match" class="club-day-result"></p><button id="talk-match" class="latest-fixture" type="button" hidden></button><div class="snapshot-questions"><button data-snapshot-question="change" aria-pressed="${activeQuestion==='change'}">What changed?</button><button data-snapshot-question="recent" aria-pressed="${activeQuestion==='recent'}">Recent results</button></div><p class="snapshot-answer" id="snapshot-answer"></p><small>Local computed facts at this cutoff · no AI or live feed</small></div><div class="ground-card"><span>HOME GROUND · SEASON REFERENCE</span><h3>${club.ground}</h3><p>${club.id==='west-ham'?'2015/16: Boleyn Ground.<br>2016/17: London Stadium. A change of home.':club.id==='stoke'?'Britannia in 2015/16; bet365 in 2016/17. A name change, the same ground.':club.id==='hull'?'The reviewed directory uses KC Stadium; the KCOM naming date remains unverified.':'A historical reference ground, not a claim about every fixture venue.'}</p></div><p class="detail-evidence">Membership and ground references come from the reviewed PL, UEFA and club-source inventory. Full fixture venues and precise coordinates remain unverified.</p><p class="crest-coverage">${LeagueModel.crestFor(club.id,state.season)?'Period crest source checked; not a match-shirt rendering.':'Period crest not verified here; initials shown.'}</p><p class="detail-limit">Historical match questions use local computed results. The fictional conversation demo is separate.</p><button class="league-demo" id="card-demo">Try conversation demo ↗</button>` : `<div class="eyebrow">YOUR NEXT STOP</div><div class="empty-map-art" aria-hidden="true">↗</div><h2>Where will you<br>start?</h2><p>Select a club on the map or in the standings to meet its home for ${state.season}.</p>`;
    updateSnapshot();
    bindCrestFallbacks();
    if(focusKey) { const target=workspace.querySelector(focusKey); if(target) target.focus(); }
  }
  workspace.addEventListener('click',e=>{
    if(e.target.closest('#try-pl-annotations')){stopSeason();closeSeasonPicker();document.dispatchEvent(new Event('matchmate:workspace-exit'));const scroll=window.scrollY;workspace.hidden=true;demo.hidden=true;document.querySelector('#open-league').setAttribute('aria-pressed','false');document.querySelector('.preview span').textContent='Recorded PL sample · Pitch passage, timeline and observations · No live feed';PLAnnotations.open(()=>{workspace.hidden=false;demo.hidden=true;document.querySelector('#open-league').setAttribute('aria-pressed','true');document.querySelector('.preview span').textContent='Historical results · End-of-day replay · Not live';document.querySelector('#try-pl-annotations').focus({preventScroll:true});window.scrollTo(0,scroll);});return;}
    if(e.target.closest('#try-recorded')){stopSeason();closeSeasonPicker();document.dispatchEvent(new Event('matchmate:workspace-exit'));const scroll=window.scrollY;workspace.hidden=true;demo.hidden=true;document.querySelector('#open-league').setAttribute('aria-pressed','false');document.querySelector('.preview span').textContent='Recorded A-League sample · Retrospective, source-derived · No live feed';RecordedReplay.open(()=>{workspace.hidden=false;demo.hidden=true;document.querySelector('#open-league').setAttribute('aria-pressed','true');document.querySelector('.preview span').textContent='Historical results · End-of-day replay · Not live';document.querySelector('#try-recorded').focus({preventScroll:true});window.scrollTo(0,scroll);});return;}
    if(e.target.closest('#talk-match')){const match=HistoricalMatchModel.latest(currentSeason(),snapshotIndex,state.selected);if(!match||e.target.closest('#talk-match').dataset.fixture!==HistoricalMatchModel.key(state.season,match))return;stopSeason();closeSeasonPicker();const scroll=typeof window!=='undefined'?window.scrollY:0;workspace.hidden=true;demo.hidden=true;HistoricalMatch.open({seasonKey:state.season,season:currentSeason(),match,allSeasons:[...seasons.values()]},()=>{workspace.hidden=false;demo.hidden=true;document.querySelector('#talk-match').focus({preventScroll:true});if(typeof window!=='undefined')window.scrollTo(0,scroll);});return;}
    if(e.target.closest('#season-play')){if(playTimer)stopSeason();else startSeason();return;}
    if(e.target.closest('#season-prev, #season-next')){stopSeason();snapshotIndex+=e.target.closest('#season-prev')?-1:1;updateSnapshot(true);return;}
    if(e.target.closest('#map-toggle')){mapCollapsed=!mapCollapsed;const toggle=document.querySelector('#map-toggle');toggle.textContent=mapCollapsed?'Show map':'Hide map';toggle.setAttribute('aria-expanded',String(!mapCollapsed));document.querySelector('#league-map-canvas').classList.toggle('map-collapsed',mapCollapsed);return;}
    const question=e.target.closest('[data-snapshot-question]');if(question){stopSeason();activeQuestion=question.dataset.snapshotQuestion;document.querySelector('#snapshot-answer').textContent=SeasonTableModel.explain(currentSeason(),snapshotIndex,state.selected,activeQuestion);workspace.querySelectorAll('[data-snapshot-question]').forEach(b=>b.setAttribute('aria-pressed',String(b===question)));return;}
    if (e.target.closest('#league-season-trigger')) { if(seasonPopupOpen) closeSeasonPicker(true); else openSeasonPicker(); return; }
    const option = e.target.closest('[data-season-option]');
    if (option) { chooseSeason(option.dataset.seasonOption); return; }
    if (e.target.closest('#season-clear')) { seasonQuery='';activeSeason=state.season;document.querySelector('#season-search').value='';renderSeasonOptions();document.querySelector('#season-search').focus();return; }
    const club=e.target.closest('[data-league-club]');
    if(club){stopSeason();activeQuestion=null;state=LeagueModel.transition(state,{type:'select',value:club.dataset.leagueClub});paint(`${club.matches('.club-pin')?'.club-pin':'.standings '}[data-league-club="${state.selected}"]`);const c=LeagueModel.members(state.season).find(c=>c.id===state.selected);document.querySelector('#league-announcement').textContent=`${c.name}. ${c.ground}. ${state.season}.`;}
    else if(e.target.closest('#try-demo, #card-demo')) showLeague(false);
  });
  workspace.addEventListener('input',e=>{if(e.target.id==='season-search'){seasonQuery=e.target.value;activeSeason=null;renderSeasonOptions();}if(e.target.id==='season-range'){stopSeason();snapshotIndex=Number(e.target.value);updateSnapshot(true);}});
  workspace.addEventListener('change',e=>{if(e.target.id==='season-speed'){const playing=playTimer!==null;stopSeason();playSpeed=Number(e.target.value);if(playing)startSeason();}});
  workspace.addEventListener('pointerdown',e=>{if(e.target.id==='season-range')stopSeason();});
  document.addEventListener('visibilitychange',()=>{if(document.hidden)stopSeason();});
  if(typeof window!=='undefined')window.addEventListener('pagehide',stopSeason);
  workspace.addEventListener('keydown',e=>{
    if (seasonPopupOpen && e.key==='Escape') { e.preventDefault();closeSeasonPicker(true);return; }
    if(e.target.id==='league-season-trigger' && ['ArrowDown','ArrowUp'].includes(e.key)){e.preventDefault();openSeasonPicker();return;}
    if(e.target.id==='season-search') {
      const options=LeagueModel.filterSeasons(seasonQuery);
      if(['ArrowDown','ArrowUp'].includes(e.key)) {
        e.preventDefault();
        if(options.length){const i=options.indexOf(activeSeason);activeSeason=options[(i+(e.key==='ArrowDown'?1:-1)+options.length)%options.length];renderSeasonOptions();}
      } else if(e.key==='Enter') {e.preventDefault();if(activeSeason) chooseSeason(activeSeason);}
      return;
    }
    if(e.target.matches('.club-pin')&&['Enter',' '].includes(e.key)){e.preventDefault();e.target.dispatchEvent(new MouseEvent('click',{bubbles:true}));}
  });
  document.addEventListener('pointerdown',e=>{if(seasonPopupOpen&&!e.target.closest('.season-picker')) closeSeasonPicker();});
  // Keep the combobox focused until the option's click commits the choice.
  // A blur can otherwise expose body as activeElement before that click fires.
  workspace.addEventListener('pointerdown',e=>{
    if(seasonPopupOpen && e.target.closest('[data-season-option], #season-clear') && e.button===0) e.preventDefault();
  });
  // Focus-in names the destination that actually received focus. Checking
  // activeElement in a blur microtask can dismiss an in-progress pointer click.
  document.addEventListener('focusin',e=>{if(seasonPopupOpen&&!e.target.closest('.season-picker')) closeSeasonPicker();});
  document.querySelector('#open-league').addEventListener('click',()=>showLeague(true));
  document.addEventListener('click',e=>{if(e.target.closest('.wordmark')&&((typeof RecordedReplay!=='undefined'&&RecordedReplay.isOpen())||(typeof PLAnnotations!=='undefined'&&PLAnnotations.isOpen())))showLeague(false);});
  paint();
  demo.hidden = true;
  document.querySelector('#preferences').hidden = true;
})();

(function(root){
  'use strict';
  const aliases={'Arsenal FC':'arsenal','Chelsea FC':'chelsea','Everton FC':'everton','Liverpool FC':'liverpool','Southampton FC':'southampton','Sunderland AFC':'sunderland','Watford FC':'watford','Burnley FC':'burnley','Middlesbrough FC':'middlesbrough'};
  function createSeason(raw,clubs,{complete=true}={}){
    if(!raw || !Array.isArray(raw.matches))throw Error('Missing matches');
    const names=new Map(clubs.map(c=>[c.name,c.id])),ids=new Set(clubs.map(c=>c.id)),seen=new Set(),counts=new Map(clubs.map(c=>[c.id,0]));
    if(ids.size!==clubs.length)throw Error('Duplicate club identity');
    const matches=raw.matches.map(m=>{
      const home=names.get(m.team1)||aliases[m.team1],away=names.get(m.team2)||aliases[m.team2];
      if(!ids.has(home)||!ids.has(away)||home===away)throw Error('Unknown or self-playing club');
      if(!/^\d{4}-\d{2}-\d{2}$/.test(m.date)||!Number.isFinite(Date.parse(m.date+'T00:00:00Z'))||new Date(m.date+'T00:00:00Z').toISOString().slice(0,10)!==m.date)throw Error('Invalid played date');
      if(!Array.isArray(m.score?.ft)||m.score.ft.length!==2||!m.score.ft.every(n=>Number.isInteger(n)&&n>=0))throw Error('Invalid full-time result');
      const key=home+':'+away;if(seen.has(key))throw Error('Duplicate home/away fixture');seen.add(key);
      counts.set(home,counts.get(home)+1);counts.set(away,counts.get(away)+1);
      return {id:key,date:m.date,home,away,hg:m.score.ft[0],ag:m.score.ft[1],round:m.round||null};
    }).sort((a,b)=>a.date.localeCompare(b.date)||a.id.localeCompare(b.id));
    if(complete&&(clubs.length!==20||matches.length!==380||[...counts.values()].some(n=>n!==38)))throw Error('Incomplete Premier League season');
    return {clubs:clubs.map(c=>({id:c.id,name:c.name,initials:c.initials})),matches,dates:[...new Set(matches.map(m=>m.date))]};
  }
  function snapshot(season,index){
    if(!Number.isInteger(index)||index<0||index>season.dates.length)throw Error('Invalid snapshot index');
    const date=index?season.dates[index-1]:null;
    const rows=new Map(season.clubs.map(c=>[c.id,{...c,p:0,w:0,d:0,l:0,gf:0,ga:0,gd:0,pts:0}]));
    const eligible=date?season.matches.filter(m=>m.date<=date):[];
    for(const m of eligible)for(const [id,gf,ga] of [[m.home,m.hg,m.ag],[m.away,m.ag,m.hg]]){
      const r=rows.get(id);r.p++;r.gf+=gf;r.ga+=ga;r.gd=r.gf-r.ga;
      if(gf>ga){r.w++;r.pts+=3;}else if(gf===ga){r.d++;r.pts++;}else r.l++;
    }
    const ordered=[...rows.values()].sort((a,b)=>b.pts-a.pts||b.gd-a.gd||b.gf-a.gf||a.name.localeCompare(b.name,'en'));
    let lastKey=null,rank=0;const groups=new Map();
    ordered.forEach((r,i)=>{const key=`${r.pts}:${r.gd}:${r.gf}`;if(key!==lastKey)rank=i+1;r.rank=rank;r.recordKey=key;groups.set(key,(groups.get(key)||0)+1);lastKey=key;});
    ordered.forEach(r=>{r.shared=groups.get(r.recordKey)>1;});
    return {index,date,rows:ordered,completed:eligible.length,results:date?season.matches.filter(m=>m.date===date):[]};
  }
  function clubFacts(season,index,id){
    const current=snapshot(season,index),row=current.rows.find(r=>r.id===id);
    if(!row)throw Error('Unknown selected club');
    const previous=index?snapshot(season,index-1).rows.find(r=>r.id===id):null;
    return {row,date:current.date,previousDate:index>1?season.dates[index-2]:null,pointsChange:previous?row.pts-previous.pts:0,positionChange:index>1?previous.rank-row.rank:null,
      today:current.results.filter(m=>m.home===id||m.away===id),recent:current.date?season.matches.filter(m=>m.date<=current.date&&(m.home===id||m.away===id)).slice(-3).reverse():[]};
  }
  function resultText(season,m){const name=id=>season.clubs.find(c=>c.id===id).name;return `${m.date}: ${name(m.home)} ${m.hg}–${m.ag} ${name(m.away)}`;}
  function explain(season,index,id,question){
    const f=clubFacts(season,index,id);
    if(!f.date)return 'Before the first match: 0 played and 0 points. No results are included yet.';
    if(question==='recent')return f.recent.length?f.recent.map(m=>resultText(season,m)).join('\n'):'No results for this club by the selected date.';
    const movement=f.positionChange===null?'There is no previous played-date position to compare.':f.positionChange>0?`Up ${f.positionChange} position${f.positionChange===1?'':'s'} versus the previous played date (${f.previousDate}).`:f.positionChange<0?`Down ${-f.positionChange} position${f.positionChange===-1?'':'s'} versus the previous played date (${f.previousDate}).`:`Position unchanged versus the previous played date (${f.previousDate}).`;
    return `${f.row.name}: ${f.row.p} played, ${f.row.pts} points at end of ${f.date}. ${f.pointsChange} points added on this played date. ${movement}${f.row.shared?' The position is shared on points, goal difference and goals scored.':''}\n${f.today.length?f.today.map(m=>resultText(season,m)).join('\n'):'This club did not play on the selected date; other results can still change its position.'}`;
  }
  const api={createSeason,snapshot,clubFacts,explain};
  if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.SeasonTableModel=api;
})(typeof window!=='undefined'?window:globalThis);

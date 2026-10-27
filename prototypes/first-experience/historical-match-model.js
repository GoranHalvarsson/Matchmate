(function(root){
 'use strict';
 const table=typeof module!=='undefined'&&module.exports?require('./season-table-model.js'):root.SeasonTableModel;
 const name=(season,id)=>season.clubs.find(c=>c.id===id).name;
 const result=(season,m)=>`${m.date}: ${name(season,m.home)} ${m.hg}–${m.ag} ${name(season,m.away)}`;
 function latest(season,index,club){const date=table.snapshot(season,index).date;return date?season.matches.filter(m=>m.date<=date&&(m.home===club||m.away===club)).at(-1)||null:null;}
 function key(seasonKey,match){return seasonKey+'|'+match.id;}
 function compute(context,input){
  const {season,match,allSeasons}=context;
  const q=input.trim().toLowerCase();
  if(/^(result|score|what (was|is) the (result|score))\??$/.test(q))return result(season,match)+'. Full-time result from the local historical dataset.';
  if(/^(form|pre.?match form|how (was|were) (their|the teams?) form)\??$/.test(q))return [match.home,match.away].map(id=>{
   const prior=season.matches.filter(m=>m.date<match.date&&(m.home===id||m.away===id)).slice(-5);
   const record=prior.map(m=>{const gf=m.home===id?m.hg:m.ag,ga=m.home===id?m.ag:m.hg;return gf>ga?'W':gf===ga?'D':'L';}).join(' ');
   return `${name(season,id)}: ${prior.length?record+' (oldest → newest)':'No earlier results in this season.'}${prior.length?'\n'+prior.map(m=>result(season,m)).join('\n'):''}`;
  }).join('\n\n')+'\nPre-match form uses up to five results in this season, strictly before this played date.';
  if(/^(table|table impact|what changed|what did this change)\??$/.test(q)){
   const index=season.dates.indexOf(match.date)+1,current=table.snapshot(season,index),prior=table.snapshot(season,index-1);
   return `End of played date ${match.date} (all ${current.results.length} matches that day included):\n`+[match.home,match.away].map(id=>{const r=current.rows.find(r=>r.id===id),p=prior.rows.find(r=>r.id===id);return `${r.name}: ${r.pts} points (+${r.pts-p.pts}), ${r.p} played, position ${r.shared?'=':''}${r.rank}${index>1?` (previous played-date position ${p.shared?'=':''}${p.rank})`:''}.`;}).join('\n')+'\nOther results contribute to the table; this is not a claim that this match alone caused a position change.';
  }
  if(/^(head.?to.?head|prior meetings|previous meetings)\??$/.test(q)){
   const prior=allSeasons.flatMap(s=>s.matches.filter(m=>m.date<match.date&&((m.home===match.home&&m.away===match.away)||(m.home===match.away&&m.away===match.home))).map(m=>result(s,m))).sort();
   return (prior.length?prior.join('\n'):'No earlier meeting is present in the loaded coverage.')+'\nLimited coverage: only the two loaded Premier League seasons, 2015/16 and 2016/17, strictly before this played date. This is not their complete history.';
  }
  return 'I can compute the result, pre-match form, end-of-day table impact and prior meetings from the two loaded seasons. Try one of those questions. Scorers, lineups, venues and match events are not covered. This is local computation, not AI.';
 }
 function answer(context,input){return 'Selected match: '+result(context.season,context.match)+'\n\n'+compute(context,input);}
 const api={latest,key,result,answer};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.HistoricalMatchModel=api;
})(typeof window!=='undefined'?window:globalThis);

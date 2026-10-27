(function(root){
'use strict';
const POLICY='retrospective-minimal-pp-interval-completion-v1';
const fields=['sourceId','sourceLine','type','actorId','teamId','period','frameStart','frameEnd','timeStart','timeEnd','start','end','startMatched','endMatched','dependency'];
const finite=n=>typeof n==='number'&&Number.isFinite(n);
const exact=(object,keys)=>!!object&&typeof object==='object'&&!Array.isArray(object)&&Object.keys(object).length===keys.length&&keys.every(k=>Object.hasOwn(object,k));
function seconds(text){if(typeof text!=='string'||!/^\d{2}:\d{2}\.\d{1,3}$/.test(text))return NaN;const [m,s]=text.split(':').map(Number);return s<60?m*60+s:NaN;}
function classify(question){
 const q=String(question).toLowerCase().replace(/[’']/g,'').replace(/[^a-z0-9\s]/g,' ').replace(/\s+/g,' ').trim();
 const aliases={history:['what happened before this moment','what happened before now','what happened so far'],player:['what did this player do','what did the selected player do'],position:['describe the selected players position','describe this players position','where is the selected player'],coverage:['what is available at this cursor','what is available here']};
 return Object.keys(aliases).find(kind=>aliases[kind].includes(q))||'unsupported';
}
function create(sample,bundle){
 const roster=new Map(sample.players.map(p=>[p.id,{...p}])),frames=new Map(sample.frames.map(f=>[f.frameId,f]));
 const sampleValid=roster.size===sample.players.length&&frames.size===sample.frames.length;
 const bundleValid=exact(bundle,['schemaVersion','source','pin','matchId','period','start','end','policy','annotations'])&&bundle.schemaVersion===1&&bundle.source==='SkillCorner'&&bundle.pin===sample.pin&&bundle.matchId===sample.matchId&&bundle.period===sample.period&&bundle.period===1&&bundle.start===sample.start&&bundle.end===sample.end&&bundle.policy===POLICY&&Array.isArray(bundle.annotations)&&bundle.annotations.length<=6&&[sample.home.id,sample.away.id].every(id=>bundle.annotations.filter(e=>e?.teamId===id).length<=3);
 const ids=new Map();for(const e of bundleValid?bundle.annotations:[])if(e&&typeof e.sourceId==='string')ids.set(e.sourceId,(ids.get(e.sourceId)||0)+1);
 function valid(e){
  if(!exact(e,fields)||ids.get(e.sourceId)!==1||!/^8_\d+$/.test(e.sourceId)||!Number.isInteger(e.sourceLine)||e.sourceLine<2||e.sourceLine>1001||e.type!=='player_possession'||e.period!==sample.period)return false;
  const p=roster.get(e.actorId);
  if(!p||!Number.isInteger(e.actorId)||p.teamId!==e.teamId||![sample.home.id,sample.away.id].includes(e.teamId)||e.startMatched!==true||e.endMatched!==true)return false;
  if(!Number.isInteger(e.frameStart)||!Number.isInteger(e.frameEnd)||e.frameStart>e.frameEnd||!finite(e.start)||!finite(e.end)||e.start>e.end||e.start<sample.start||e.end>sample.end)return false;
  const dependency=e.dependency;
  if(!exact(dependency,['kind','verified','frame','clock'])||dependency.kind!==POLICY||dependency.verified!==true||!finite(dependency.clock)||dependency.clock!==e.end||dependency.frame!==e.frameEnd)return false;
  if(seconds(e.timeStart)!==e.start||seconds(e.timeEnd)!==e.end||frames.get(e.frameStart)?.clock!==e.start||frames.get(e.frameEnd)?.clock!==e.end)return false;
  for(let id=e.frameStart;id<=e.frameEnd;id++){
   const frame=frames.get(id),people=frame?.players;
   if(!frame||frame.period!==e.period||frame.clock!==Number((e.start+(id-e.frameStart)*sample.cadence).toFixed(8))||!Array.isArray(people)||people.length!==roster.size||new Set(people.map(p=>p.playerId)).size!==roster.size||people.some(p=>!roster.has(p.playerId)||!finite(p.x)||!finite(p.y)))return false;
  }
  return true;
 }
 // Validate once, then retain an allowlisted projection. Never return CSV objects.
 const accepted=sampleValid&&bundleValid?bundle.annotations.filter(valid).map(e=>{
  const p=roster.get(e.actorId),team=e.teamId===sample.home.id?sample.home:sample.away;
  return Object.freeze({sourceId:e.sourceId,sourceLine:e.sourceLine,actorId:p.id,actorName:p.name,teamId:p.teamId,teamName:team.name,type:'player_possession',start:e.start,end:e.end,frameStart:e.frameStart,frameEnd:e.frameEnd,timeStart:e.timeStart,timeEnd:e.timeEnd,scope:'Recorded analysis'});
 }).sort((a,b)=>a.end-b.end||a.frameStart-b.frameStart||a.sourceId.localeCompare(b.sourceId)):[];
 function eligible(time,playerId){
  if(!finite(time)||time<sample.start||time>sample.end)return [];
  // No rounding or epsilon: the exact mapped interval end is the spoiler cutoff.
  return accepted.filter(e=>e.end<=time&&(playerId===undefined||e.actorId===playerId));
 }
 function describe(event){return `${event.actorName} (${event.teamName}): source-classified possession interval ${event.timeStart}–${event.timeEnd}. Source ${sample.matchId}/${event.sourceId}, CSV line ${event.sourceLine}.`;}
 function answer(time,playerId,question){
  const kind=classify(question),player=roster.get(playerId);
  const facts=kind==='history'?eligible(time).slice(-3).reverse():kind==='player'?eligible(time,playerId).slice(-3).reverse():[];
  const text=facts.length?facts.map(describe).join('\n\n'):kind==='player'&&player?`No completed annotations for ${player.name} are available in this curated selection at this cursor. This limited coverage does not establish that the player did nothing.`:'No completed annotations are available in this curated selection at this cursor. This does not mean no football happened.';
  return {kind,time,playerId,sourceIds:facts.map(e=>e.sourceId),facts,text};
 }
 return {eligible,answer,describe};
}
const api={create,classify,POLICY};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.RecordedEventsModel=api;
})(typeof window!=='undefined'?window:globalThis);

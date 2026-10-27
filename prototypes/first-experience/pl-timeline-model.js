(function(root){
'use strict';
const fail=message=>{throw Error('PL timeline: '+message);},finite=n=>typeof n==='number'&&Number.isFinite(n),plain=o=>o&&typeof o==='object'&&!Array.isArray(o),keys=(o,allowed)=>{if(!plain(o)||Object.keys(o).some(k=>!allowed.includes(k)))fail('unexpected fields');},str=s=>typeof s==='string'&&s.trim().length>0;
const badgeLabels={sampled_visual:'Sampled video',asr_candidate:'Commentary candidate · unverified',replay:'Replay / earlier action',historical_discussion:'Historical discussion',broadcast_attribution:'Broadcast attribution'};
const questions={'what happened here':'here','what happened':'here','here':'here','what supports this':'support','what is the evidence':'support','evidence':'support','what is uncertain':'uncertain','what is unknown':'uncertain','uncertainty':'uncertain','what happened earlier':'earlier','earlier':'earlier','where are the players':'position','where is this player':'position','show player positions':'position'};
const normalize=q=>String(q??'').toLowerCase().trim().replace(/[?!.]+$/,'').replace(/\s+/g,' ');
function create(input){
 const s=JSON.parse(JSON.stringify(input));keys(s,['version','kind','fixture','source','bounds','policy','passages']);
 if(s.version!==1||s.kind!=='retrospective_candidate_passage_timeline')fail('unsupported artifact');
 keys(s.fixture,['id','officialMatchId','date','home','away']);if(!['id','date','home','away'].every(k=>str(s.fixture[k]))||s.fixture.id!=='premier-league:2026-27:2026-09-18:brentford:chelsea'||s.fixture.officialMatchId!==2645236)fail('fixture');
 keys(s.bounds,['start','end']);if(s.bounds.start!==0||!finite(s.bounds.end)||s.bounds.end<=0)fail('bounds');
 keys(s.policy,['availability','reportsIncluded','liveKnownThen','completeIncidentFeed','movement']);if(s.policy.availability!=='latest_support_end'||['reportsIncluded','liveKnownThen','completeIncidentFeed','movement'].some(k=>s.policy[k]!==false))fail('policy');
 keys(s.source,['url','ledger','asr','visual','reviewedBundle','audioSha256','videoSha256']);
 if(!/^https:\/\/www\.brentfordfc\.com\/en\/video\/[a-zA-Z0-9_]+\?provider=stream$/.test(s.source.url))fail('source URL');
 for(const k of ['ledger','asr','visual','reviewedBundle']){keys(s.source[k],['sha256','bytes']);if(!/^[a-f0-9]{64}$/.test(s.source[k].sha256)||!Number.isSafeInteger(s.source[k].bytes)||s.source[k].bytes<=0)fail('source receipt');}
 for(const k of ['audioSha256','videoSha256'])if(!/^[a-f0-9]{64}$/.test(s.source[k]))fail('media receipt');
 if(!Array.isArray(s.passages)||!s.passages.length)fail('passages');
 const ids=new Set();
 for(const p of s.passages){
  keys(p,['id','title','text','uncertainty','reviewStatus','badges','mediaInterval','support','availableAt','withheldReason']);
  if(!str(p.id)||ids.has(p.id)||!['title','text','uncertainty','reviewStatus'].every(k=>str(p[k])))fail('passage identity/text');ids.add(p.id);
  const interval=p.mediaInterval;if(!Array.isArray(interval)||interval.length!==2||!interval.every(finite)||interval[0]<0||interval[1]<interval[0]||interval[1]>s.bounds.end)fail('media interval');
  if(!Array.isArray(p.badges)||new Set(p.badges).size!==p.badges.length||p.badges.some(b=>!Object.hasOwn(badgeLabels,b)))fail('badges');
  keys(p.support,['asr','visual']);const ends=[interval[1]];let resolved=0;
  for(const [kind,list]of Object.entries(p.support)){if(!Array.isArray(list))fail('support collection');const refs=new Set();for(const r of list){keys(r,kind==='asr'?['id','start','end','role']:['id','start','end','scope']);if(!str(r.id)||!finite(r.start)||!finite(r.end)||r.start<0||r.end<r.start||r.end>s.bounds.end)fail('support interval');if(kind==='asr'?!['asr','context'].includes(r.role):!str(r.scope))fail('support scope');const key=r.id+':'+(r.role||'visual');if(refs.has(key))fail('duplicate support');refs.add(key);ends.push(r.end);resolved++;}}
  if(p.availableAt===null){if(!str(p.withheldReason))fail('withheld reason');}
  else if(!resolved||p.withheldReason!==null||!finite(p.availableAt)||p.availableAt!==Math.max(...ends))fail('latest supporting end');
  if(p.support.asr.some(r=>r.role==='asr')!==p.badges.includes('asr_candidate')||!!p.support.visual.length!==p.badges.includes('sampled_visual'))fail('evidence badge');
 }
 const freeze=o=>{if(o&&typeof o==='object'){Object.values(o).forEach(freeze);Object.freeze(o);}return o;};freeze(s);
 const valid=t=>finite(t)&&t>=s.bounds.start&&t<=s.bounds.end;
 const eligible=t=>valid(t)?s.passages.filter(p=>p.availableAt!==null&&p.availableAt<=t).sort((a,b)=>a.availableAt-b.availableAt||a.id.localeCompare(b.id)):[];
 const endpoints=[...new Set(s.passages.filter(p=>p.availableAt!==null).map(p=>p.availableAt))].sort((a,b)=>a-b);
 const format=t=>{if(!finite(t))return 'Unknown';const n=Math.floor(t),h=Math.floor(n/3600),m=Math.floor(n%3600/60),sec=n%60;return [h,m,sec].map(n=>String(n).padStart(2,'0')).join(':');};
 function view(t,selectedId=null){const passages=eligible(t),selected=selectedId===null?passages.at(-1)||null:passages.find(p=>p.id===selectedId)||null;return {cursor:valid(t)?t:null,passages,selected,gap:valid(t)&&!s.passages.some(p=>p.mediaInterval[0]<=t&&t<=p.mediaInterval[1]),withheld:s.passages.filter(p=>p.availableAt===null).map(p=>({id:p.id,reason:p.withheldReason}))};}
 function answer(t,id,question){const v=view(t,id),p=v.selected,kind=questions[normalize(question)]||'unsupported',stamp={cursor:v.cursor,passageId:p?.id||null};let text,evidenceIds=[];
  if(v.cursor===null)text='Invalid media cursor. Choose a point within this source.';
  else if(kind==='position')text='Full-match player movement is unavailable. The separate Estimated replay mode contains only two anonymous schematic estimates over three seconds; this timeline supplies no coordinates or inferred identities.';
  else if(kind==='unsupported')text='This local prototype supports only: What happened here? What supports this? What is uncertain? What happened earlier? Player positions are unavailable. It does not generate AI answers.';
  else if(kind==='earlier'){const earlier=v.passages.filter(x=>!p||x.availableAt<p.availableAt);text=earlier.length?earlier.slice(-3).map(x=>x.id+' · '+x.title+': '+x.text+' Uncertainty: '+x.uncertainty).join('\n\n'):'No earlier completed passage is available in this curated timeline. This does not mean no football happened.';evidenceIds=earlier.slice(-3).flatMap(x=>[...x.support.asr.map(r=>r.id),...x.support.visual.map(r=>r.id)]);}
  else if(!p)text='No completed passage is selected at this cursor. Use Next passage or choose an available passage. Empty coverage does not mean no football happened.';
  else if(kind==='here'){text=p.text+' Uncertainty: '+p.uncertainty;evidenceIds=[...p.support.asr.map(r=>r.id),...p.support.visual.map(r=>r.id)];}
  else if(kind==='uncertain'){text=p.uncertainty+' Sampled video and unverified machine transcription do not establish a complete incident feed, measured tracking or exact contacts.';evidenceIds=[...p.support.asr.map(r=>r.id),...p.support.visual.map(r=>r.id)];}
  else{text='Passage '+p.id+' covers source media '+format(p.mediaInterval[0])+'–'+format(p.mediaInterval[1])+'. All cited support finishes by '+format(p.availableAt)+' ('+p.availableAt+' s). '+(p.support.visual.length?'Sampled visual references: '+p.support.visual.map(r=>r.id+' through '+r.end+' s').join(', ')+'. ':'')+(p.support.asr.length?'Unverified ASR/context references: '+[...new Set(p.support.asr.map(r=>r.id))].join(', ')+'. ':'')+'This is a retrospective release rule, not live arrival or known-then evidence.';evidenceIds=[...p.support.asr.map(r=>r.id),...p.support.visual.map(r=>r.id)];}
  return {kind,text,evidenceIds:[...new Set(evidenceIds)],stamp};
 }
 return {start:0,end:s.bounds.end,format,badgeLabels,source:s.source,eligible,view,answer,clamp:t=>valid(t)?t:null,adjacent:(t,direction)=>!valid(t)?null:direction<0?[...endpoints].reverse().find(x=>x<t)??null:endpoints.find(x=>x>t)??null};
}
const api={create,normalize,badgeLabels};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.PLTimelineModel=api;
})(typeof window!=='undefined'?window:globalThis);

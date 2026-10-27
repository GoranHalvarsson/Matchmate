(function(root,factory){if(typeof module==='object'&&module.exports)module.exports=factory();else root.PLOfficialModel=factory();})(typeof window!=='undefined'?window:globalThis,function(){
'use strict';
const fail=m=>{throw Error('Official record: '+m);};
function create(input){
 const r=JSON.parse(JSON.stringify(input));
 if(r.version!==1||r.kind!=='completed_official_match_record'||r.fixture?.officialMatchId!=='2645236'||r.policy?.scope!=='completed_match_reference'||['asOfCursor','knownThen','completeEventFeed','measuredPositions','pitchIdentityJoins'].some(k=>r.policy[k]!==false))fail('scope');
 const sources=new Map((r.sources||[]).map(s=>[s.id,s]));if(sources.size!==r.sources?.length)fail('sources');
 for(const s of sources.values())if(!/^https:\/\/(www\.)?(premierleague\.com|chelseafc\.com|brentfordfc\.com)\//.test(s.url)||!/^\d{4}-\d{2}-\d{2}$/.test(s.observedOn))fail('source URL/date');
 function evidence(v){if(!v||!sources.has(v.source)||!v.locator)fail('evidence');}
 for(const v of [r.fixture.date,r.fixture.season,r.fixture.competition,r.fixture.result,r.summary.venue,r.summary.attendance,r.summary.scheduledKickoff])evidence(v.evidence);
 if(r.fixture.result.status!=='completed'||JSON.stringify(r.fixture.result.value)!=='[3,0]'||r.summary.scheduledKickoff.actual!==null)fail('result/kickoff');
 const players=new Map();if(r.players?.length!==40)fail('lineup coverage');
 for(const p of r.players){if(players.has(p.id)||!['brentford','chelsea'].includes(p.team)||!p.id.startsWith(p.team+':')||!p.name||!Number.isInteger(p.shirt)||p.shirt<1||!['starter','bench'].includes(p.status)||p.role!==null)fail('player');players.set(p.id,p);evidence(p.lineupEvidence);evidence(p.nameEvidence);if(p.captainEvidence)evidence(p.captainEvidence);}
 for(const team of ['brentford','chelsea']){const list=r.players.filter(p=>p.team===team);if(list.filter(p=>p.status==='starter').length!==11||list.filter(p=>p.status==='bench').length!==9||new Set(list.map(p=>p.shirt)).size!==20||list.filter(p=>p.captainEvidence).length!==1)fail('team roster');}
 if(r.formations?.length!==2)fail('formations');for(const f of r.formations){if(f.measured!==false||!f.value||!f.disagreements?.length)fail('formation scope');evidence(f.evidence);f.disagreements.forEach(d=>evidence(d.evidence));}
 const accepted=['possession','xg','total_shots','shots_on_target','big_chances','corners','saves','red_cards','yellow_cards','fouls','offsides'];
 if(r.stats?.length!==11||new Set(r.stats.map(s=>s.id)).size!==11)fail('stat coverage');for(const s of r.stats){if(!accepted.includes(s.id)||s.scope!=='rendered completed-match team summary'||s.period_breakdown!==null||s.operational_definition!==null||!Object.values(s.values).every(v=>Number.isFinite(v)&&v>=0))fail('stat scope');evidence(s.evidence);}
 if(r.events?.length!==14||new Set(r.events.map(e=>e.id)).size!==14)fail('event coverage');for(const e of r.events){if(!['goal','substitution','yellow_card'].includes(e.kind)||e.time?.precision!=='reported_minute'||e.time.seconds!==null||!Number.isInteger(e.time.minute)||e.time.minute<0||!e.time.printed||Object.hasOwn(e,'mediaSeconds'))fail('event time');evidence(e.evidence);const ids=e.kind==='substitution'?[e.off,e.on]:[e.player];if(ids.some(id=>!players.has(id)||players.get(id).team!==e.team))fail('event player');if(e.kind==='goal'){evidence(e.assist?.evidence);if(e.assist.player!==null&&!players.has(e.assist.player))fail('assist');e.disagreements.forEach(d=>evidence(d.evidence));}}
 for(const [kind,count]of [['goal',3],['substitution',9],['yellow_card',2]])if(r.events.filter(e=>e.kind===kind).length!==count)fail('event counts');
 if(r.summary.officials?.length!==6)fail('officials');r.summary.officials.forEach(o=>evidence(o.evidence));
 if(r.videoLinks?.length!==1)fail('video links');const link=r.videoLinks[0];if(link.eventId!=='goal-1'||link.passageId!=='first-goal'||JSON.stringify(link.sourceSeconds)!=='[3708,3719]'||link.initial!==3715.4||link.exactEventSecond!==false||link.identityJoin!==false)fail('video association');
 const freeze=v=>{if(v&&typeof v==='object'){Object.freeze(v);Object.values(v).forEach(freeze);}return v;};freeze(r);
 return Object.freeze({record:r,player:id=>players.get(id)||null,source:id=>sources.get(id)||null,team:id=>r.players.filter(p=>p.team===id),reference:scope=>{if(scope!=='completed_match_reference')fail('explicit completed reference required');return r;}});
}
return {create};
});

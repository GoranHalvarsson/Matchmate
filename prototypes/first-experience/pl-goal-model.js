(function(root){
'use strict';
const finite=n=>typeof n==='number'&&Number.isFinite(n),copy=o=>JSON.parse(JSON.stringify(o));
const need=(ok,s)=>{if(!ok)throw Error('Goal passage: '+s);};
const keys=(o,k)=>o&&typeof o==='object'&&!Array.isArray(o)&&Object.keys(o).sort().join(' ')===k.split(' ').sort().join(' ');
const hash=r=>keys(r,'sha256 bytes')&&/^[a-f0-9]{64}$/.test(r.sha256)&&Number.isInteger(r.bytes)&&r.bytes>0;
const normalize=s=>String(s).toLowerCase().replace(/[’']/g,'').replace(/[^a-z0-9 ]/g,' ').trim().replace(/\s+/g,' ');
const questions={'where is this player':'position','describe this players position':'position','what is visible here':'coverage','what supports this':'evidence','where is the ball':'ball','what happened before this moment':'history','what is missing here':'missing'};
function create(input){
 need(keys(input,'version kind fixtureId source bounds method participants frames shots cuts facts limits'),'bundle allowlist');const data=copy(input);
 need(data.version===1&&data.kind==='manual_first_goal_pitch_passage'&&data.fixtureId==='premier-league:2026-27:2026-09-18:brentford:chelsea','fixture/version');
 need(keys(data.source,'url reviewedOn videoSha256 frameReceipt artifact')&&data.source.url==='https://www.brentfordfc.com/en/video/0_2hoy7vw7?provider=stream'&&data.source.reviewedOn==='2026-10-17'&&/^[a-f0-9]{64}$/.test(data.source.videoSha256)&&hash(data.source.frameReceipt)&&hash(data.source.artifact),'source receipts');
 need(keys(data.bounds,'start end initial actionStart actionEnd')&&JSON.stringify(data.bounds)===JSON.stringify({start:3708,end:3719,initial:3715.4,actionStart:3715,actionEnd:3718.6}),'approved interval');
 const m=data.method;need(keys(m,'layout calibration continuousReview review axes uncertaintyRadius units playerInterpolationMax ballInterpolation identity ball')&&m.layout==='manual_landmark_schematic_estimates'&&m.calibration==='none'&&m.continuousReview===false&&m.units==='schematic_percent_not_metres'&&m.uncertaintyRadius===8&&m.playerInterpolationMax===0.200001&&m.ballInterpolation===false&&m.identity==='anonymous_shot_local_tracklets_not_unique_players'&&m.ball==='apparent_landmark_projection_height_unknown_not_ground_position','estimate policy');
 need(Array.isArray(data.participants)&&data.participants.length===16,'bounded tracklets');const ids=new Set();
 for(const p of data.participants){need(keys(p,'id label team shot tag identity')&&/^(corner-B1|wide-(B[1-6]|C[1-8]|GK))$/.test(p.id)&&!ids.has(p.id)&&['brentford','chelsea'].includes(p.team)&&['corner','wide'].includes(p.shot)&&typeof p.label==='string'&&p.label.length<100&&p.identity===null,'anonymous identity');ids.add(p.id);}
 need(Array.isArray(data.frames)&&data.frames.length===24,'24 reviewed layouts');let previous=-Infinity;
 for(const f of data.frames){
  need(keys(f,'id time shot clock score image points ball')&&/^G\d{2}$/.test(f.id)&&finite(f.time)&&f.time>previous&&f.time>=data.bounds.start&&f.time<=data.bounds.end&&['corner','wide'].includes(f.shot)&&/^60:\d{2}$/.test(f.clock)&&JSON.stringify(f.score)==='[0,0]','frame scope');previous=f.time;
  need(keys(f.image,'sha256 bytes sourcePts')&&hash({sha256:f.image.sha256,bytes:f.image.bytes})&&f.image.sourcePts===f.time,'source PTS');
  const seen=new Set();need(Array.isArray(f.points),'frame points');
  for(const p of f.points){need(keys(p,'id x y')&&ids.has(p.id)&&!seen.has(p.id)&&data.participants.find(a=>a.id===p.id).shot===f.shot&&finite(p.x)&&finite(p.y)&&p.x>=0&&p.x<=50&&p.y>=0&&p.y<=100,'schematic point/shot');seen.add(p.id);}
  if(f.ball!==null)need(keys(f.ball,'x y height screen')&&finite(f.ball.x)&&finite(f.ball.y)&&f.ball.x>=0&&f.ball.x<=50&&f.ball.y>=0&&f.ball.y<=100&&f.ball.height==='unknown'&&(f.ball.screen===null||(Array.isArray(f.ball.screen)&&f.ball.screen.length===2&&f.ball.screen.every(finite))),'ball observation');
 }
 need(JSON.stringify(data.shots)===JSON.stringify([{id:'corner',support:[3712,3714.4]},{id:'wide',support:[3715,3718.6]}]),'shot support');
 need(JSON.stringify(data.cuts)===JSON.stringify([{bounds:[3714.92,3714.96],from:'corner',to:'wide'},{bounds:[3718.76,3718.8],from:'wide',to:'celebration'}]),'camera boundaries');
 need(Array.isArray(data.facts)&&data.facts.length===3,'bounded facts');for(const f of data.facts)need(keys(f,'id availableAt evidenceIds text')&&finite(f.availableAt)&&Array.isArray(f.evidenceIds)&&f.evidenceIds.length>0&&f.evidenceIds.every(id=>data.frames.some(p=>p.id===id))&&f.availableAt===Math.max(...f.evidenceIds.map(id=>data.frames.find(p=>p.id===id).time))&&typeof f.text==='string'&&f.text.length<400,'fact endpoint');
 need(Array.isArray(data.limits)&&data.limits.length===9&&data.limits.every(s=>typeof s==='string'&&s.length<700),'limits');
 const {start,end,initial,actionStart,actionEnd}=data.bounds;
 const clamp=t=>Math.max(start,Math.min(end,finite(t)?t:initial));
 function view(time){
  const valid=finite(time)&&time>=start&&time<=end;const exact=valid?data.frames.find(f=>f.time===time):null,index=data.frames.findIndex(f=>f.time>time),a=exact||data.frames[index-1],b=exact||data.frames[index],shot=data.shots.find(s=>time>=s.support[0]&&time<=s.support[1]);
  const can=!!(shot&&a&&b&&a.shot===shot.id&&b.shot===shot.id&&b.time-a.time<=m.playerInterpolationMax&&!data.cuts.some(c=>a.time<c.bounds[1]&&b.time>c.bounds[0]));
  const players=data.participants.map(actor=>{const p=can?a.points.find(p=>p.id===actor.id):null,q=can?b.points.find(p=>p.id===actor.id):null;return {...actor,available:!!(p&&q),position:p&&q?{x:p.x+(q.x-p.x)*(a===b?0:(time-a.time)/(b.time-a.time)),y:p.y+(q.y-p.y)*(a===b?0:(time-a.time)/(b.time-a.time)),kind:exact?'reviewed_estimate':'interpolated_estimate',evidenceIds:exact?[a.id]:[a.id,b.id],usesFutureEndpoint:!exact}:null};});
  return {time,frame:exact?{id:exact.id,time:exact.time,clock:exact.clock,score:[...exact.score]}:null,shot:shot?.id||null,players,count:players.filter(p=>p.available).length,ball:exact?.ball?{...copy(exact.ball),evidenceIds:[exact.id],kind:'reviewed_estimate'}:null,facts:valid?data.facts.filter(f=>f.availableAt<=time).map(copy):[],gap:!players.some(p=>p.available)};
 }
 function answer(time,id,question){
  const v=view(time),actor=v.players.find(p=>p.id===id),kind=questions[normalize(question)]||'unsupported',point=actor?.position,evidenceIds=point?.evidenceIds||[];
  let text;
  if(kind==='position')text=(actor?.label||'Selected tracklet')+': '+(point?`a broad schematic estimate ${point.x<8?'near the goalmouth':point.y>60?'on the near side of the attacking area':'in the attacking area'}. ${point.kind==='interpolated_estimate'?'Interpolated between neighbouring reviewed frames, including a future endpoint; not an observed running path.':'Manual placement at a reviewed source frame.'}`:'position unknown at this cursor. The track is off camera, ambiguous, occluded or outside its supported shot. No position is carried across the gap.');
  else if(kind==='ball')text=v.ball?'A ball marker is supported at this exact reviewed frame. Its airborne layout is an apparent schematic projection; height and ground position are unknown. No contact or flight path is inferred.':'Ball position unknown at this cursor. Ball observations are exact-frame only; none is held or interpolated through occlusion or between samples.';
  else if(kind==='coverage')text=`${v.count} anonymous tracklet positions are supported here${v.ball?'; an exact-frame schematic ball observation is available':'; ball position is unknown'}. This is visible coverage, not a full XI or a count of distinct players. `+(v.frame?`Captured display ${v.frame.clock}, score ${v.frame.score.join('–')}; a scoreboard is not an exact goal time.`:'No exact reviewed frame here: captured clock and scoreboard are unknown.');
  else if(kind==='evidence')text=(point?`Selected tracklet: ${point.evidenceIds.join(' / ')}. `:'No selected-player position evidence at this cursor. ')+(v.ball?`Ball: ${v.ball.evidenceIds.join(' / ')}. `:'Ball: no observation here. ')+`Manual body-base placements against goal, box and touchline landmarks; uncalibrated, with broad schematic uncertainty. ${point?.usesFutureEndpoint?'The interpolation uses the neighbouring future frame. ':''}Camera cuts and ambiguous identities interrupt support.`;
  else if(kind==='history')text=v.facts.length?v.facts.map(f=>f.text).join(' '):'No earlier completed observation is available in this bounded passage. Sparse coverage does not establish that no football happened.';
  else if(kind==='missing')text='Off-camera players, unresolved identities and occlusions remain unknown. B/C tags are local anonymous labels, not shirt numbers. Ball height, ground position, exact contacts, scorer/assist, goal time, tactics, speed and distance are unverified. No full-team tracking or live feed is supplied.';
  else text='This local preview supports the selected tracklet’s estimated position, current visibility, ball evidence and earlier completed observations. Other free-text interpretations, goals, scorer/assist, contacts and metrics are unsupported. Use a suggested question.';
  return {kind,text,time,participantId:id,evidenceIds:kind==='ball'?v.ball?.evidenceIds||[]:kind==='history'?[...new Set(v.facts.flatMap(f=>f.evidenceIds))]:[...evidenceIds]};
 }
 return {start,end,initial,actionStart,actionEnd,participants:copy(data.participants),source:copy(data.source),limits:[...data.limits],view,answer,clamp,adjacent:(t,d)=>(d<0?data.frames.filter(f=>f.time<t).at(-1):data.frames.find(f=>f.time>t))?.time??null};
}
const api={create};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.PLGoalModel=api;
})(typeof window!=='undefined'?window:globalThis);

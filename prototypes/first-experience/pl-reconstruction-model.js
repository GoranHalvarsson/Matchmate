(function(root){
'use strict';
const finite=n=>typeof n==='number'&&Number.isFinite(n),same=(a,b)=>JSON.stringify(a)===JSON.stringify(b);
const need=(ok,message)=>{if(!ok)throw new Error('PL reconstruction: '+message);};
const exact=(o,keys)=>!!o&&typeof o==='object'&&!Array.isArray(o)&&same(Object.keys(o).sort(),keys.split(' ').sort());
const receipt=r=>exact(r,'sha256 bytes')&&/^[a-f0-9]{64}$/.test(r.sha256)&&Number.isInteger(r.bytes)&&r.bytes>0;
const times=[3715,3715.5,3716,3716.5,3717,3717.5,3718],clocks=['60:22','60:22','60:23','60:23','60:24','60:24','60:25'];
const positions={shirt27:[[20,73],[20,72],[19,70],[18.5,69],[18,68],[18.5,67],[19,65]],goalkeeper:[[3,45],[4,44],[4,43],[4,42],[3,44],[3,45],[3,46]]};
const interpolated='Interpolated estimate using neighbouring reviewed frames; retrospective reconstruction, not known-then.';
function create(input,annotations){
 need(exact(input,'version kind fixtureId source method bounds policy participants frames segments cuts limits'),'bundle allowlist');
 const data=JSON.parse(JSON.stringify(input));
 need(data.version===1&&data.kind==='local_manual_video_reconstruction'&&data.fixtureId===annotations?.fixture.id,'fixture/version');
 need(exact(data.source,'url reviewedOn annotationArtifact captureReceipt reconstructionArtifact')&&data.source.url===annotations.source.url&&data.source.reviewedOn==='2026-10-17','source');
 need(receipt(data.source.annotationArtifact)&&same(data.source.annotationArtifact,annotations.source.artifact)&&receipt(data.source.captureReceipt)&&receipt(data.source.reconstructionArtifact),'source receipts');
 need(same(data.method,{sampling:'seven_paused_same_shot_frames',continuousReview:false,timing:'DOM_currentTime_and_burned_in_second_clock',staleTransportText:'01:01:45',layout:'manual_normalized_landmark_estimates',axes:'x0_left_goal_x100_opposite_goal_y0_far_touchline_y100_near_touchline',calibration:'none',uncertaintyRadius:8,uncertaintyUnit:'schematic_percentage_units_not_metres_or_statistical_confidence'}),'manual method/uncertainty');
 need(same(data.bounds,{start:3715,end:3718,inspectEnd:3718.5})&&same(data.policy,{interpolation:'linear_adjacent_same_track_same_shot',maxGapSeconds:0.5,carryOutside:false,liveKnownThen:false,measuredTracking:false,namedIdentities:false,ball:false}),'bounded interpolation policy');
 const participants=[{id:'shirt27',label:'Brentford shirt 27',team:'brentford',shirt:27,identity:null,basis:'same_shot_visible_team_and_shirt_27'},{id:'goalkeeper',label:'Chelsea goalkeeper',team:'chelsea',shirt:null,identity:null,basis:'same_shot_sole_green_goalkeeper'}];
 need(same(data.participants,participants),'two anonymous participants only');
 need(Array.isArray(data.frames)&&data.frames.length===7,'seven sampled frames');
 for(const [i,f] of data.frames.entries()){
  need(exact(f,'id time clock score shotId imageReceipt dom points')&&f.id==='R'+String(i+1).padStart(2,'0')&&f.time===times[i]&&f.clock===clocks[i]&&same(f.score,[0,0])&&f.shotId==='wide-goalmouth','reviewed frame chronology/clock');
  need(receipt(f.imageReceipt)&&same(f.dom,{time:f.time,paused:true,ready:4,seeking:false}),'paused DOM/image receipt');
  need(Array.isArray(f.points)&&f.points.length===2,'two approved points');
  for(const [j,p] of f.points.entries()){const id=participants[j].id;need(exact(p,'participantId x y')&&p.participantId===id&&finite(p.x)&&finite(p.y)&&same([p.x,p.y],positions[id][i]),'approved schematic point/identity');}
 }
 need(same(data.segments,[{id:'S01',start:3715,end:3718,shotId:'wide-goalmouth',frameIds:data.frames.map(f=>f.id),participantIds:participants.map(p=>p.id)}]),'single approved same-shot segment');
 const cuts=annotations.cuts.filter(c=>c.bounds[1]===3715||c.bounds[0]===3718).map(c=>({after:c.after,before:c.before,bounds:c.bounds}));need(same(data.cuts,cuts),'original camera boundaries');
 need(Array.isArray(data.limits)&&data.limits.length===8&&data.limits.every(l=>typeof l==='string'&&l.length>0&&l.length<650),'scope limits');
 const {start,end,inspectEnd}=data.bounds;
 function view(time,participantId){
  const actor=participants.find(p=>p.id===participantId),frame=finite(time)?data.frames.find(f=>f.time===time):null;
  let position=null;
  if(actor&&finite(time)&&time>=start&&time<=end){
   const after=data.frames.findIndex(f=>f.time>=time),a=frame||data.frames[after-1],b=frame||data.frames[after];
   const segment=data.segments.find(s=>s.start<=time&&s.end>=time&&s.participantIds.includes(actor.id));
   if(a&&b&&segment&&a.shotId===b.shotId&&a.shotId===segment.shotId&&b.time-a.time<=data.policy.maxGapSeconds&&!data.cuts.some(c=>a.time<c.bounds[1]&&b.time>c.bounds[0])){
    const p=a.points.find(p=>p.participantId===actor.id),q=b.points.find(p=>p.participantId===actor.id),ratio=a===b?0:(time-a.time)/(b.time-a.time);
    if(p&&q)position={participantId:actor.id,x:p.x+(q.x-p.x)*ratio,y:p.y+(q.y-p.y)*ratio,radius:data.method.uncertaintyRadius,kind:frame?'reviewed_estimate':'interpolated_estimate',evidenceIds:frame?[frame.id]:[a.id,b.id],bounds:[a.time,b.time],usesFutureEndpoint:!frame,disclosure:frame?'Manual layout estimate at a reviewed frame; not measured tracking.':interpolated};
   }
  }
  return {time,frame:frame?{id:frame.id,time:frame.time,clock:frame.clock,score:[...frame.score]}:null,position};
 }
 function answer(time,participantId,question){
  const kind=annotationsApiClassify(question),v=view(time,participantId),actor=participants.find(p=>p.id===participantId),p=v.position;
  let text;
  const support=p?`Evidence ${p.evidenceIds.join(' / ')}; clip bounds ${p.bounds.join('–')} seconds. ${p.disclosure}`:'No supported position at this cursor. No estimate is carried outside the sampled segment.';
  if(kind==='position')text=`${actor?.label||'Selected participant'}: ${p?(actor.id==='shirt27'?'near the near-side penalty-area edge':'near the goalmouth')+', as a broad schematic estimate.':'position unknown.'} ${support}`;
  else if(kind==='observed')text=(v.frame?`Reviewed frame ${v.frame.id} supports broad manual layout estimates for two anonymous participants. `:'No reviewed frame exists at this exact cursor. ')+support+' Ball, contacts and the other players remain unknown.';
  else if(kind==='missing')text='Ball and other players, named identities, exact contacts, scorer action, goal time, outcome, tactics, speed and distance are unknown. Seven sampled frames are not continuous or frame-complete review. Broad schematic uncertainty is not metric or statistical confidence. '+support;
  else text='This reconstruction supports the selected anonymous participant’s estimated position, evidence and missing information. Contacts, scorer, outcome, metrics and other questions are unsupported.';
  return {kind:['position','observed','missing'].includes(kind)?kind:'unsupported',text,time,participantId,evidenceIds:p?[...p.evidenceIds]:[],bounds:p?[...p.bounds]:null};
 }
 const annotationsApiClassify=q=>root.PLAnnotationsModel?root.PLAnnotationsModel.classify(q):require('./pl-annotations-model.js').classify(q);
 return {start,end,inspectEnd,participants:participants.map(p=>({...p})),view,answer,adjacent:(time,direction)=>(direction<0?data.frames.filter(f=>f.time<time).at(-1):data.frames.find(f=>f.time>time))?.time??null,gap:()=>end+0.25,clamp:time=>Math.max(start,Math.min(inspectEnd,finite(time)?time:start))};
}
const api={create};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.PLReconstructionModel=api;
})(typeof window!=='undefined'?window:globalThis);

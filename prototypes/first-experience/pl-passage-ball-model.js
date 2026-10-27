(function(root){
'use strict';
const need=(ok,s)=>{if(!ok)throw Error('Passage ball: '+s);};
const keys=(o,s)=>o&&typeof o==='object'&&!Array.isArray(o)&&Object.keys(o).sort().join(' ')===s.split(' ').sort().join(' ');
const finite=n=>typeof n==='number'&&Number.isFinite(n),hash=s=>typeof s==='string'&&/^[a-f0-9]{64}$/.test(s);
const receipt=r=>keys(r,'sha256 bytes')&&hash(r.sha256)&&Number.isInteger(r.bytes)&&r.bytes>0;
const freeze=o=>{if(o&&typeof o==='object'){Object.values(o).forEach(freeze);Object.freeze(o);}return o;};
const normalize=q=>String(q).toLowerCase().replace(/[^a-z0-9 ]/g,' ').trim().replace(/\s+/g,' ');
const approved=[50,100,300,400,650,725,730,735,740,745,750,755,760,765,770,775,780,785,790,795,800,805,810,815,820,825,830,835,850,855,860,870,875,880,885,890,895,900,910,915,920,925,950,1075,1275,1450,1500,1575,1625];
const ground=[765,770,775,780,785,790,795,800,805,810,815,825,830];
const personHash='27d9da0fa866b39d94a922085c07aac86aba2b9725842172446fead6dbaeb10c';
function create(input,person,personReceipt,nativeInput,nativeReceipt){
 need(keys(input,'version kind fixtureId source bounds policy observations'),'overlay allowlist');const d=JSON.parse(JSON.stringify(input));
 need(d.version===1&&d.kind==='retrospective_longer_passage_ball_overlay'&&d.fixtureId==='premier-league:2026-27:2026-09-18:brentford:chelsea','fixture/version');
 const s=d.source;need(keys(s,'url videoSha256 researchLedger personArtifact')&&s.url==='https://www.brentfordfc.com/en/video/0_2hoy7vw7?provider=stream'&&s.videoSha256==='87b511b61f3d48e410294e06bf71bbc3a88c7b7ca6a50b89e572808c3e82711f'&&receipt(s.researchLedger)&&receipt(s.personArtifact)&&s.personArtifact.sha256===personHash&&s.personArtifact.bytes===550466,'pinned source receipts');
 need(person?.fixtureId===d.fixtureId&&person.source?.videoSha256===s.videoSha256&&person.kind==='estimated_consecutive_person_passage'&&personReceipt?.artifact?.sha256===personHash&&personReceipt.artifact.bytes===550466,'matching immutable person dataset receipt');
 need(keys(d.bounds,'start end step')&&d.bounds.start===4950&&d.bounds.end===5017.2&&d.bounds.step===.2&&JSON.stringify(person.bounds)===JSON.stringify(d.bounds)&&Array.isArray(person.samples)&&person.samples.length===337,'matching sample bounds');
 person.samples.forEach((p,i)=>need(p.nativeIndex===i*5&&Math.abs(p.time-(4950+i*.2))<1e-7&&p.pts===Math.round(p.time*12800),'person source alignment'));
 const expected={position:'exact_sample_only',hold:false,interpolation:false,knownThen:false,sourceAvailability:null,screenOnlyHasPitch:false,fieldModel:[105,68],dimensionsVerified:false,events:false};
 need(keys(d.policy,Object.keys(expected).join(' '))&&Object.entries(expected).every(([k,v])=>JSON.stringify(d.policy[k])===JSON.stringify(v)),'retrospective support policy');
 need(Array.isArray(d.observations)&&d.observations.length===49,'49 reviewed screen observations');
 d.observations.forEach((o,i)=>{
  need(keys(o,'id nativeIndex time pts screen height pitch sourceImage support')&&o.nativeIndex===approved[i]&&o.id==='ball-'+String(o.nativeIndex).padStart(4,'0')&&finite(o.time)&&Math.abs(o.time-(4950+o.nativeIndex/25))<1e-7&&o.pts===Math.round(o.time*12800),'approved exact observations');
  need(keys(o.screen,'x y radiusPx method')&&finite(o.screen.x)&&finite(o.screen.y)&&o.screen.x>=0&&o.screen.x<=1024&&o.screen.y>=0&&o.screen.y<=576&&finite(o.screen.radiusPx)&&o.screen.radiusPx>0&&o.screen.radiusPx<=20&&o.screen.method===(o.nativeIndex===820?'manual_overlap':'reviewed_detector_proposal'),'screen observation region');
  need(['height_ambiguous','apparent_elevated_height_unknown','apparent_near_ground_height_unmeasured'].includes(o.height),'height uncertainty');
  need(keys(o.sourceImage,'pixelSha256 pngSha256 cropSha256')&&Object.values(o.sourceImage).every(hash),'source image hashes');
  const support=o.support;need(keys(support,'start end knownThen usesLaterFrames frames')&&support.knownThen===false&&support.usesLaterFrames===true&&Math.abs(support.start-(o.time-.08))<1e-7&&Math.abs(support.end-(o.time+.08))<1e-7&&Array.isArray(support.frames)&&support.frames.length===5,'retrospective nearby-frame support');
  support.frames.forEach((f,j)=>need(keys(f,'nativeIndex pts pixelSha256 pngSha256')&&f.nativeIndex===o.nativeIndex+j-2&&f.pts===o.pts+(j-2)*512&&hash(f.pixelSha256)&&hash(f.pngSha256),'exact supporting PTS and hashes'));
  need(support.frames[2].pixelSha256===o.sourceImage.pixelSha256&&support.frames[2].pngSha256===o.sourceImage.pngSha256,'current source receipt join');
  if(ground.includes(o.nativeIndex)){
   const p=o.pitch;need(keys(p,'x y nativeGroundProxyPx local8pxSensitivityPercent')&&finite(p.x)&&finite(p.y)&&p.x>=0&&p.x<=100&&p.y>=0&&p.y<=100&&Array.isArray(p.nativeGroundProxyPx)&&p.nativeGroundProxyPx.length===2&&p.nativeGroundProxyPx.every(finite)&&Math.abs(p.nativeGroundProxyPx[0]-o.screen.x)<1e-7&&p.nativeGroundProxyPx[1]>=o.screen.y&&p.nativeGroundProxyPx[1]<=576&&finite(p.local8pxSensitivityPercent)&&p.local8pxSensitivityPercent>0&&o.height==='apparent_near_ground_height_unmeasured','uncertain ground estimate');
   need(person.samples[o.nativeIndex/5].camera==='estimated','no pitch ball in camera gap');
  }else need(o.pitch===null&&o.height!=='apparent_near_ground_height_unmeasured','screen-only cannot acquire pitch point');
 });
 const native=nativeInput===undefined&&nativeReceipt===undefined?null:validateNative(nativeInput,nativeReceipt,d.source,person);
 freeze(d);const byIndex=new Map(d.observations.map(o=>[o.nativeIndex,o])),times=Object.freeze(d.observations.filter(o=>o.pitch).map(o=>o.time));
 if(native)for(const o of native.observations)byIndex.set(o.nativeIndex,o);
 function view(t){if(!finite(t))return {state:'unknown',observation:null,pitch:null};const n=Math.round((t-4950)*25),o=Math.abs(t-(4950+n/25))<1e-7?byIndex.get(n):null;return o?{state:o.pitch?'estimated':'screen_only',observation:o,pitch:o.pitch}:{state:'unknown',observation:null,pitch:null};}
 function describe(t){const v=view(t);return v.observation?.method==='manual_lower_edge_M803-1'?'Ball: manual near-ground estimate · M803-1':v.state==='estimated'?'Ball: uncertain near-ground pitch estimate':v.state==='screen_only'?'Ball seen in video; pitch position unknown':'Ball: unknown · unreviewed or ambiguous sample';}
 function answer(t,q){const k=normalize(q),v=view(t);if(!['where is the ball','what is visible here','what supports this'].includes(k))return null;
  const detail=v.observation?.method==='manual_lower_edge_M803-1'?'The ball uses the separately reviewed manual lower-edge estimate M803-1 at native frame 803, source 4982.12 seconds. The image point has broad ±4-pixel uncertainty; apparent near-ground context supports an estimate, not measured height/contact or ground accuracy.':v.state==='estimated'?'The ball has an uncertain near-ground pitch estimate at this exact source sample. Apparent low/foot-level movement supports a ground-plane estimate; height/contact, camera and dimensions remain uncertain.':v.state==='screen_only'?'Ball seen in video; pitch position unknown at this exact sample. Height or ground contact is not supported well enough for a pitch marker.':'Ball position is unknown at this unreviewed or ambiguous sample. No earlier/later point is held or interpolated here.';
  if(k==='where is the ball')return {kind:'ball',time:t,state:v.state,text:detail};
  if(k==='what is visible here')return {kind:'ball_coverage',time:t,state:v.state,text:detail+' The 5 Hz passage retains 49 reviewed video observations and 13 pitch estimates among 337 source samples'+(native?'; ball detail adds a reviewed 55-frame native segment over 2.16 seconds, including one separate manual estimate. Overlapping instants use the native estimate. This does not extend continuous tracking over the whole passage.':'; it is not continuous ball tracking.')};
  return {kind:'ball_evidence',time:t,state:v.state,text:detail+' Ball adjudication is retrospective and uses supporting frames'+(v.observation?' from source '+v.observation.support.start.toFixed(2)+' to '+v.observation.support.end.toFixed(2)+' seconds, including frames after this sample':' where an observation was accepted')+'. This is not real-time known-then knowledge. Exact source PTS/image hashes are retained; each native estimate uses its own qualified camera. No event, name or possession is inferred.'};
 }
 const detail=native?Object.freeze({start:native.bounds.start,end:native.bounds.end,step:.04,count:55,index:n=>Math.max(0,Math.min(54,finite(n)?Math.round(n):0)),at:n=>native.observations[Math.max(0,Math.min(54,finite(n)?Math.round(n):0))]}):null;
 return Object.freeze({view,describe,answer,times,detail,source:d.source,continuitySource:native?.source||null,policy:d.policy,screenCount:byIndex.size,pitchCount:[...byIndex.values()].filter(o=>o.pitch).length,sampleCount:337,next:t=>finite(t)?(times.find(x=>x>t+1e-7)??times[0]):null});
}
function validateNative(input,r,sparse,person){
 need(keys(input,'version kind fixtureId source bounds policy observations')&&keys(r,'version artifact source nativeObservations automaticGroundEstimates manualAugmentation firstNativeIndex lastNativeIndex runtimeMode sourceMediaBundled publicationClearance'),'native artifact/receipt allowlist');
 const d=JSON.parse(JSON.stringify(input));need(d.version===1&&d.kind==='reviewed_native_ball_continuity'&&d.fixtureId===person.fixtureId,'native fixture/version');
 const pins={personArtifact:{sha256:personHash,bytes:550466},sparseBallArtifact:{sha256:'9b715612300936354839436d4cf4f0387933cd3df65cfe431e3a364428ba5378',bytes:78830},trialLedger:{sha256:'59efe2064d1afa4a5e447148aac9c76028ce8ff19b37c8eb8e8fa51af65bd281',bytes:347010},trialProtocol:{sha256:'c31352345efd8eb16632b2abb24c859990006df83d39d9fdeb95ebcf851d34cc',bytes:4998},groundReviewPolicy:{sha256:'820b257f1eaa06898d1c56e948805dfa6087128009bc9c078260f35af953a859',bytes:1974},manualAugmentation:{sha256:'22da8713a677b287261ae410babed3dc4075dda108ee740e5147df4cd9bd8ae5',bytes:6113}};
 need(keys(d.source,'url videoSha256 '+Object.keys(pins).join(' '))&&d.source.url===sparse.url&&d.source.videoSha256===sparse.videoSha256&&Object.entries(pins).every(([k,v])=>JSON.stringify(d.source[k])===JSON.stringify(v))&&JSON.stringify(r.source)===JSON.stringify(d.source),'native pinned provenance');
 need(r.version===1&&keys(r.artifact,'file sha256 bytes')&&r.artifact.file==='brentford-chelsea-2026-09-18-ball-continuity.json'&&hash(r.artifact.sha256)&&r.artifact.bytes>0&&r.nativeObservations===55&&r.automaticGroundEstimates===54&&r.manualAugmentation==='M803-1'&&r.firstNativeIndex===765&&r.lastNativeIndex===819&&r.runtimeMode==='ball_only_native_detail'&&r.sourceMediaBundled===false&&r.publicationClearance===false,'native receipt scope');
 need(JSON.stringify(d.bounds)==='{"start":4980.6,"end":4982.76,"step":0.04,"firstNativeIndex":765,"lastNativeIndex":819}','native bounds');
 const policy={position:'exact_native_only',hold:false,interpolation:false,knownThen:false,sourceAvailability:null,fieldModel:[105,68],dimensionsVerified:false,peopleInDetail:'hidden',events:false,precedence:'native_segment_over_sparse_at_exact_time'};
 need(keys(d.policy,Object.keys(policy).join(' '))&&Object.entries(policy).every(([k,v])=>JSON.stringify(d.policy[k])===JSON.stringify(v)),'native exact-only policy');
 need(Array.isArray(d.observations)&&d.observations.length===55,'55 native observations');
 d.observations.forEach((o,j)=>{
  const n=765+j;need(keys(o,'id nativeIndex time pts screen pitch method height sourceImage camera support manual')&&o.nativeIndex===n&&o.id==='continuity-ball-'+String(n).padStart(4,'0')&&finite(o.time)&&Math.abs(o.time-(4950+n/25))<1e-7&&o.pts===Math.round(o.time*12800),'native exact PTS');
  need(keys(o.screen,'x y radiusPx')&&finite(o.screen.x)&&finite(o.screen.y)&&o.screen.x>=0&&o.screen.x<=1024&&o.screen.y>=0&&o.screen.y<=576&&o.screen.radiusPx===4&&o.height==='apparent_near_ground_height_unmeasured','native screen/height');
  need(keys(o.sourceImage,'pixelSha256 pngSha256')&&Object.values(o.sourceImage).every(hash),'native source image receipts');
  const c=o.camera;need(keys(c,'nativeIndex projection normalizedCondition fitErrorPx rawKeypoints visualReview')&&c.nativeIndex===n&&finite(c.normalizedCondition)&&c.normalizedCondition>0&&c.normalizedCondition<=1e6&&finite(c.fitErrorPx)&&c.fitErrorPx>=0&&c.fitErrorPx<=10&&Number.isInteger(c.rawKeypoints)&&c.rawKeypoints>=4&&c.visualReview==='approximate_native_field_overlay'&&Array.isArray(c.projection)&&c.projection.length===3&&c.projection.every(a=>Array.isArray(a)&&a.length===4&&a.every(finite)),'qualified exact native camera');
  const p=o.pitch;need(keys(p,'x y nativeGroundProxyPx local8pxSensitivityPercent')&&finite(p.x)&&finite(p.y)&&p.x>=0&&p.x<=100&&p.y>=0&&p.y<=100&&Array.isArray(p.nativeGroundProxyPx)&&p.nativeGroundProxyPx.length===2&&p.nativeGroundProxyPx.every(finite)&&p.nativeGroundProxyPx[0]>=0&&p.nativeGroundProxyPx[0]<=1024&&p.nativeGroundProxyPx[1]>=0&&p.nativeGroundProxyPx[1]<=576&&finite(p.local8pxSensitivityPercent)&&p.local8pxSensitivityPercent>0,'native ground proxy');
  const P=c.projection,x=p.x*1.05-52.5,y=p.y*.68-34,z=P[2][0]*x+P[2][1]*y+P[2][3],u=(P[0][0]*x+P[0][1]*y+P[0][3])/z,v=(P[1][0]*x+P[1][1]*y+P[1][3])/z;
  const a=P[0][0],b=P[0][1],cc=P[0][3],dd=P[1][0],e=P[1][1],f=P[1][3],g=P[2][0],h=P[2][1],ii=P[2][3],det=a*(e*ii-f*h)-b*(dd*ii-f*g)+cc*(dd*h-e*g);
  need(finite(det)&&Math.abs(det)>1e-9&&finite(u)&&finite(v)&&Math.hypot(u-p.nativeGroundProxyPx[0],v-p.nativeGroundProxyPx[1])<1e-6,'native camera projection agreement');
  const s=o.support;need(keys(s,'startNativeIndex endNativeIndexInclusive start end knownThen usesLaterFrames')&&s.startNativeIndex===(n<=815?765:815)&&s.endNativeIndexInclusive===(n<815?815:835)&&Math.abs(s.start-(4950+s.startNativeIndex/25))<1e-7&&Math.abs(s.end-(4950+s.endNativeIndexInclusive/25))<1e-7&&s.knownThen===false&&s.usesLaterFrames===true&&s.start<=o.time&&s.end>o.time,'native retrospective support');
  if(n===803)need(o.method==='manual_lower_edge_M803-1'&&keys(o.manual,'revision pixelUncertaintyRadius contextNativeIndices')&&o.manual.revision==='M803-1'&&o.manual.pixelUncertaintyRadius===4&&JSON.stringify(o.manual.contextNativeIndices)==='[801,802,803,804,805]'&&JSON.stringify(p.nativeGroundProxyPx)==='[123,320]','separate manual803 provenance');
  else need(o.method==='reviewed_bidirectional_track_current_box'&&o.manual===null,'reviewed track/current-box method');
  if(n%5===0)need(person.samples[n/5].camera==='estimated','native cannot override camera gap');
 });
 return freeze(d);
}
const api={create};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.PLPassageBallModel=api;
})(typeof window!=='undefined'?window:globalThis);

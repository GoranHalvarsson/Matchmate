(function(root){
'use strict';
const need=(ok,s)=>{if(!ok)throw Error('Combined passage: '+s);};
const keys=(o,s)=>o&&typeof o==='object'&&!Array.isArray(o)&&Object.keys(o).sort().join(' ')===s.split(' ').sort().join(' ');
const finite=n=>typeof n==='number'&&Number.isFinite(n),hash=s=>typeof s==='string'&&/^[a-f0-9]{64}$/.test(s);
const freeze=o=>{if(o&&typeof o==='object'){Object.values(o).forEach(freeze);Object.freeze(o);}return o;};
const normalize=q=>String(q).toLowerCase().replace(/[^a-z0-9 ]/g,' ').trim().replace(/\s+/g,' ');
const range=(a,b)=>Array.from({length:b-a+1},(_,i)=>a+i);
const pitchFrames=new Set([...range(765,819),...range(822,835),...range(848,864),...range(908,925)]);
const unknownFrames=new Set([...range(841,847),882,883,...range(901,907)]);
const fixture='premier-league:2026-27:2026-09-18:brentford:chelsea';
function projected(P,x,y,pixel){
 const a=P[0][0],b=P[0][1],c=P[0][3],d=P[1][0],e=P[1][1],f=P[1][3],g=P[2][0],h=P[2][1],i=P[2][3],det=a*(e*i-f*h)-b*(d*i-f*g)+c*(d*h-e*g);
 need(finite(det)&&Math.abs(det)>1e-9,'invertible native camera');
 const X=x*1.05-52.5,Y=y*.68-34,z=g*X+h*Y+i,u=(a*X+b*Y+c)/z,v=(d*X+e*Y+f)/z;
 need(finite(u)&&finite(v)&&Math.hypot(u-pixel[0],v-pixel[1])<1e-5,'current camera/ground reprojection');
}
function pixel(p){return Array.isArray(p)&&p.length===2&&p.every(finite)&&p[0]>=0&&p[0]<=1024&&p[1]>=0&&p[1]<=576;}
function position(p){return finite(p.x)&&finite(p.y)&&p.x>=0&&p.x<=100&&p.y>=0&&p.y<=100;}
function create(input,receipt,base,nativeBall){
 need(keys(input,'version kind fixtureId source bounds policy coverage frames')&&keys(receipt,'version artifact source nativeFrames personEstimates ballPitchFrames ballScreenOnlyFrames ballUnknownFrames manualAugmentation sourceMediaBundled publicationClearance'),'artifact/receipt allowlist');
 const d=JSON.parse(JSON.stringify(input));need(d.version===1&&d.kind==='reviewed_combined_native_passage'&&d.fixtureId===fixture&&nativeBall?.fixtureId===fixture&&nativeBall.observations?.length===55&&base?.start===4950&&base?.end===5017.2,'fixture and original layers');
 const pins={personArtifact:{sha256:'27d9da0fa866b39d94a922085c07aac86aba2b9725842172446fead6dbaeb10c',bytes:550466},ballArtifact:{sha256:'028f0b59242f9f393779bea4d69a703896bd2c4fccb86eb82c1e61ad5de3536f',bytes:102358},nativeResearch:{sha256:'7a006c30cb66227433172228c78657e2e1256f15fb3ab719176600f59c9bc9cc',bytes:22093469},reviewLedger:{sha256:'1c02a72d477244932dbb3cebeb47c7eed05205ef776a72280ba05753cc240fd0',bytes:331878},reviewProtocol:{sha256:'4c6fc3d9418aee5327958c7cfa36f1147748bcfb54aeee3fa93198bb54e9e144',bytes:1697}};
 need(keys(d.source,'url videoSha256 '+Object.keys(pins).join(' '))&&d.source.url==='https://www.brentfordfc.com/en/video/0_2hoy7vw7?provider=stream'&&d.source.videoSha256==='87b511b61f3d48e410294e06bf71bbc3a88c7b7ca6a50b89e572808c3e82711f'&&Object.entries(pins).every(([k,v])=>JSON.stringify(d.source[k])===JSON.stringify(v))&&JSON.stringify(receipt.source)===JSON.stringify(d.source),'pinned source/review provenance');
 need(receipt.version===1&&keys(receipt.artifact,'file sha256 bytes')&&receipt.artifact.file==='brentford-chelsea-2026-09-18-combined-native-passage.json'&&hash(receipt.artifact.sha256)&&Number.isInteger(receipt.artifact.bytes)&&receipt.artifact.bytes>0&&receipt.nativeFrames===201&&receipt.personEstimates===2512&&receipt.ballPitchFrames===104&&receipt.ballScreenOnlyFrames===81&&receipt.ballUnknownFrames===16&&receipt.manualAugmentation==='M803-1'&&receipt.sourceMediaBundled===false&&receipt.publicationClearance===false,'receipt counts/scope');
 need(JSON.stringify(d.bounds)==='{"start":4979,"end":4987,"step":0.04,"firstNativeIndex":725,"lastNativeIndex":925}','201 exact bounds');
 const policy={position:'exact_native_only',hold:false,interpolation:false,knownThen:false,sourceAvailability:null,fieldModel:[105,68],dimensionsVerified:false,independentPeopleBall:true,identity:'proposed_anonymous_tracklets',events:false};
 need(keys(d.policy,Object.keys(policy).join(' '))&&Object.entries(policy).every(([k,v])=>JSON.stringify(d.policy[k])===JSON.stringify(v)),'independent exact-only policy');
 need(JSON.stringify(d.coverage)==='{"reviewedNativeFrames":201,"personEstimates":2512,"ballPitchFrames":104,"ballScreenOnlyFrames":81,"ballUnknownFrames":16,"longestBallRunFrames":55,"longestBallRunSpanSeconds":2.16,"manualAugmentation":"M803-1"}','reviewed coverage scope');
 need(Array.isArray(d.frames)&&d.frames.length===201,'201 reviewed native frames');
 const prior=new Map(nativeBall.observations.map(o=>[o.nativeIndex,o])),ids=new Set();let people=0;const counts={estimated:0,screen_only:0,unknown:0};
 d.frames.forEach((f,j)=>{
  const n=725+j;need(keys(f,'nativeIndex time pts sourceImage camera people withheldCount ball')&&f.nativeIndex===n&&finite(f.time)&&Math.abs(f.time-(4950+n/25))<1e-7&&f.pts===Math.round(f.time*12800),'exact frame PTS');
  need(keys(f.sourceImage,'pixelSha256 pngSha256')&&Object.values(f.sourceImage).every(hash),'source image receipts');
  const c=f.camera;need(keys(c,'state projection normalizedCondition fitErrorPx rawKeypoints visualReview')&&c.state==='estimated'&&Array.isArray(c.projection)&&c.projection.length===3&&c.projection.every(a=>Array.isArray(a)&&a.length===4&&a.every(finite))&&finite(c.normalizedCondition)&&c.normalizedCondition>0&&c.normalizedCondition<=1e6&&finite(c.fitErrorPx)&&c.fitErrorPx>=0&&c.fitErrorPx<=10&&Number.isInteger(c.rawKeypoints)&&c.rawKeypoints>=4&&c.visualReview==='approximate_native_field_overlay','qualified exact camera');
  need(Array.isArray(f.people)&&f.people.length>=1&&f.people.length<=30&&Number.isInteger(f.withheldCount)&&f.withheldCount>=0,'independent people estimates');
  const local=new Set();for(const p of f.people){need(keys(p,'id x y nativeFootProxyPx bbox association local8pxSensitivityPercent')&&typeof p.id==='string'&&/^T\d+-S\d+$/.test(p.id)&&!local.has(p.id)&&p.association==='proposed-track'&&position(p)&&pixel(p.nativeFootProxyPx)&&finite(p.local8pxSensitivityPercent)&&p.local8pxSensitivityPercent>0&&Array.isArray(p.bbox)&&p.bbox.length===4&&p.bbox.every(finite),'anonymous person allowlist');
   const [x1,y1,x2,y2]=p.bbox;need(x1>=1&&x2<=1023&&y1>=0&&y2<=575&&x2>x1&&y2>y1&&x2-x1<y2-y1&&Math.abs(p.nativeFootProxyPx[0]-(x1+x2)/2)<1e-7&&p.nativeFootProxyPx[1]===y2,'supported foot proxy');projected(c.projection,p.x,p.y,p.nativeFootProxyPx);local.add(p.id);ids.add(p.id);people++;
  }
  const b=f.ball,expected=pitchFrames.has(n)?'estimated':unknownFrames.has(n)?'unknown':'screen_only';need(keys(b,'state pitch method imageUncertaintyRadiusPx support')&&b.state===expected,'reviewed independent ball states');counts[b.state]++;
  const s=b.support;need(keys(s,'startNativeIndex endNativeIndexInclusive knownThen usesLaterFrames')&&Number.isInteger(s.startNativeIndex)&&Number.isInteger(s.endNativeIndexInclusive)&&s.startNativeIndex<=n&&s.endNativeIndexInclusive>=n&&s.startNativeIndex>=725&&s.endNativeIndexInclusive<=925&&s.knownThen===false&&s.usesLaterFrames===(s.endNativeIndexInclusive>n),'retrospective ball support');
  if(b.state==='estimated'){const p=b.pitch;need(keys(p,'x y nativeGroundProxyPx local8pxSensitivityPercent')&&position(p)&&pixel(p.nativeGroundProxyPx)&&finite(p.local8pxSensitivityPercent)&&p.local8pxSensitivityPercent>0,'uncertain ball ground proxy');projected(c.projection,p.x,p.y,p.nativeGroundProxyPx);
   if(prior.has(n)){const o=prior.get(n);need(JSON.stringify(b.pitch)===JSON.stringify(o.pitch)&&b.method===o.method&&JSON.stringify(f.sourceImage)===JSON.stringify(o.sourceImage),'preserved prior55/manual point');}
   else need(b.method==='reviewed_current_frame_box_lower_edge','separate reviewed added proxy');
  }else need(b.pitch===null&&b.method===(b.state==='unknown'?'ambiguous_or_occluded':'seen_in_video_height_ambiguous'),'no pitch for ambiguity/height');
  need(b.imageUncertaintyRadiusPx===(n===803?4:null),'manual803 uncertainty only');
 });
 need(people===2512&&counts.estimated===104&&counts.screen_only===81&&counts.unknown===16,'computed coverage matches review');
 freeze(d);const labels=new Map(),used=new Set(base.samples.flatMap(f=>f.markers.map(p=>base.tag(p.id))));let next=0;
 const alpha=n=>{let s='';do{s=String.fromCharCode(65+n%26)+s;n=Math.floor(n/26)-1;}while(n>=0);return s;};
 for(const id of ids){if(base.has(id)){labels.set(id,base.tag(id));continue;}while(used.has(alpha(next)))next++;const tag=alpha(next++);labels.set(id,tag);used.add(tag);}
 const tag=id=>labels.get(id)||(base.has(id)?base.tag(id):'?'),label=id=>labels.has(id)||base.has(id)?'Track '+tag(id):'No track selected';
 const index=n=>Math.max(0,Math.min(200,finite(n)?Math.round(n):0)),at=n=>d.frames[index(n)];
 function exact(t){if(!finite(t))return null;const i=Math.round((t-4979)/.04);return i>=0&&i<=200&&Math.abs(d.frames[i].time-t)<1e-7?d.frames[i]:null;}
 function view(t,id){const sample=exact(t);return {time:t,sample,markers:sample?.people||[],selected:sample?.people.find(p=>p.id===id)||null,gap:!sample,ball:sample?.ball||{state:'unknown',pitch:null,method:'outside_reviewed_frame'}};}
 function ballDescription(t){const b=view(t).ball;return b.method==='manual_lower_edge_M803-1'?'Ball: manual near-ground estimate · M803-1':b.state==='estimated'?'Ball: uncertain ground-plane proxy estimate':b.state==='screen_only'?'Ball seen in video; pitch position unknown':'Ball: unknown · occluded, ambiguous or outside this passage';}
 function ballText(t){const b=view(t).ball;return b.method==='manual_lower_edge_M803-1'?'Manual lower-edge estimate M803-1 at source 4982.12 seconds, with broad ±4-pixel image uncertainty. Height/contact and metric ground accuracy are unmeasured.':b.state==='estimated'?'An uncertain ground-plane ball proxy is supported at this exact native frame. Apparent low/foot-level context supports the estimate; height/contact, camera and field dimensions remain uncertain.':b.state==='screen_only'?'The ball is seen in the source video, but its pitch position is unknown because ground height/contact is ambiguous. No pitch marker is shown.':'Ball position is withheld at this exact frame because the source is ambiguous/occluded or outside reviewed support. No earlier/later position is carried here.';}
 function answer(t,id,q){const v=view(t,id),k=normalize(q),text=(kind,s)=>({kind,time:t,selectedId:ids.has(id)||base.has(id)?id:null,text:s});
  if(k==='where is the ball')return text('ball',ballText(t));
  if(k==='where is this track')return text('position',v.selected?label(id)+' has an uncertain person-box foot-proxy estimate at this exact source frame. Its automatic anonymous association remains proposed; team/name/role are unknown.':label(id)+' has no supported position at this exact source frame. Missing detections and fragmented IDs stay missing; no position is held.');
  if(k==='what is visible here')return text('coverage',(v.sample?v.markers.length+' generic person candidates have current-frame estimates. People may include officials or residual duplicate candidates; this is not complete player recall. ':'No exact reviewed native frame. ')+ballText(t)+' This eight-second passage has 104 ball pitch-estimate frames, 81 screen-only and 16 unknown; the longest uninterrupted ball-estimate run is 2.16 seconds.');
  if(k==='why is there a gap')return text('gap',v.ball.state==='estimated'?'A ball proxy and current person candidates are supported here. Individual missing people still disappear; source/filter uncertainty remains.':'The ball has independent visibility/height/occlusion limits. Current person estimates can remain while the ball pitch marker disappears. Detector absence alone does not prove occlusion; no gap is filled.');
  if(k==='what supports this')return text('evidence','Existing official video, source '+(finite(t)?t.toFixed(2):'unknown')+' seconds'+(v.sample?' / native PTS '+v.sample.pts:'; no exact reviewed frame')+'. All 201 source stills were reviewed on chronological crop/overlay boards, not continuous video playback or metric ground truth. Each person/ball estimate uses the exact native camera and retained source hashes. The earlier55 ball estimates include separate manual M803-1; added points are explicitly reviewed lower-edge proxies. Reconstruction is retrospective and can use later context, not live known-then knowledge. Assumed pitch105 ×68 dimensions and automatic identities remain uncertain.');
  return text('unsupported','I can explain current candidates, selected-track support, ball states, gaps or sources. Names, teams, goals, possession, scores, future positions and movement metrics are not supported here.');
 }
 return Object.freeze({start:4979,end:4987,step:.04,count:201,at,index,view,answer,label,tag,has:id=>ids.has(id)||base.has(id),ballDescription,coverage:d.coverage,source:d.source,gap:i=>{for(let j=index(i)+1;j<201;j++)if(d.frames[j].ball.state!=='estimated')return j;return d.frames.findIndex(f=>f.ball.state!=='estimated');},nextBall:i=>{for(let j=index(i)+1;j<201;j++)if(d.frames[j].ball.state==='estimated')return j;return d.frames.findIndex(f=>f.ball.state==='estimated');}});
}
const api={create};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.PLPassageCombinedModel=api;
})(typeof window!=='undefined'?window:globalThis);

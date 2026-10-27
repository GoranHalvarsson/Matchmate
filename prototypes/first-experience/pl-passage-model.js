(function(root){
'use strict';
const need=(ok,message)=>{if(!ok)throw Error('Longer passage: '+message);};
const keys=(o,s)=>o&&typeof o==='object'&&!Array.isArray(o)&&Object.keys(o).sort().join(' ')===s.split(' ').sort().join(' ');
const finite=n=>typeof n==='number'&&Number.isFinite(n),hash=s=>/^[a-f0-9]{64}$/.test(s);
const normalize=s=>String(s).toLowerCase().replace(/[^a-z0-9 ]/g,' ').trim().replace(/\s+/g,' ');
const freeze=o=>{if(o&&typeof o==='object'){Object.values(o).forEach(freeze);Object.freeze(o);}return o;};
function create(input){
 need(keys(input,'version kind fixtureId source bounds policy quality samples'),'bundle allowlist');
 const d=JSON.parse(JSON.stringify(input));
 need(d.version===1&&d.kind==='estimated_consecutive_person_passage'&&d.fixtureId==='premier-league:2026-27:2026-09-18:brentford:chelsea','fixture/version');
 need(keys(d.source,'url videoSha256 researchInputSha256')&&d.source.url==='https://www.brentfordfc.com/en/video/0_2hoy7vw7?provider=stream'&&hash(d.source.videoSha256)&&hash(d.source.researchInputSha256),'source provenance');
 need(keys(d.bounds,'start end step')&&d.bounds.start===4950&&d.bounds.end===5017.2&&d.bounds.step===.2,'approved bounds');
 const expected={positions:'exact_sample_only',interpolation:false,hold:false,ball:null,identities:'anonymous_proposed_associations_with_two_bounded_reviewed_continuations',units:'normalized_percent_not_metres',fieldModel:[105,68],measuredDimensions:false,knownThen:false,reportedEvents:false};
 need(keys(d.policy,Object.keys(expected).join(' '))&&Object.entries(expected).every(([k,v])=>JSON.stringify(d.policy[k])===JSON.stringify(v)),'estimate/identity policy');
 const q=d.quality;need(keys(q,'wholeIntervalAccepted qualifiedSubsets nativeFramesProcessed humanDenseSamples humanDenseSpacing humanNativeDiagnostics allFramesHumanReviewed reviewedCameraGap reviewedIdentityBounds')&&q.wholeIntervalAccepted===false&&q.nativeFramesProcessed===1681&&q.humanDenseSamples===169&&q.humanDenseSpacing===.4&&q.humanNativeDiagnostics===57&&q.allFramesHumanReviewed===false&&JSON.stringify(q.qualifiedSubsets)==='[[4950,5011.6],[5012.6,5017.2]]'&&JSON.stringify(q.reviewedCameraGap)==='[5011.72,5012.56]'&&JSON.stringify(q.reviewedIdentityBounds)==='[5010,5017.2]','qualified subsets and review limits');
 need(Array.isArray(d.samples)&&d.samples.length===337,'337 bounded samples');
 const allIds=new Set();
 d.samples.forEach((s,i)=>{
  need(keys(s,'nativeIndex time pts camera gapReasons withheldCount markers ball')&&s.nativeIndex===i*5&&finite(s.time)&&Math.abs(s.time-(4950+i*.2))<1e-7&&s.pts===Math.round(s.time*12800),'native PTS/sample alignment');
  need(['estimated','gap'].includes(s.camera)&&Array.isArray(s.gapReasons)&&s.gapReasons.every(x=>typeof x==='string'&&/^[a-z0-9_:-]+$/.test(x))&&Number.isInteger(s.withheldCount)&&s.withheldCount>=0&&s.ball===null,'gap/ball support');
  need(Array.isArray(s.markers)&&s.markers.length<=30&&(s.camera!=='gap'||s.markers.length===0),'gap cannot contain positions');
  if(s.time>=5011.72&&s.time<=5012.56)need(s.camera==='gap','reviewed camera exclusion');
  const ids=new Set();s.markers.forEach(m=>{
   need(keys(m,'id x y association')&&typeof m.id==='string'&&/^(T\d+-S\d+|reviewed-[bc])$/.test(m.id)&&!ids.has(m.id)&&finite(m.x)&&finite(m.y)&&m.x>=0&&m.x<=100&&m.y>=0&&m.y<=100,'bounded anonymous foot estimates');ids.add(m.id);allIds.add(m.id);
   const reviewed=m.id.startsWith('reviewed-');need(m.association===(reviewed?'reviewed-continuation':'proposed-track')&&(!reviewed||(s.time>=5010&&s.time<=5017.2)),'reviewed join bounds');
  });
 });
 freeze(d);
 const labels=new Map([['reviewed-b','B'],['reviewed-c','C']]);let letter=0;
 function alpha(n){let s='';do{s=String.fromCharCode(65+n%26)+s;n=Math.floor(n/26)-1;}while(n>=0);return s;}
 for(const id of allIds){if(labels.has(id))continue;while(['B','C'].includes(alpha(letter)))letter++;labels.set(id,alpha(letter++));}
 const label=id=>labels.has(id)?'Track '+labels.get(id):'No track selected',tag=id=>labels.get(id)||'?';
 function exact(t){if(!finite(t))return null;const i=Math.round((t-4950)/.2);return i>=0&&i<337&&Math.abs(d.samples[i].time-t)<1e-7?d.samples[i]:null;}
 function view(t,selected){const s=exact(t),marker=s?.markers.find(p=>p.id===selected)||null;return {time:t,sample:s,markers:s?s.markers:[],selected:marker,gap:!s||s.camera==='gap',ball:null};}
 function index(n){return Math.max(0,Math.min(336,Number.isFinite(n)?Math.round(n):0));}
 function gap(i){for(let n=index(i)+1;n<337;n++)if(d.samples[n].camera==='gap')return n;return d.samples.findIndex(s=>s.camera==='gap');}
 function answer(t,id,question){
  const v=view(t,id),s=v.sample,k=normalize(question),name=label(id),base={time:t,selectedId:labels.has(id)?id:null};
  const text=(kind,value)=>({...base,kind,text:value});
  if(k==='where is the ball')return text('ball','Ball position is unknown. This passage contains no supported ball observations.');
  if(k==='where is this track')return text('position',v.selected?name+' has an estimated position in the '+(v.selected.x<33?'left third':v.selected.x>67?'right third':'middle third')+', toward the '+(v.selected.y<50?'far':'near')+' touchline at this exact video sample. It is an uncertain ground-plane foot proxy, not a measured location.':name+': position unknown at this exact sample. No previous or next position is carried here.');
  if(k==='what is visible here')return text('coverage',s&&!v.gap?s.markers.length+' generic person candidates have estimated positions at this exact sample. People may include officials or duplicate candidates; this is not a count of identified players. Team, role and ball position are unknown.':'Positions are withheld here because camera support is missing or unstable. Ball position is also unknown.');
  if(k==='why is there a gap')return text('gap',v.gap?'The camera estimate is missing or failed a check at this sample. All person positions are withheld; no motion is filled across the gap.':'The camera estimate passed the applicable checks at this sample. Individual missing detections, overlaps, clipped feet and ambiguous poses can still be withheld. This is not complete player coverage.');
  if(k==='what supports this')return text('evidence','Existing official video, source '+(finite(t)?t.toFixed(2):'unknown')+' seconds'+(s?' / native PTS '+s.pts:'; no exact exported sample')+'. Fixed football calibration and person detector/tracker; 169 stills at 0.4-second spacing plus 57 native diagnostics reviewed, not every frame. Only two final-action continuations were manually joined from 5010 onward; other associations remain proposed. Pitch dimensions are an unverified 105 × 68 model.');
  return text('unsupported','I can explain the selected track, visible candidates, gaps, ball coverage or sources at this exact sample. Names, teams, events, scores, future positions and movement metrics are not supported by this passage.');
 }
 return Object.freeze({start:4950,end:5017.2,step:.2,samples:d.samples,source:d.source,quality:d.quality,policy:d.policy,label,tag,view,index,gap,answer,has:id=>labels.has(id),at:i=>d.samples[index(i)]});
}
const api={create};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.PLPassageModel=api;
})(typeof window!=='undefined'?window:globalThis);

// Offline projection. Normal builds use only the pinned JSON, never ignored research.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const base=__dirname,root=path.resolve(base,'../..'),name='brentford-chelsea-2026-09-18-timeline.json',receiptName='brentford-chelsea-2026-09-18-timeline-receipt.json';
const hash=bytes=>crypto.createHash('sha256').update(bytes).digest('hex'),read=p=>fs.readFileSync(p),json=p=>JSON.parse(read(p)),record=p=>{const b=read(p);return {sha256:hash(b),bytes:b.length};};
const FINAL_BUNDLE='c02321106f4e5b444ff7c0ba891fcca07ff40c7a945882c863861c281c6e0908';
// Narrow excerpts remove retrospective report-derived details, not media uncertainty.
const excerpts={
 'E-09':'Contact types and exact goal time remain unverified. Shirt-number observations apply only to their reviewed frames.',
 'E-10':'Approximate media interval and visible broadcast graphics do not establish an exact entry time.',
 'E-12b':'Speech alone does not establish all four identities or exact entry times.',
 'E-13':'Pass versus parry/contact type and exact line crossing remain unclear.',
 'E-14':'Sampled card view does not positively establish recipient/issuance or cause.',
 'E-14b':'Brentford entrant not identified by face in sampled images. Graphic airtime and formal substitution time differ.',
 'E-15':'Throw-in, not corner. Scorer jersey/contact not readable in reviewed samples. O.DANGO11 and DIOUF25 are visible celebration teammates, not automatically scorers.'
};
// These cited observations describe support beyond their headline interval/point.
const scopeOverrides={
 'V-PASS-01':{end:13,locator:'reviewed_passages/V-PASS-01/observation: clearly in play by 13'},
 'V-PASS-02':{end:2805,locator:'reviewed_passages/V-PASS-02/observation: lower third at 2800 and 2805'},
 'V-PER-07':{end:3735,locator:'visible_people/V-PER-07/identity_scope: shirt 19 separately clear in 3733–3735'}
};
// Mechanical readability edits only; no identity, time or factual correction.
const copyEdits={
 'E-03':{uncertainty:[['at624','at 624'],['replay626–638','replay 626–638'],['KELLEHER1','KELLEHER 1'],['at622','at 622']]},
 'E-05':{text:[['by2187–2188','by 2187–2188']],uncertainty:[['THIAGO9/COLWILL6','THIAGO 9 / COLWILL 6'],['at2175','at 2175']]},
 'E-08':{text:[['Kayode33/Yarmoliuk6','Kayode 33 / Yarmoliuk 6'],['Hickey2/Sangaré18','Hickey 2 / Sangaré 18']]},
 'E-10':{text:[['Welbeck18/Fofana3','Welbeck 18 / Fofana 3'],['Quenda23/Acheampong34','Quenda 23 / Acheampong 34']]},
 'E-13':{text:[['shirt7','shirt 7'],['shorts9','shorts 9'],['locator5015.52–5017.52','locator 5015.52–5017.52'],['about82:02–82:04','about 82:02–82:04']]},
 'E-14':{text:[['at5145','at 5145']]},
 'E-14b':{text:[['Neto7/Barco4','Neto 7 / Barco 4'],['Estêvão41/Nicoll-Jazuli32','Estêvão 41 / Nicoll-Jazuli 32']]},
 'E-15':{text:[['shirt7','shirt 7'],['around5728–5732','around 5728–5732'],['readable3-0','readable 3–0'],['at5738/94:05','at 5738 / 94:05']],uncertainty:[['O.DANGO11','O.DANGO 11'],['DIOUF25','DIOUF 25']]}
};
function readable(id,field,text){for(const [from,to]of copyEdits[id]?.[field]||[]){if(!text.includes(from))throw Error('Source wording drift: '+id+'/'+field+'/'+from);text=text.replace(from,to);}return text;}
function project(sourceDir){
 const ledgerPath=path.join(sourceDir,'event-ledger.json'),asrPath=path.join(sourceDir,'asr-complete.json'),visualPath=path.join(sourceDir,'visual-observations.json'),bundlePath=path.join(sourceDir,'review-bundle.json');
 if(hash(read(bundlePath))!==FINAL_BUNDLE)throw Error('Final reviewed bundle byte hash mismatch');
 const ledger=json(ledgerPath),asr=json(asrPath),visual=json(visualPath),bundle=json(bundlePath);
 // Living wiki pages can gain later entries. Research inputs themselves stay immutable.
 for(const [file,r]of Object.entries(bundle.files).filter(([file])=>file.startsWith('output/full-match-review/'))){const b=read(path.resolve(sourceDir,file.slice('output/full-match-review/'.length)));if(hash(b)!==r.sha256||b.length!==r.bytes)throw Error('Reviewed source mismatch: '+file);}
 if(ledger.asr_manifest_sha256!==hash(read(asrPath))||ledger.visual_receipt_sha256!==hash(read(visualPath)))throw Error('Ledger dependency hash mismatch');
 const segments=new Map(asr.segments.map(s=>[s.id,s])),visuals=new Map(['clock_anchors','visible_people','sampled_event_observations','reviewed_passages','broadcast_attributions'].flatMap(k=>(visual[k]||[]).map(v=>[v.id,{...v,collection:k}])));
 const audit=[];
 const passages=ledger.episodes.map(e=>{
  const missing=[],speech=[];
  for(const [role,refs]of [['asr',e.asr?.segment_refs||[]],['context',e.asr_context_refs||[]]])for(const id of refs){const s=segments.get(id);if(!s||!Number.isFinite(s.start)||!Number.isFinite(s.end)||s.end<s.start){missing.push('Unresolved '+role+' support '+id);continue;}speech.push({id,start:s.start,end:s.end,role});}
  const pictures=(e.visual_refs||[]).map(id=>{const v=visuals.get(id);if(!v){missing.push('Unresolved visual support '+id);return null;}const span=v.media_interval||[v.media_pts,v.media_pts],override=scopeOverrides[id];let start=span[0],end=Math.max(span[1],override?.end??span[1]);if(!Number.isFinite(start)||!Number.isFinite(end)||end<start){missing.push('Unresolved visual timing '+id);return null;}return {id,start,end,scope:override?.locator||v.collection+'/'+id+'/'+(v.media_interval?'media_interval':'media_pts')};}).filter(Boolean);
  const ends=[e.media_interval[1],...speech.map(s=>s.end),...pictures.map(v=>v.end)],availableAt=missing.length?null:Math.max(...ends);
  if(availableAt!==null&&availableAt>ledger.media_duration_seconds)missing.push('Supporting evidence exceeds source endpoint');
  const badges=[];if(pictures.length)badges.push('sampled_visual');if(speech.some(s=>s.role==='asr'))badges.push('asr_candidate');if(['E-03','E-05','E-12'].includes(e.id))badges.push('replay');if(['E-04','E-14b'].includes(e.id))badges.push('historical_discussion');if(['E-08','E-10','E-14b'].includes(e.id))badges.push('broadcast_attribution');
  const p={id:e.id,title:e.title,text:readable(e.id,'text',e.paraphrase),uncertainty:readable(e.id,'uncertainty',excerpts[e.id]||e.uncertainty),reviewStatus:e.review_status,badges,mediaInterval:[...e.media_interval],support:{asr:speech,visual:pictures},availableAt:missing.length?null:availableAt,withheldReason:missing.length?missing.join('; '):null};
  audit.push({id:e.id,excludedFields:['asr.claim','asr.raw_name_forms',...(e.report_refs?['report_refs']:[])],uncertaintyExcerpted:!!excerpts[e.id],uncertaintySource:'episodes/'+e.id+'/uncertainty',reason:e.id==='E-09'?'Remove report-derived details and express frame-scoped shirt observations in plain product language.':excerpts[e.id]?'Remove report-derived names/minutes and retain media/ASR limits.':null,copyEdits:copyEdits[e.id]||null,visualScopeOverrides:pictures.filter(v=>scopeOverrides[v.id]).map(v=>({id:v.id,scope:v.scope,end:v.end})),cutoff:{intervalEnd:e.media_interval[1],asrAndContextEnd:speech.length?Math.max(...speech.map(s=>s.end)):null,visualEnd:pictures.length?Math.max(...pictures.map(s=>s.end)):null,availableAt:p.availableAt,withheldReason:p.withheldReason}});
  return p;
 });
 const original=json(path.join(base,'brentford-chelsea-2026-09-18-first-goal.json'));
 const artifact={version:1,kind:'retrospective_candidate_passage_timeline',fixture:{id:original.fixture.id,officialMatchId:original.fixture.officialMatchId,date:original.fixture.date,home:'Brentford',away:'Chelsea'},source:{url:ledger.source_page,ledger:record(ledgerPath),asr:record(asrPath),visual:record(visualPath),reviewedBundle:record(bundlePath),audioSha256:asr.source_sha256,videoSha256:visual.source_sha256},bounds:{start:0,end:ledger.media_duration_seconds},policy:{availability:'latest_support_end',reportsIncluded:false,liveKnownThen:false,completeIncidentFeed:false,movement:false},passages};
 require('../../prototypes/first-experience/pl-timeline-model.js').create(artifact);
 const bytes=Buffer.from(JSON.stringify(artifact,null,2)+'\n');
 return {artifact,bytes,receipt:{version:1,method:'Explicit allowlist of reviewed candidate episodes; no raw commentary, reports, media paths or report facts. Complete passage released at latest supporting interval, ASR/context or scoped visual end.',artifact:{file:name,sha256:hash(bytes),bytes:bytes.length},source:artifact.source,excludedCollections:['published_report_rows','report_conflicts','visual_observations','raw ASR words/text','source media/frame paths'],passages:audit}};
}
function loadPinned(){const bytes=read(path.join(base,name)),artifact=JSON.parse(bytes),receipt=json(path.join(base,receiptName));if(hash(bytes)!==receipt.artifact.sha256||bytes.length!==receipt.artifact.bytes)throw Error('Pinned timeline receipt mismatch');require('../../prototypes/first-experience/pl-timeline-model.js').create(artifact);return {artifact,receipt,bytes};}
if(require.main===module){
 const args=process.argv.slice(2),source=args.indexOf('--source');
 if(source>=0){const result=project(path.resolve(args[source+1]));if(args.includes('--check')){if(!read(path.join(base,name)).equals(result.bytes)||JSON.stringify(json(path.join(base,receiptName)))!==JSON.stringify(result.receipt))throw Error('Projection does not reproduce pinned artifacts');console.log('Pinned timeline and receipt reproduce exactly from reviewed source.');}else{fs.writeFileSync(path.join(base,name),result.bytes);fs.writeFileSync(path.join(base,receiptName),JSON.stringify(result.receipt,null,2)+'\n');console.log('Projected '+result.artifact.passages.length+' passages.');}}
 else{const {artifact}=loadPinned();console.log('Pinned timeline receipt and model valid: '+artifact.passages.length+' passages; no research input required.');}
}
module.exports={project,loadPinned,scopeOverrides,excerpts,copyEdits,readable};

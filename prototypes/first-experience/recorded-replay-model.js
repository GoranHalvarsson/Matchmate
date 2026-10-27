(function(root){
'use strict';
const quality=flag=>flag===true?'detected':flag===false?'estimated':'unknown';
const qualityText=state=>({detected:'Source detected',estimated:'Source estimated',unknown:'Position quality unknown',missing:'Position unavailable'}[state]);
const finite=value=>typeof value==='number'&&Number.isFinite(value);
const valid=p=>!!p&&finite(p.x)&&finite(p.y);
function clock(time){if(!Number.isFinite(time))return 'Clock unavailable';const tenths=Math.floor(time*10);return `${String(Math.floor(tenths/600)).padStart(2,'0')}:${String(Math.floor(tenths/10)%60).padStart(2,'0')}.${tenths%10}`;}
function create(sample){
 const {start,end,cadence}=sample;
 const slotEnd=time=>Number((time+cadence).toFixed(8));
 const frameBySlot=new Map(sample.frames.map(frame=>[Math.round((frame.clock-start)/cadence),frame]));
 const roster=sample.players.map(p=>({...p,team:p.teamId===sample.home.id?'home':'away',teamName:p.teamId===sample.home.id?sample.home.name:sample.away.name}));
 function observation(p){const present=valid(p);return {...(p||{}),available:present,quality:present?quality(p.isDetected):'missing',missingReason:present?null:(p?.missingReason||'entity-not-in-frame'),outOfBounds:present&&(Math.abs(p.x)>sample.pitch.length/2||Math.abs(p.y)>sample.pitch.width/2)};}
 function at(time){
  // Binary search avoids rounding a cursor forward to a future tenth.
  let low=0,high=sample.frames.length-1,frame=null;
  while(low<=high){const mid=(low+high)>>1,candidate=sample.frames[mid];if(candidate.clock<=time){frame=candidate;low=mid+1;}else high=mid-1;}
  if(!Number.isFinite(time)||time<start||time>end||!frame||time>=slotEnd(frame.clock))frame=null;
  const source=new Map((frame?.players||[]).map(p=>[p.playerId,p]));
  const players=roster.map(p=>({...p,...observation(source.get(p.id))}));
  const count=players.filter(p=>p.available).length;
  const frameState=!frame?'missing-frame':!frame.players.length?'empty-positions':!count?'no-valid-positions':count<roster.length?'partial-positions':'available-positions';
  return {time,frame,players,ball:observation(frame?.ball),count,fullGap:count===0,frameState};
 }
 // Coverage is computed from expected source slots, not inferred on-field presence.
 const slots=Array.from({length:Math.round((end-start)/cadence)+1},(_,index)=>{
  const time=Number((start+index*cadence).toFixed(8)),frame=frameBySlot.get(index);
  const count=(frame?.players||[]).filter(valid).length;
  return {time,frameState:!frame?'missing-frame':!frame.players.length?'empty-positions':!count?'no-valid-positions':count<roster.length?'partial-positions':'available-positions',fullGap:count===0};
 });
 function groups(key){const result=[];for(const slot of slots){const last=result[result.length-1],value=slot[key];if(last&&last.value===value){last.end=slot.time;last.slots++;}else result.push({value,start:slot.time,end:slot.time,slots:1});}return result;}
 const coverage=groups('frameState');
 const gaps=groups('fullGap').filter(g=>g.value).map((g,i)=>({...g,id:'gap-'+i}));
 const gapAt=time=>gaps.find(g=>time>=g.start&&time<slotEnd(g.end)&&time<=end)||null;
 function seek(time){return {time:Math.max(start,Math.min(end,Number.isFinite(Number(time))?Number(time):start)),playing:false,continuedGap:null};}
 function play(state){return {...state,playing:state.time<end&&!at(state.time).fullGap,continuedGap:null};}
 function continueGap(state){const gap=gapAt(state.time);return gap&&state.time<end?{...state,playing:true,continuedGap:gap.id}:{...state,playing:false,continuedGap:null};}
 function advance(state,elapsed){
  if(!state.playing)return state;
  const target=Math.min(end,state.time+Math.max(0,Number(elapsed)||0));
  const current=gapAt(state.time);
  if(current&&state.continuedGap!==current.id)return {...state,playing:false,continuedGap:null};
  const crossed=gaps.find(g=>g.start>state.time&&g.start<=target&&g.id!==state.continuedGap);
  if(crossed)return {time:crossed.start,playing:false,continuedGap:null};
  return {time:target,playing:target<end,continuedGap:gapAt(target)?.id===state.continuedGap?state.continuedGap:null};
 }
 function adjacent(time,direction){const gap=gapAt(time);if(!gap)return null;const candidates=slots.filter(s=>!s.fullGap&&(direction<0?s.time<gap.start:s.time>gap.end));return (direction<0?candidates.at(-1):candidates[0])?.time??null;}
 function lastKnown(time,id){for(let i=sample.frames.length-1;i>=0;i--){const f=sample.frames[i];if(f.clock>time)continue;const p=f.players.find(p=>p.playerId===id);if(valid(p))return {time:f.clock,...p};}return null;}
 function explain(time,id){
  const frame=at(time),p=frame.players.find(p=>p.id===id);
  if(!p)return 'Choose a player from this sample’s source roster.';
  const identity=`At first-half ${clock(time)}, ${p.name} (${p.teamName}${p.number==null?'':', shirt '+p.number})`;
  if(!p.available)return `${identity}: position unavailable. The source roster remains available; absence of a position does not establish a substitution. Movement, speed, distance and tactics are unavailable at this cursor.\n\nRecorded tracking replay · Retrospective, source-derived. Completed possession annotations are separate; score is unavailable.`;
  return `${identity}: ${qualityText(p.quality).toLowerCase()}. Source sample ${clock(frame.frame.clock)} reports x ${p.x} m, y ${p.y} m from pitch centre.${p.outOfBounds?' This coordinate is outside the nominal pitch boundary; it is preserved without correction.':''}\n\nThis describes the selected source sample only. It does not establish intent, tactical role or what happens next. Speed, distance and score are not supplied; completed possession annotations are separate. Recorded tracking replay · Retrospective, source-derived.`;
 }
 // The source axes are retained. Display orientation is explicit, never a data correction.
 function project(p){return {left:(p.x+sample.pitch.length/2+6)/(sample.pitch.length+12)*100,top:(sample.pitch.width/2-p.y+6)/(sample.pitch.width+12)*100};}
 return {sample,roster,at,coverage,gaps,gapAt,seek,play,continueGap,advance,adjacent,lastKnown,explain,project,clock,qualityText};
}
const api={create,quality,qualityText,clock};if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.RecordedReplayModel=api;
})(typeof window!=='undefined'?window:globalThis);

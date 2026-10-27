/* Synthetic tracking example. Coordinates are metres on a 105 x 68 pitch. */
(function (root) {
  const roles = ['Goalkeeper','Left back','Centre back','Centre back','Right back','Midfielder','Winger','Midfielder','Forward','Midfielder','Winger'];
  const base = [[6,34],[27,10],[22,26],[22,43],[27,58],[44,23],[57,12],[43,43],[68,34],[55,36],[56,57]];
  const players = ['H','R'].flatMap(team => base.map(([x,y],i) => ({id:team+(i+1),number:i+1,team,name:(team==='H'?'Harbour':'Riverside')+' '+(i+1),role:roles[i],x:team==='H'?x:105-x,y:team==='H'?y:68-y})));
  const events = [{time:0,label:'Possession',text:'Harbour 6 has the ball in midfield.'},{time:6,label:'Pass',text:'Harbour 6 releases a pass towards Harbour 7.'},{time:12,label:'Receive',text:'Harbour 7 receives the ball on the near flank.'},{time:17,label:'Return pass',text:'Harbour 7 plays the ball back towards Harbour 8.'},{time:20,label:'Receive',text:'Harbour 8 receives the return pass.'}];
  const lerp=(a,b,t)=>a+(b-a)*t;
  function frame(time) {
    time=Math.max(0,Math.min(20,Number(time)||0));
    const positions=players.map(p=>({...p,x:p.x+(p.team==='H'?time*.35:-time*.12),y:p.y}));
    const winger=positions.find(p=>p.id==='H7');winger.x=57+time*.7;winger.y=12+time*.35;
    const receiver=positions.find(p=>p.id==='H8');receiver.x=43+time*.5;receiver.y=43-time*.6;
    const source=positions.find(p=>p.id==='H6');
    let ball;
    if(time<6)ball={x:source.x+1,y:source.y};
    else if(time<12){const t=(time-6)/6;ball={x:lerp(47.1,66.4,t),y:lerp(23,16.2,t)};}
    else if(time<17)ball={x:winger.x+1,y:winger.y};
    else {const t=(time-17)/3;ball={x:lerp(69.9,54,t),y:lerp(17.95,31,t)};}
    return {time,players:positions,ball,events:events.filter(event=>event.time<=time)};
  }
  function clock(time){const seconds=40+Math.floor(time);return `${64+Math.floor(seconds/60)}:${String(seconds%60).padStart(2,'0')}`;}
  function explain(time,id){const f=frame(time);const player=f.players.find(p=>p.id===id)||f.players.find(p=>p.id==='H7');const nearest=f.players.filter(p=>p.team!==player.team).map(p=>({p,d:Math.hypot(p.x-player.x,p.y-player.y)})).sort((a,b)=>a.d-b.d)[0];
    return `At ${clock(f.time)}, ${player.name} is a ${player.role.toLowerCase()}. The nearest opponent is ${nearest.p.name}, about ${nearest.d.toFixed(1)} metres away in this authored frame.\n\n${f.events[f.events.length-1].text}\n\nThe spacing is calculated from this synthetic frame. It does not establish intent or what happens next.`;
  }
  // Synthetic match totals at the clip start, not reconstructed match history.
  const baseline=Object.fromEntries(players.map(p=>[p.id,{distance:p.number===1?2100:6100+p.number*83+(p.team==='R'?120:0),completed:12+p.number*2,missed:2+p.number%5,tackles:p.number%4,interceptions:p.number%3}]));
  function stats(time){
    const f=frame(time),t=f.time;
    return f.players.map(p=>{
      const start=players.find(q=>q.id===p.id),b=baseline[p.id];
      const metres=Math.hypot(p.x-start.x,p.y-start.y);
      const rate=p.id==='H7'?Math.hypot(.7,.35):p.id==='H8'?Math.hypot(.5,.6):p.team==='H'?.35:.12;
      const complete=(p.id==='H6'&&t>=12)||(p.id==='H7'&&t>=20)?1:0;
      const pending=(p.id==='H6'&&t>=6&&t<12)||(p.id==='H7'&&t>=17&&t<20)?1:0;
      return {...p,...b,distance:b.distance+metres,speed:rate*3.6,completed:b.completed+complete,pending};
    });
  }
  function ballStats(time){
    const t=frame(time).time,points=[0,6,12,17,20].filter(x=>x<t).concat(t);
    let distance=0;
    for(let i=1;i<points.length;i++){const a=frame(points[i-1]).ball,b=frame(points[i]).ball;distance+=Math.hypot(b.x-a.x,b.y-a.y);}
    const segment=t<6?[0,6]:t<12?[6,12]:t<17?[12,17]:[17,20];
    const a=frame(segment[0]).ball,b=frame(segment[1]).ball;
    const trail=[];for(let x=Math.max(0,t-4);x<t;x+=.2)trail.push(frame(x).ball);trail.push(frame(t).ball);
    return {distance,speed:Math.hypot(b.x-a.x,b.y-a.y)/(segment[1]-segment[0])*3.6,passes:(t>=6?1:0)+(t>=17?1:0),trail};
  }
  const api={players,events,frame,clock,explain,stats,ballStats};
  if(typeof module!=='undefined'&&module.exports)module.exports=api;else root.ReplayModel=api;
})(typeof window!=='undefined'?window:globalThis);

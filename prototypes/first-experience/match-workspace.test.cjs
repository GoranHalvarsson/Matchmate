const test=require('node:test'),assert=require('node:assert/strict'),fs=require('node:fs'),vm=require('node:vm');
test('shared renderer boots before demo controllers and preserves guide, match replay, clubs and stadium hooks',()=>{
 const nodes=new Map(),listeners=[];const node=selector=>{if(!nodes.has(selector))nodes.set(selector,{innerHTML:'',textContent:'',value:'',dataset:{},children:[],style:{},classList:{toggle(){}},setAttribute(){},addEventListener(){},querySelector:s=>node(selector+' '+s),querySelectorAll:()=>[],insertAdjacentHTML(pos,html){this.innerHTML+=html;},after(){},before(){},focus(){},scrollIntoView(){},remove(){},getBoundingClientRect:()=>({top:0})});return nodes.get(selector);};
 const document={querySelector:node,querySelectorAll:()=>[],createElement:()=>node('#pitch-replay'),addEventListener:(type,fn)=>listeners.push({type,fn})};
 const window={addEventListener(){},ReplayModel:require('./replay-model.js')};const sandbox={window,document,innerWidth:1440,matchMedia:()=>({matches:true}),setInterval:()=>1,clearInterval(){},console};vm.createContext(sandbox);
 for(const file of ['match-workspace.js','app.js','experience.js','replay.js']){vm.runInContext(fs.readFileSync(require.resolve('./'+file),'utf8'),sandbox);if(file==='match-workspace.js')sandbox.MatchWorkspace=window.MatchWorkspace;}
 assert.match(node('#demo-workspace').innerHTML,/id="composer"/);assert.match(node('#historical-match').innerHTML,/id="historical-composer"/);assert.match(node('#speaker-name').textContent,/Your Matchmate/);
 vm.runInContext("open('match','live')",sandbox);assert.match(node('#pitch-replay').innerHTML,/data-player="H7"/);assert.match(node('#pitch-replay').innerHTML,/id="replay-range"/);assert.equal(node('#speaker-name').textContent,'The match');
 vm.runInContext("section='clubs';catalog();open('harbour')",sandbox);assert.match(node('#catalog').innerHTML,/data-entity="harbour"/);assert.equal(node('#speaker-name').textContent,'Harbour FC');
 vm.runInContext("section='stadiums';catalog();open('lantern')",sandbox);assert.match(node('#catalog').innerHTML,/data-entity="lantern"/);assert.equal(node('#speaker-name').textContent,'The Lantern');assert.equal(node('#pitch-replay').innerHTML,'');
 vm.runInContext("open('guide')",sandbox);assert.equal(node('#speaker-name').textContent,'Your Matchmate');assert.ok(listeners.length);
});

test('replay CSS control and readout contracts apply equally to both isolated ID sets',()=>{
 const css=fs.readFileSync(require.resolve('./style.css'),'utf8');
 const rules={'replay-range':['width:100%','min-height:35px'],'replay-player':['min-height:44px','max-width:155px'],'replay-play':['background:var(--ink)','min-width:70px'],'replay-event':['font-size:12px','margin:8px 0'],'replay-ball':['position:absolute'],'ball-halo':['position:absolute'],'ball-readout':['font-size:10px']};
 for(const [id,properties] of Object.entries(rules)){const selector=':is(#'+id+',#historical-'+id+')',start=css.indexOf(selector);assert.ok(start>=0,selector);const body=css.slice(start).split('{')[1].split('}')[0];for(const property of properties)assert.ok(body.includes(property),id+' '+property);}
 const replayCSS=css.slice(css.indexOf('.replay{'),css.indexOf('/* A separate, generously'));
 const selectors=[...replayCSS.matchAll(/([^{}]+)\{/g)].map(m=>m[1]).join(' ');const audited=[...selectors.matchAll(/#([a-z-]+)/g)].map(m=>m[1]).filter(id=>!id.startsWith('historical-'));
 for(const id of audited)assert.ok(Object.hasOwn(rules,id),'unpaired replay CSS hook '+id);
});

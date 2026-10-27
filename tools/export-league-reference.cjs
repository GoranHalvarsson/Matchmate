'use strict';
// Normalize existing POC reference sources once. No preview HTML, result reconstruction or movement generation.
const fs=require('node:fs'),path=require('node:path'),crypto=require('node:crypto');
const root=path.resolve(__dirname,'..'),poc=path.join(root,'prototypes/first-experience');
const league=require(path.join(poc,'league-model.js')),table=require(path.join(poc,'season-table-model.js'));
const folder=path.resolve(process.argv[2]||path.join(root,'.local/league-reference-v1')); fs.mkdirSync(folder,{recursive:true});
const hash=b=>crypto.createHash('sha256').update(b).digest('hex');
const read=p=>fs.readFileSync(path.join(poc,p));
const sourceManifest=JSON.parse(read('assets/seasons/manifest.json')),crestManifest=JSON.parse(read('assets/crests/manifest.json'));
const sources=[];
function source(id,relative,license){const b=read(relative); sources.push({sourceId:id,path:'prototypes/first-experience/'+relative,sha256:hash(b),license});return b;}
source('league-model','league-model.js','Existing project mapping; crest artwork rights remain separate.');
source('season-table-model','season-table-model.js','Existing project results-only calculation.');
source('results-manifest','assets/seasons/manifest.json',sourceManifest.license);
source('crest-manifest','assets/crests/manifest.json',crestManifest.rights);
const license=source('results-license','assets/seasons/CC0-1.0.txt',sourceManifest.license).toString('utf8');
const parity=[];
const seasons=league.filterSeasons().map(label=>{
 const key=label.replace('/','-'),pin=sourceManifest.seasons[label];
 const bytes=source('season-'+key,'assets/seasons/'+pin.file,sourceManifest.license);
 if(hash(bytes)!==pin.sha256)throw Error('Pinned source SHA mismatch '+label);
 const clubs=league.members(label).map(c=>({id:c.id,name:c.name,initials:c.initials,town:c.town,ground:c.ground,x:c.x,y:c.y,inset:c.inset,crestPath:league.crestFor(c.id,label)}));
 const model=table.createSeason(JSON.parse(bytes),clubs);
 for(let index=0;index<=model.dates.length;index++){
  const s=table.snapshot(model,index);
  parity.push({seasonKey:key,index,date:s.date,completed:s.completed,rows:s.rows,results:s.results,
    facts:clubs.map(c=>({clubId:c.id,...table.clubFacts(model,index,c.id)}))});
 }
 return {seasonKey:key,label,clubs,matches:model.matches.map(m=>({matchKey:m.id,date:m.date,home:m.home,away:m.away,homeGoals:m.hg,awayGoals:m.ag,round:m.round}))};
});
const self=fs.readFileSync(__filename);sources.push({sourceId:'league-exporter',path:'tools/export-league-reference.cjs',sha256:hash(self),license:'Existing project source adapter; no additional data licence inferred.'});
const bundle={schemaVersion:1,version:'poc-league-v1',timing:'Retrospective corrected end of played date; not known-then, intraday or live.',
 calculation:sourceManifest.adjustments+' '+sourceManifest.rule_sources.rules,sources,resultsLicenseText:license,resultsSourceMetadata:sourceManifest,crestSourceMetadata:crestManifest,seasons};
function immutable(file,value,cap){const bytes=Buffer.from(JSON.stringify(value));if(bytes.length>cap)throw Error('Export cap exceeded '+file);const dest=path.join(folder,file);
 if(fs.existsSync(dest)){if(!fs.readFileSync(dest).equals(bytes))throw Error('Immutable export conflict; use another directory: '+dest);}else fs.writeFileSync(dest,bytes,{flag:'wx'});return{file:dest,bytes:bytes.length,sha256:hash(bytes)};}
const output=immutable('league-reference.json',bundle,2000000);
const expected=immutable('parity.json',parity,10000000);
console.log(JSON.stringify({bundle:output,parity:expected,seasons:seasons.map(s=>({key:s.seasonKey,clubs:s.clubs.length,matches:s.matches.length})),snapshots:parity.length}));

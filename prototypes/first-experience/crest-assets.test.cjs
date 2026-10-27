const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const {buildPreview} = require('./build-preview.cjs');
const {assertSafeSvg,loadAssets} = require('./crest-assets.cjs');
const {members,crestFor} = require('./league-model.js');
const manifest = require('./assets/crests/manifest.json');
test('period crest mapping matches documented coverage and keeps redesign variants distinct',()=>{
  for (const season of ['2015/16','2016/17']) for (const club of members(season)) {
    const record=manifest.assets.find(a=>a.club_id===club.id && a.seasons.includes(season));
    assert.equal(crestFor(club.id,season),record?'assets/crests/'+record.file:null);
    assert.equal(crestFor(club.id,season,true),null,'failed image must fall back to initials');
    assert.ok(record,'all40club-season memberships must have period artwork');assert.equal(manifest.assets.filter(a=>a.club_id===club.id&&a.seasons.includes(season)).length,1,'unique period mapping');
  }
  assert.equal(crestFor('west-ham','2015/16'),'assets/crests/west-ham-2015.png');assert.notEqual(crestFor('west-ham','2015/16'),crestFor('west-ham','2016/17'));
  assert.equal(crestFor('manchester-city','2015/16'),'assets/crests/manchester-city-2015.png');assert.notEqual(crestFor('manchester-city','2015/16'),crestFor('manchester-city','2016/17'));
  assert.ok(crestFor('west-ham','2016/17'));
  assert.ok(crestFor('manchester-city','2016/17'));
  assert.equal(crestFor('newcastle','2016/17'),null);
});
test('local crest files have valid signatures, safe SVG and provenance',()=>{
  assert.equal(loadAssets().length,25);
  for(const a of manifest.assets){assert.match(a.source_page,/^https:\/\//);assert.ok(a.history_sources.length);assert.ok(a.rights_status);assert.ok(a.historical_status);}
  for(const attack of ['<svg><script>alert(1)</script></svg>','<svg onload="bad()"/>','<svg><image href="https://remote.test/x"/></svg>','<svg><path fill="url(https://remote.test/x)"/></svg>','<!DOCTYPE svg SYSTEM "https://remote.test/x"><svg/>','<svg><style>@import "https://remote.test/x";</style></svg>']) assert.throws(()=>assertSafeSvg(attack));
  assert.equal(assertSafeSvg('<svg><defs><clipPath id="x"><path/></clipPath></defs><g clip-path="url(#x)"><path/></g></svg>'),true);
});
test('portable preview embeds exact local crest bytes without runtime asset requests',()=>{
  const html=fs.readFileSync(path.join(__dirname,'preview.html'),'utf8');
  assert.equal(html,buildPreview().html,'portable artifact must match the current source build');
  for(const asset of loadAssets()){
    assert.ok(html.includes(asset.dataUrl),asset.path+' must be embedded');
    assert.ok(!html.includes(asset.path),asset.path+' must not require a local file in portable preview');
  }
});

test('all 25 assets retain exact source bytes and PNG dimensions with complete 40-membership coverage',()=>{
 let coverage=0;for(const season of ['2015/16','2016/17'])for(const club of members(season)){assert.ok(crestFor(club.id,season));coverage++;}assert.equal(coverage,40);assert.equal(new Set(manifest.assets.map(a=>a.file)).size,25);
 for(const a of manifest.assets){const bytes=fs.readFileSync(path.join(__dirname,'assets/crests',a.file));assert.equal(bytes.length,a.bytes);assert.equal(require('node:crypto').createHash('sha256').update(bytes).digest('hex'),a.sha256);assert.ok(bytes.length>100&&bytes.length<2000000);if(a.file.endsWith('.png')){assert.equal(bytes.toString('ascii',12,16),'IHDR');assert.equal(bytes.readUInt32BE(16),a.width);assert.equal(bytes.readUInt32BE(20),a.height);assert.ok(a.width>=32&&a.width<=2048&&a.height>=32&&a.height<=2048);assert.equal(bytes.toString('ascii',bytes.length-8,bytes.length-4),'IEND');}}
 assert.match(manifest.assets.find(a=>a.file==='west-ham-2015.png').historical_status,/commemorative/);assert.match(manifest.purpose,/exception|except/);
});

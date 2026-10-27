'use strict';
// Operator/build-side sampling of the completed pure model. No new movement algorithm; no Node in service images.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const root = path.resolve(__dirname, '..');
const officialPath = path.join(root, 'data/official-match-records/brentford-chelsea-2026-09-18.json');
const authoredPath = path.join(root, 'data/reconstructed-match-illustrations/brentford-chelsea-2026-09-18.json');
const modelPath = path.join(root, 'prototypes/first-experience/pl-reconstructed-model.js');
const official = JSON.parse(fs.readFileSync(officialPath)), authored = JSON.parse(fs.readFileSync(authoredPath));
const model = require(modelPath).create(authored, official);
const windows = [[0, 3000], [2819000, 2822000], [3778000, 3782000], [4258000, 4262000]];
const frames = windows.flatMap(([start, end]) => Array.from({length:(end-start)/100+1}, (_, i) => model.frame((start+i*100)/1000)));
const hashes = Object.fromEntries([officialPath, authoredPath, modelPath, __filename].map(p => [path.relative(root, p).replaceAll('\\','/'), crypto.createHash('sha256').update(fs.readFileSync(p)).digest('hex')]));
const output = {exportVersion:1, sampling:'Existing model sampled every 100ms only inside declared tiny windows; no interpolation outside them.', windows, hashes,
  people: official.players, events: official.events.filter(e => ['goal-1','sub-5'].includes(e.id)), sources:official.sources, frames};
const destination = path.resolve(process.argv[2] || path.join(root, '.local/shared-inputs/brentford-slice.json'));
fs.mkdirSync(path.dirname(destination), {recursive:true}); fs.writeFileSync(destination, JSON.stringify(output));
console.log(`Exported ${frames.length} existing generated frames / ${official.players.length} roster people / ${output.events.length} selected reported events to ${destination}`);

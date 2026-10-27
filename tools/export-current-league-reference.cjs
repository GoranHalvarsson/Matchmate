'use strict';
// Compose the immutable historical bundle with one pinned community results snapshot.
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const root = path.resolve(__dirname, '..');
const hash = bytes => crypto.createHash('sha256').update(bytes).digest('hex');
const read = relative => fs.readFileSync(path.join(root, relative));
const parse = relative => JSON.parse(read(relative));
const dataPath = 'data/current-season/2026-27/';
const manifest = parse(dataPath + 'manifest.json');
const fail = message => { throw Error(message); };
const validDate = date => typeof date === 'string' && /^\d{4}-\d{2}-\d{2}$/.test(date) && !Number.isNaN(Date.parse(date)) && new Date(date).toISOString().slice(0, 10) === date;
if (manifest.commit !== 'e6744429ee395bc86f247348c6184bb08d4eb361' || manifest.resultsThrough !== '2026-09-20'
    || !validDate(manifest.resultsThrough) || !/^2026-10-23T\d{2}:\d{2}:\d{2}Z$/.test(manifest.retrievedAt)
    || Number.isNaN(Date.parse(manifest.retrievedAt)) || manifest.resultsThrough > manifest.retrievedAt.slice(0, 10)) fail('Invalid pinned snapshot metadata.');
const crestIds = ['arsenal', 'aston-villa', 'bournemouth', 'brentford', 'brighton', 'chelsea', 'coventry', 'crystal-palace', 'everton', 'fulham', 'hull', 'ipswich', 'leeds', 'liverpool', 'manchester-city', 'manchester-united', 'newcastle', 'nottingham-forest', 'sunderland', 'tottenham'];
const requiredPins = [dataPath + 'en.1.json', dataPath + 'LICENSE.md', dataPath + 'club-context.json', '.local/league-reference-v1/league-reference.json',
  'data/reference-foundation/memberships.json', 'data/reference-foundation/membership-ground-review.json', 'data/reference-foundation/grounds.json',
  dataPath + 'crest-sources.json', ...crestIds.map(id => 'src/Matchmate.Web/wwwroot/assets/crests/' + id + '-2026.png')];
if (manifest.files.length !== requiredPins.length || new Set(manifest.files.map(f => f.path)).size !== requiredPins.length) fail('Invalid source hash manifest.');
for (const relative of requiredPins) {
  const pin = manifest.files.find(f => f.path === relative);
  if (!pin || hash(read(relative)) !== pin.sha256) fail('Pinned source SHA mismatch: ' + relative);
}
if (hash(read(dataPath + 'en.1.json')) !== 'bb91f0ab3e8359df163bb9ec98549bfed85e200dd4c32f3bd606b5890d932dd2'
    || hash(read(dataPath + 'LICENSE.md')) !== '36ffd9dc085d529a7e60e1276d73ae5a030b020313e6c5408593a6ae2af39673'
    || hash(read('.local/league-reference-v1/league-reference.json')) !== '32982d9bc2bc272fdd6b7d0c97227326f47d2ac680e62c80dc97aae828f5bbb4') fail('Unexpected immutable input.');
const original = parse('.local/league-reference-v1/league-reference.json');
if (original.schemaVersion !== 1 || original.version !== 'poc-league-v1' || original.seasons.length !== 2
    || original.seasons.some(s => !['2015-16', '2016-17'].includes(s.seasonKey) || s.clubs.length !== 20 || s.matches.length !== 380)) fail('Invalid historical bundle.');
const context = parse(dataPath + 'club-context.json'), raw = parse(dataPath + 'en.1.json'), crests = parse(dataPath + 'crest-sources.json');
if (crests.assets.length !== 20 || JSON.stringify(crests.assets.map(a => a.clubId).sort()) !== JSON.stringify(crestIds)) fail('Invalid current badge membership.');
for (const asset of crests.assets) {
  const bytes = read(asset.path), crestPath = 'assets/crests/' + asset.clubId + '-2026.png';
  if (asset.path !== 'src/Matchmate.Web/wwwroot/' + crestPath || asset.crestPath !== crestPath
      || asset.url !== 'https://resources.premierleague.com/premierleague/badges/100/t' + asset.optaId + '.png'
      || bytes.subarray(0, 8).toString('hex') !== '89504e470d0a1a0a' || bytes.subarray(12, 16).toString() !== 'IHDR'
      || bytes.readUInt32BE(16) !== asset.width || bytes.readUInt32BE(20) !== asset.height || asset.width < 1 || asset.width > 512 || asset.height < 1 || asset.height > 512
      || bytes.length !== asset.bytes || hash(bytes) !== asset.sha256) fail('Invalid pinned current PNG badge: ' + asset.clubId);
}
const memberships = parse('data/reference-foundation/memberships.json').filter(m => m.season === '2026/27');
const expected = memberships.map(m => m.club_id).sort();
if (context.seasonKey !== '2026-27' || context.clubs.length !== 20 || memberships.length !== 20
    || JSON.stringify(context.clubs.map(c => c.club.id).sort()) !== JSON.stringify(expected)) fail('Current membership mismatch.');
const names = new Map(), appearances = new Map();
for (const row of context.clubs) {
  const member = memberships.find(m => m.club_id === row.club.id);
  if (!member || row.sourceTeamName !== member.source_team_name || names.has(row.sourceTeamName)
      || row.club.crestPath !== crests.assets.find(a => a.clubId === row.club.id)?.crestPath) fail('Ambiguous source club/badge mapping.');
  names.set(row.sourceTeamName, row.club.id); appearances.set(row.club.id, 0);
}
if (raw.matches.length !== 380 || manifest.scheduledFixtures !== 380) fail('Expected 380 scheduled fixtures.');
const fixtures = new Set(), matches = [], sourceNames = new Set(); let pending = 0, arrayScores = 0;
for (const fixture of raw.matches) {
  const home = names.get(fixture.team1), away = names.get(fixture.team2), matchKey = home + ':' + away;
  if (!home || !away || home === away || fixtures.has(matchKey) || !validDate(fixture.date)
      || fixture.date < '2026-07-01' || fixture.date > '2027-06-30' || typeof fixture.round !== 'string' || fixture.round.length > 100) fail('Invalid/duplicate scheduled fixture.');
  fixtures.add(matchKey); sourceNames.add(fixture.team1); sourceNames.add(fixture.team2);
  const score = Array.isArray(fixture.score) ? fixture.score : fixture.score?.ft;
  if (score === undefined) { if (fixture.score !== undefined && fixture.score !== null) fail('Unrecognized score format.'); pending++; continue; }
  if (!Array.isArray(score) || score.length !== 2 || score.some(n => !Number.isInteger(n) || n < 0)) fail('Invalid full-time score.');
  if (fixture.date > manifest.resultsThrough) fail('Scored fixture exceeds accepted results cutoff.');
  if (Array.isArray(fixture.score)) arrayScores++;
  appearances.set(home, appearances.get(home) + 1); appearances.set(away, appearances.get(away) + 1);
  matches.push({matchKey, date: fixture.date, home, away, homeGoals: score[0], awayGoals: score[1], round: fixture.round});
}
matches.sort((a,b) => a.date.localeCompare(b.date) || a.matchKey.localeCompare(b.matchKey));
if (sourceNames.size !== 20 || matches.length !== 50 || pending !== 330 || arrayScores !== 5 || manifest.completedResults !== matches.length
    || manifest.clubCount !== 20 || matches[0].date !== manifest.firstResultDate || matches.at(-1).date !== manifest.resultsThrough
    || [...appearances.values()].some(n => n > 38)) fail('Unexpected snapshot result coverage.');
const current = {seasonKey: '2026-27', label: '2026/27', clubs: context.clubs.map(c => c.club), matches, status: 'ongoing',
  scheduledMatchCount: 380, resultsThrough: manifest.resultsThrough, retrievedAt: manifest.retrievedAt};
const source = (sourceId, relative, license) => ({sourceId, path: relative, sha256: hash(read(relative)), license});
const sources = [...original.sources,
  source('current-results', dataPath + 'en.1.json', 'CC0-1.0'),
  source('current-license', dataPath + 'LICENSE.md', 'CC0-1.0'),
  source('current-context', dataPath + 'club-context.json', 'Project curated illustration/context; source limitations retained.'),
  source('current-manifest', dataPath + 'manifest.json', 'Project provenance for CC0-1.0 results.'),
  source('current-crests', dataPath + 'crest-sources.json', 'Copyright/trademark artwork; Matchmate reuse licence not established. Not CC0.'),
  source('historical-bundle', '.local/league-reference-v1/league-reference.json', 'Original bundle provenance and rights retained.'),
  source('current-exporter', 'tools/export-current-league-reference.cjs', 'Project source adapter.')];
const bundle = {...original, schemaVersion: 2, version: 'league-reference-v2', sources,
  resultsSourceMetadata: {historical: original.resultsSourceMetadata, current: manifest},
  crestSourceMetadata: {historical: original.crestSourceMetadata, current: crests}, seasons: [...original.seasons, current]};
const bytes = Buffer.from(JSON.stringify(bundle)); if (bytes.length > 2_000_000) fail('Bundle cap exceeded.');
const folder = path.resolve(process.argv[2] || path.join(root, '.local/league-reference-v2')); fs.mkdirSync(folder, {recursive: true});
const destination = path.join(folder, 'league-reference.json');
if (fs.existsSync(destination)) { if (!fs.readFileSync(destination).equals(bytes)) fail('Immutable export conflict: ' + destination); }
else fs.writeFileSync(destination, bytes, {flag: 'wx'});
console.log(JSON.stringify({bundle: {path: destination, bytes: bytes.length, sha256: hash(bytes)}, sourceSha256: hash(read(dataPath + 'en.1.json')),
  seasons: bundle.seasons.map(s => ({key:s.seasonKey, clubs:s.clubs.length, results:s.matches.length, scheduled:s.scheduledMatchCount ?? 380})), pendingExcluded: pending, flatZeroScores: arrayScores}));

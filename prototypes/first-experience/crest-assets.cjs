'use strict';
const fs = require('node:fs');
const path = require('node:path');
const allowedTags = new Set(['svg','g','path','polygon','rect','defs','clipPath','linearGradient','stop','style']);
function assertSafeSvg(text) {
  if (/<!DOCTYPE|<!ENTITY|<\?|\bon[a-z]+\s*=|@import|javascript:|data:/i.test(text)) throw new Error('Active SVG content');
  for (const tag of text.matchAll(/<\/?([\w:-]+)(?=[\s/>])/g)) {
    if (!allowedTags.has(tag[1])) throw new Error('Unexpected SVG element: '+tag[1]);
  }
  for (const ref of text.matchAll(/\b(?:[\w-]+:)?(?:href|src)\s*=\s*(["'])(.*?)\1/gi)) {
    if (!/^#[\w.-]+$/.test(ref[2])) throw new Error('External SVG reference');
  }
  for (const ref of text.matchAll(/url\(\s*["']?([^)'"\s]+)["']?\s*\)/gi)) {
    if (!/^#[\w.-]+$/.test(ref[1])) throw new Error('External SVG URL');
  }
  return true;
}
function loadAssets(root = __dirname) {
  const manifest = JSON.parse(fs.readFileSync(path.join(root,'assets/crests/manifest.json'),'utf8'));
  return manifest.assets.map(asset => {
    if (!/^[a-z0-9-]+\.(svg|png)$/.test(asset.file)) throw new Error('Unexpected crest path');
    const bytes = fs.readFileSync(path.join(root,'assets/crests',asset.file));
    const svg = asset.file.endsWith('.svg');
    if (svg) assertSafeSvg(bytes.toString('utf8'));
    else if (!bytes.subarray(0,8).equals(Buffer.from([137,80,78,71,13,10,26,10]))) throw new Error('Invalid PNG');
    return {path:'assets/crests/'+asset.file,dataUrl:`data:image/${svg?'svg+xml':'png'};base64,${bytes.toString('base64')}`};
  });
}
module.exports = {assertSafeSvg,loadAssets};

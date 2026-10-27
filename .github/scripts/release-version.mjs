import { execFileSync, spawnSync } from 'node:child_process';
import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

export function commit(value) {
  if (!/^[0-9a-f]{40}$/.test(value ?? '')) throw new Error('Expected a full lowercase commit SHA.');
  return value;
}

export function version(tag) {
  const match = /^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/.exec(tag ?? '');
  if (!match || tag.length > 40) throw new Error('Release tag must be canonical vMAJOR.MINOR.PATCH, at most 40 characters.');
  const parts = match.slice(1).map(Number);
  if (parts.some(part => !Number.isSafeInteger(part))) throw new Error('Release version exceeds safe integer bounds.');
  return parts;
}

function compare(left, right) {
  const a = version(left), b = version(right);
  return a[0] - b[0] || a[1] - b[1] || a[2] - b[2];
}

export function releases(refs) {
  const names = new Set();
  return refs.filter(ref => /^v\d/.test(ref.tag)).map(ref => {
    version(ref.tag); commit(ref.sha);
    if (names.has(ref.tag)) throw new Error(`Conflicting or duplicated release reference: ${ref.tag}`);
    names.add(ref.tag);
    return ref;
  }).sort((a, b) => compare(a.tag, b.tag));
}

export function remoteTags(text) {
  const direct = new Map(), peeled = new Map();
  for (const line of text.split(/\r?\n/).filter(Boolean)) {
    const match = /^([a-f0-9]{40})\s+refs\/tags\/(.+?)(\^\{\})?$/.exec(line);
    if (!match) throw new Error('Invalid remote tag reference.');
    if (!/^v\d/.test(match[2])) continue;
    version(match[2]);
    const target = match[3] ? peeled : direct;
    if (target.has(match[2])) throw new Error('Duplicated remote tag reference.');
    target.set(match[2], match[1]);
  }
  for (const tag of peeled.keys()) if (!direct.has(tag)) throw new Error('Annotated tag is missing its reference.');
  return [...direct].map(([tag, sha]) => ({ tag, sha: peeled.get(tag) ?? sha }));
}

export function planRelease(sha, refs, isAncestor) {
  commit(sha);
  const ordered = releases(refs), same = ordered.filter(ref => ref.sha === sha);
  if (same.length > 1) throw new Error('Commit has multiple release tags; resolve ambiguity without moving any tag.');
  if (same.length === 1) return { ...same[0], reused: true };
  const latest = ordered.at(-1);
  if (latest && !isAncestor(latest.sha, sha)) throw new Error('Stale or divergent commit cannot receive the next release version.');
  const parts = latest ? version(latest.tag) : [0, 1, -1];
  if (!Number.isSafeInteger(parts[2] + 1)) throw new Error('Patch version exceeds safe integer bounds.');
  const tag = `v${parts[0]}.${parts[1]}.${parts[2] + 1}`;
  version(tag);
  return { tag, sha, reused: false };
}

export async function createRelease(sha, refs, isAncestor, createReference, readReference) {
  const planned = planRelease(sha, refs, isAncestor);
  if (planned.reused) return planned;
  try {
    await createReference(planned.tag, sha);
  } catch (error) {
    if (![409, 422].includes(error.status)) throw error;
    if (await readReference(planned.tag) !== sha) throw new Error(`Release tag conflict: ${planned.tag} belongs to another commit; it will not be moved.`);
    return { ...planned, reused: true };
  }
  if (await readReference(planned.tag) !== sha) throw new Error('Created release reference did not match the intended commit.');
  return planned;
}

export function verifyRelease(tag, expected, refs, isAncestor, main, { head, latest = false } = {}) {
  version(tag); commit(expected); commit(main);
  const ordered = releases(refs), selected = ordered.find(ref => ref.tag === tag);
  if (!selected || selected.sha !== expected) throw new Error('Release tag does not resolve to the expected commit.');
  if (head !== undefined && commit(head) !== expected) throw new Error('Checked-out source does not equal the release commit.');
  if (!isAncestor(expected, main)) throw new Error('Release commit is not on main.');
  if (latest && ordered.at(-1)?.tag !== tag) throw new Error('An older release cannot overwrite the latest development release.');
  return selected;
}

export function imageTag(tag, sha, run, attempt) {
  version(tag); commit(sha);
  if (!/^\d{1,20}$/.test(run ?? '') || !/^[1-9]\d{0,7}$/.test(attempt ?? '')) throw new Error('Invalid workflow run identity.');
  const result = `${tag}-sha-${sha}-run-${run}-${attempt}`;
  if (result.length > 128) throw new Error('Immutable image tag exceeds registry limits.');
  return result;
}

function git(...args) { return execFileSync('git', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim(); }
function isAncestor(older, newer) {
  const result = spawnSync('git', ['merge-base', '--is-ancestor', commit(older), commit(newer)], { encoding: 'utf8' });
  if (result.status === 0) return true;
  if (result.status === 1) return false;
  throw new Error('Cannot verify main ancestry.');
}
function refresh() {
  git('fetch', '--no-tags', 'origin', '+refs/heads/main:refs/remotes/origin/main');
  git('fetch', '--tags', 'origin'); // Refuse rewritten remote tags; never force tag updates.
}
function readTags() {
  return remoteTags(git('ls-remote', '--tags', 'origin')); // Deleted refs must not survive through the local tag cache.
}
function output(values) {
  if (!process.env.GITHUB_OUTPUT) throw new Error('GITHUB_OUTPUT is required.');
  appendFileSync(process.env.GITHUB_OUTPUT, Object.entries(values).map(([key, value]) => `${key}=${value}\n`).join(''));
}
function summary(text) { if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, `${text}\n`); }
async function request(path, body) {
  const token = process.env.GITHUB_TOKEN, repository = process.env.GITHUB_REPOSITORY;
  if (!token || !/^[\w.-]+\/[\w.-]+$/.test(repository ?? '')) throw new Error('Repository token and identity are required.');
  const response = await fetch(`${process.env.GITHUB_API_URL || 'https://api.github.com'}/repos/${repository}/${path}`, {
    method: body ? 'POST' : 'GET', headers: { Authorization: `Bearer ${token}`, Accept: 'application/vnd.github+json', 'Content-Type': 'application/json', 'X-GitHub-Api-Version': '2022-11-28' },
    ...(body ? { body: JSON.stringify(body) } : {}), signal: AbortSignal.timeout(30000)
  });
  if (!response.ok) { const error = new Error(`GitHub reference request failed with HTTP ${response.status}.`); error.status = response.status; throw error; }
  return response.json();
}

async function main() {
  if (process.env.GITHUB_REF !== 'refs/heads/main') throw new Error('Release and deployment workflows must run from main, preserving environment/OIDC policy.');
  const mode = process.argv[2], expectedInput = process.env.EXPECTED_COMMIT || '', tag = process.env.RELEASE_TAG || '';
  if (!['create', 'select', 'verify-checkout', 'guard-deploy'].includes(mode)) throw new Error('Unknown release command.');
  if (mode !== 'create' && !['preview', 'deploy'].includes(process.env.DEPLOY_ACTION)) throw new Error('Action must be preview or deploy.');
  if (mode === 'select' && process.env.GITHUB_EVENT_NAME !== 'workflow_dispatch' && (!tag || !expectedInput)) throw new Error('Reusable deployment requires explicit release tag and expected commit.');
  refresh();
  const refs = readTags(), mainCommit = commit(git('rev-parse', 'refs/remotes/origin/main^{commit}'));
  if (mode === 'create') {
    const sha = commit(expectedInput);
    if (git('rev-parse', 'HEAD') !== sha || !isAncestor(sha, mainCommit)) throw new Error('Merged source/commit/main verification failed.');
    const result = await createRelease(sha, refs, isAncestor,
      (name, target) => request('git/refs', { ref: `refs/tags/${name}`, sha: target }),
      async name => { const ref = await request(`git/ref/tags/${name}`); return ref.object.type === 'commit' ? ref.object.sha : git('rev-parse', `refs/tags/${name}^{commit}`); });
    output({ release_tag: result.tag, expected_commit: result.sha });
    summary(`### Release ${result.tag}\nCommit: \`${result.sha}\`\nTag ${result.reused ? 'reused without movement' : 'created immutably'}. Deployment is invoked in this same main workflow event.`);
    return;
  }
  if (!tag) {
    if (process.env.DEPLOY_ACTION !== 'preview') throw new Error('Deploy requires an existing release_tag.');
    const sha = commit(git('rev-parse', 'HEAD'));
    if (!isAncestor(sha, mainCommit)) throw new Error('Preview source is not on main.');
    if (mode === 'verify-checkout') { if (sha !== commit(expectedInput)) throw new Error('Preview checkout does not match the selected source.'); return; }
    if (mode !== 'select') throw new Error('Only a release tag may be deployed.');
    output({ release_tag: '', expected_commit: sha, checkout_ref: sha, image_tag: `preview-sha-${sha}-run-${process.env.GITHUB_RUN_ID}-${process.env.GITHUB_RUN_ATTEMPT}` });
    return;
  }
  version(tag);
  const expected = commit(expectedInput || refs.find(ref => ref.tag === tag)?.sha);
  verifyRelease(tag, expected, refs, isAncestor, mainCommit, {
    ...(mode !== 'select' ? { head: git('rev-parse', 'HEAD') } : {}), latest: mode === 'guard-deploy' || process.env.DEPLOY_ACTION === 'deploy'
  });
  if (mode === 'select') {
    const image = imageTag(tag, expected, process.env.GITHUB_RUN_ID, process.env.GITHUB_RUN_ATTEMPT);
    output({ release_tag: tag, expected_commit: expected, checkout_ref: `refs/tags/${tag}`, image_tag: image });
    summary(`### Development deployment\nGit release: \`${tag}\`\nCommit: \`${expected}\`\nAll three images: \`${image}\`\nAction: ${process.env.DEPLOY_ACTION}`);
  }
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) main().catch(error => { console.error(error.message); process.exitCode = 1; });

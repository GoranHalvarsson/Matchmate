import test from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { commit, version, releases, remoteTags, planRelease, createRelease, verifyRelease, imageTag } from './release-version.mjs';

const a = 'a'.repeat(40), b = 'b'.repeat(40), c = 'c'.repeat(40);
const ancestry = (older, newer) => older === newer || older === a && [b, c].includes(newer) || older === b && newer === c;

test('first release starts at v0.1.0 and subsequent releases increment the numeric patch', () => {
  assert.deepEqual(planRelease(a, [], ancestry), { tag: 'v0.1.0', sha: a, reused: false });
  assert.deepEqual(planRelease(c, [{ tag: 'v0.1.9', sha: a }, { tag: 'v0.1.10', sha: b }], ancestry), { tag: 'v0.1.11', sha: c, reused: false });
  assert.equal(planRelease(c, [{ tag: 'v1.2.3', sha: b }], ancestry).tag, 'v1.2.4');
});

test('same SHA rerun reuses its tag and performs no reference write', async () => {
  let writes = 0;
  const result = await createRelease(a, [{ tag: 'v0.1.0', sha: a }, { tag: 'v0.1.1', sha: b }], ancestry,
    () => { writes++; }, () => { throw new Error('not called'); });
  assert.deepEqual(result, { tag: 'v0.1.0', sha: a, reused: true });
  assert.equal(writes, 0);
});

test('new reference binds one version to the exact commit and is read back', async () => {
  const stored = new Map(), writes = [];
  const result = await createRelease(a, [], ancestry, async (tag, sha) => { writes.push({ tag, sha }); stored.set(tag, sha); }, async tag => stored.get(tag));
  assert.deepEqual(writes, [{ tag: 'v0.1.0', sha: a }]);
  assert.deepEqual(result, { tag: 'v0.1.0', sha: a, reused: false });
});

test('concurrent same-commit create is idempotent; conflicting reference is never moved', async () => {
  const conflict = () => { throw Object.assign(new Error('already exists'), { status: 422 }); };
  assert.equal((await createRelease(a, [], ancestry, conflict, async () => a)).reused, true);
  await assert.rejects(createRelease(a, [], ancestry, conflict, async () => b), /belongs to another commit/);
  await assert.rejects(createRelease(a, [], ancestry, async () => {}, async () => b), /did not match/);
});

test('unrelated GitHub failures do not trigger mutation retries or new versions', async () => {
  let attempts = 0;
  await assert.rejects(createRelease(a, [], ancestry, async () => { attempts++; throw Object.assign(new Error('server unavailable'), { status: 503 }); }, async () => a), /server unavailable/);
  assert.equal(attempts, 1);
});

test('stale/divergent commits cannot be assigned a newer version; ambiguous tags fail closed', () => {
  assert.throws(() => planRelease(a, [{ tag: 'v0.1.1', sha: b }], ancestry), /Stale or divergent/);
  assert.throws(() => planRelease(c, [{ tag: 'v0.1.1', sha: b }], () => false), /Stale or divergent/);
  assert.throws(() => planRelease(a, [{ tag: 'v0.1.0', sha: a }, { tag: 'v0.1.1', sha: a }], ancestry), /multiple release tags/);
  assert.throws(() => releases([{ tag: 'v0.1.0', sha: a }, { tag: 'v0.1.0', sha: b }]), /Conflicting/);
});

test('release input rejects malformed versions, injection, prereleases and unsafe numbers', () => {
  for (const tag of ['', 'main', 'refs/tags/v0.1.0', 'v01.1.0', 'v0.01.0', 'v0.1.00', 'v0.1.0-rc.1', 'v0.1.0+build', 'v0.1.0\nnext=x', '../v0.1.0', 'v9007199254740992.0.0']) {
    assert.throws(() => version(tag), undefined, tag);
  }
  assert.deepEqual(version('v0.1.0'), [0, 1, 0]);
  assert.throws(() => commit('short-sha'));
  assert.throws(() => releases([{ tag: 'v01.1.0', sha: a }]));
  assert.deepEqual(releases([{ tag: 'historical-demo', sha: a }]), []);
});

test('fresh remote membership resolves lightweight and annotated tags, excluding deleted local refs', () => {
  assert.deepEqual(remoteTags(`${a}\trefs/tags/v0.1.0\n${b}\trefs/tags/v0.1.1\n${c}\trefs/tags/v0.1.1^{}\n`), [{ tag: 'v0.1.0', sha: a }, { tag: 'v0.1.1', sha: c }]);
  assert.deepEqual(remoteTags(''), []);
  assert.throws(() => remoteTags(`${a}\trefs/tags/v0.1.0\n${b}\trefs/tags/v0.1.0`), /Duplicated/);
  assert.throws(() => remoteTags(`${a}\trefs/tags/v0.1.0^{}`), /missing its reference/);
});

test('checkout and main ancestry are verified independently of caller context SHA', () => {
  const refs = [{ tag: 'v0.1.0', sha: a }];
  assert.equal(verifyRelease('v0.1.0', a, refs, ancestry, c, { head: a, latest: true }).sha, a);
  assert.throws(() => verifyRelease('v0.1.0', b, refs, ancestry, c), /does not resolve/);
  assert.throws(() => verifyRelease('v0.1.0', a, refs, ancestry, c, { head: b }), /Checked-out source/);
  assert.throws(() => verifyRelease('v0.1.0', a, refs, () => false, c), /not on main/);
  assert.throws(() => verifyRelease('v0.1.1', a, refs, ancestry, c), /does not resolve/);
});

test('older rerun remains recognizable but cannot overwrite a newer development release', () => {
  const refs = [{ tag: 'v0.1.0', sha: a }, { tag: 'v0.1.1', sha: b }];
  assert.equal(planRelease(a, refs, ancestry).tag, 'v0.1.0');
  assert.throws(() => verifyRelease('v0.1.0', a, refs, ancestry, c, { latest: true }), /older release/);
  assert.equal(verifyRelease('v0.1.1', b, refs, ancestry, c, { head: b, latest: true }).sha, b);
});

test('images include validated release, exact commit and unique run attempt', () => {
  assert.equal(imageTag('v0.1.0', a, '12345', '2'), `v0.1.0-sha-${a}-run-12345-2`);
  assert.notEqual(imageTag('v0.1.0', a, '12345', '1'), imageTag('v0.1.0', a, '12345', '2'));
  assert.throws(() => imageTag('v0.1.0', a, '123;echo', '1'));
  assert.throws(() => imageTag('v0.1.0', a, '123', '0'));
});

test('actual CLI rejects an off-main event before Git, network or token use', () => {
  const result = spawnSync(process.execPath, ['.github/scripts/release-version.mjs', 'create'], {
    encoding: 'utf8', env: { ...process.env, GITHUB_REF: 'refs/heads/feature', GITHUB_TOKEN: 'fixed-test-placeholder' }
  });
  assert.equal(result.status, 1); assert.match(result.stderr, /must run from main/);
  assert.doesNotMatch(result.stderr + result.stdout, /fixed-test-placeholder/);
});

test('workflow boundaries preserve same-event deployment, least permissions and CI source exclusions', () => {
  const read = name => readFileSync(new URL(`../workflows/${name}`, import.meta.url), 'utf8');
  const caller = read('release-development.yml'), deploy = read('deploy-development.yml'), validation = read('validate.yml');
  assert.match(caller, /push:\s+branches: \[main\]/);
  assert.match(caller, /tag:\s+needs: validate/);
  assert.match(caller, /deploy:\s+needs: tag/);
  assert.match(caller, /uses: \.\/\.github\/workflows\/deploy-development.yml/);
  assert.match(caller, /release_tag: \$\{\{ needs\.tag\.outputs\.release_tag \}\}/);
  assert.match(caller, /expected_commit: \$\{\{ needs\.tag\.outputs\.expected_commit \}\}/);
  assert.match(caller, /pipeline_enabled: true/);
  assert.equal((caller.match(/contents: write/g) || []).length, 1);
  assert.match(deploy, /workflow_call:/); assert.match(deploy, /environment: development/);
  assert.match(deploy, /ref: \$\{\{ steps\.version\.outputs\.checkout_ref \}\}/);
  assert.match(deploy, /release-version\.mjs verify-checkout/); assert.match(deploy, /release-version\.mjs guard-deploy/);
  assert.doesNotMatch(deploy, /tag=sha-\$\{GITHUB_SHA\}/);
  assert.notEqual(/group: (\S+)/.exec(caller)[1], /group: (\S+)/.exec(deploy)[1]);
  for (const workflow of [caller, deploy]) assert.match(workflow, /cancel-in-progress: false\s+queue: max/);
  assert.match(validation, /pull_request:\s+branches: \[main\]/);
  assert.doesNotMatch(validation, /id-token:|azure\/login|environment:|contents: write|pull_request_target/);
  const filter = /--filter '([^']+)'/;
  assert.equal(filter.exec(deploy)[1], filter.exec(validation)[1]);
  assert.match(filter.exec(validation)[1], /LeagueUiTests/);
  assert.match(filter.exec(validation)[1], /FullyQualifiedName~CurrentSeasonTests/);
  assert.match(filter.exec(validation)[1], /FullyQualifiedName~LeagueChat/);
  assert.match(filter.exec(validation)[1], /FullyQualifiedName!~All208OriginalSnapshotsAnd4160ClubFactsMatchPinnedPocAndPeriodContext/);
  assert.match(filter.exec(validation)[1], /&RequiresLocalData!=true$/);
});

const registrySource = path => readFileSync(new URL(`../../${path}`, import.meta.url), 'utf8');

// Every literal registry/scope reference must belong to the reviewed contract.
// Registry resources must bind to the supplied parameter, rather than a literal name.
// These normalized pins also reject arbitrary unquoted registry names and scope names.
// Any source edit requires review and deliberate repinning; only CRLF/LF is normalized.
const approvedRegistrySourcePins = {
  '.github/workflows/deploy-development.yml': 'b0ab41feff19754a1868e7f28c85c2ea9948bf911cb5fbef6d3f79d38b0de5af',
  'infra/container-apps/apps.bicep': 'ece9292fbe4ea0c81070c210c6532fe247790f0b42f891c028986e1712a8257d',
  'infra/azure-foundation/registry.bicep': '80f6f4e1d7c01efa94f41c62a453abdb4ef3bc6d87da9c0373745f6458b8a65d',
  'infra/azure-foundation/registry-access.bicep': '4c68634cd660c161faf89baf7ecab70eb731040b15255a74798ef4b533c6d55a'
};
const registrySourceDigest = source => createHash('sha256').update(source.replace(/\r\n/g, '\n')).digest('hex');
const approvedRegistrySourceDigests = new Set([...Object.values(approvedRegistrySourcePins), 'c1563832e739a22ad054574ceeff4c24bd6e6694956115711b889a68cdc17105']);
function assertApprovedRegistryReferences(source, registry) {
  assert.ok(approvedRegistrySourceDigests.has(registrySourceDigest(source)), 'Registry source must match its reviewed normalized SHA256 pin.');
  const allowed = new Set([registry.name, registry.loginServer, registry.resourceGroup, 'AcrPull', 'AcrPush'].map(value => value.toLowerCase()));
  for (const match of source.matchAll(/\b(?:[a-z0-9-]+\.azurecr\.io|rg-[a-z0-9-]+)\b/gi)) {
    assert.ok(allowed.has(match[0].toLowerCase()), 'Literal registry or resource group must match the dedicated registry contract.');
  }
  for (const match of source.matchAll(/(['"])(acr[a-z0-9]{2,})\1/gi)) {
    assert.ok(allowed.has(match[2].toLowerCase()), 'Literal registry name must match the dedicated registry contract.');
  }
  for (const match of source.matchAll(/resource\s+\w+\s+'Microsoft\.ContainerRegistry\/registries@[^']+'(?:\s+existing)?\s*=\s*\{([^}]+)\}/g)) {
    assert.match(match[1], /^\s*name:\s*containerRegistryName\s*$/m);
  }
  assert.doesNotMatch(source, /shared-registry\.json/);
}


test('dedicated registry contract keeps release pushes and all application pulls on the same server', () => {
  const registry = JSON.parse(registrySource('infra/azure-foundation/dedicated-registry.json'));
  const deploy = registrySource('.github/workflows/deploy-development.yml');
  const apps = registrySource('infra/container-apps/apps.bicep');
  assert.equal(registry.resourceGroup, 'rg-matchmate-dev-swedencentral');
  assert.equal(registry.resourceId, `/subscriptions/${registry.subscriptionId}/resourceGroups/${registry.resourceGroup}/providers/Microsoft.ContainerRegistry/registries/${registry.name}`);
  assert.equal(registry.loginServer, `${registry.name}.azurecr.io`);
  for (const name of ['AZURE_RESOURCE_GROUP', 'REGISTRY_NAME', 'API_CLIENT_ID', 'EXTERNAL_ID_AUTHORITY', 'EXTERNAL_ID_CLIENT_ID']) {
    assert.ok(deploy.includes(`${name}: \${{ vars.${name} }}`));
  }
  assert.doesNotMatch(deploy, /REGISTRY_NAME: acr|REGISTRY_SERVER:|AZURE_RESOURCE_GROUP: rg-/);
  assert.match(apps, /^param registryServer string\s*$/m);
  assert.match(apps, /^param externalIdAuthority string\s*$/m);
  assert.match(apps, /^param externalIdClientId string\s*$/m);
  assert.match(apps, /param homeTenantId string = subscription\(\).tenantId/);
  assert.match(apps, /name: 'Authentication__ExternalId__Authority', value: externalIdAuthority/);
  assert.match(apps, /name: 'Authentication__ExternalId__ClientId', value: externalIdClientId/);
  assert.ok(deploy.includes('az acr show --name "$REGISTRY_NAME" --resource-group "$AZURE_RESOURCE_GROUP" --query loginServer --output tsv --only-show-errors'));
  assert.equal((deploy.match(/matchmate-deployment-config\.mjs" registry/g) || []).length, 1);
  assert.ok(deploy.indexOf('uses: azure/login@') < deploy.indexOf('name: Resolve registry login server from Azure'));
  assert.ok(deploy.indexOf('name: Resolve registry login server from Azure') < deploy.indexOf('name: Registry login with federated identity'));
  const retained = deploy.indexOf('cp -- .github/scripts/deployment-config.mjs "$RUNNER_TEMP/matchmate-deployment-config.mjs"');
  assert.ok(retained > 0 && retained < deploy.indexOf('name: Check out exactly the selected release source'));
  assert.ok(deploy.includes('node "$RUNNER_TEMP/matchmate-deployment-config.mjs" validate'));
  assert.ok(deploy.indexOf('name: Validate nonsecret deployment variables') < deploy.indexOf('uses: azure/login@'));
  assert.equal((apps.match(/registries: \[\{ server: registryServer, identity: pullIdentity.id \}\]/g) || []).length, 3);
  for (const [app, repository] of Object.entries(registry.repositories)) {
    assert.equal(repository, `matchmate/${app}`);
    assert.ok(deploy.includes(`tags: \${{ env.REGISTRY_SERVER }}/${repository}:\${{ steps.version.outputs.image_tag }}`));
    assert.ok(apps.includes(`image: '\${registryServer}/${repository}:\${imageTag}'`));
  }
  const deployments = deploy.split('\n').filter(line => /az deployment group (validate|what-if|create) /.test(line));
  assert.equal(deployments.length, 3);
  for (const command of deployments) {
    assert.ok(command.includes('--template-file infra/container-apps/apps.bicep'));
    assert.ok(command.includes('--parameters "${app_parameters[@]}"'));
  }
  for (const source of [deploy, apps]) assertApprovedRegistryReferences(source, registry);
});

test('registry bootstrap preserves authenticated Basic access and the existing Matchmate identities', () => {
  const registry = JSON.parse(registrySource('infra/azure-foundation/dedicated-registry.json'));
  const bootstrap = registrySource('infra/azure-foundation/registry.bicep');
  const access = registrySource('infra/azure-foundation/registry-access.bicep');
  assert.equal(registry.location, 'swedencentral');
  assert.equal(registry.sku, 'Basic');
  assert.equal(registry.adminUserEnabled, false);
  assert.equal(registry.anonymousPullEnabled, false);
  assert.equal(registry.publicNetworkAccess, 'Enabled');
  assert.equal(registry.roleAssignmentMode, 'LegacyRegistryPermissions');
  assert.equal(registry.armAudienceAuthenticationEnabled, true);
  assert.match(bootstrap, /Microsoft.ContainerRegistry\/registries@2025-11-01/);
  assert.match(bootstrap, /^param containerRegistryName string\s*$/m);
  assert.match(access, /^param containerRegistryName string\s*$/m);
  assert.ok(bootstrap.includes(`param location string = '${registry.location}'`));
  assert.ok(bootstrap.includes(`sku: { name: '${registry.sku}' }`));
  assert.match(bootstrap, /adminUserEnabled: false/);
  assert.match(bootstrap, /anonymousPullEnabled: false/);
  assert.match(bootstrap, /publicNetworkAccess: 'Enabled'/);
  assert.match(bootstrap, /roleAssignmentMode: 'LegacyRegistryPermissions'/);
  assert.match(bootstrap, /azureADAuthenticationAsArmPolicy: \{ status: 'enabled' \}/);
  assert.match(bootstrap, /module access '\.\/registry-access\.bicep'/);
  assert.match(bootstrap, /containerRegistryName: registry.name/);
  assert.equal(registry.pullIdentity.principalId, 'e807f470-22e8-4c98-8765-d18d616c1018');
  assert.equal(registry.publisherIdentity.principalId, 'f7c25144-981a-4a2d-876e-984ca03ed56e');
  assert.equal(registry.pullIdentity.role, 'AcrPull');
  assert.deepEqual(registry.publisherIdentity.roles, ['Reader', 'AcrPush']);
  for (const identity of [registry.pullIdentity, registry.publisherIdentity]) {
    assert.equal(identity.existing, true);
    assert.equal(identity.resourceGroup, registry.resourceGroup);
    assert.ok(access.includes(`= '${identity.name}'`));
    assert.equal(identity.resourceId.toLowerCase(), `/subscriptions/${registry.subscriptionId}/resourceGroups/${registry.resourceGroup}/providers/Microsoft.ManagedIdentity/userAssignedIdentities/${identity.name}`.toLowerCase());
  }
  assert.equal((access.match(/userAssignedIdentities@[^']+' existing/g) || []).length, 2);
  assert.equal((access.match(/Microsoft.Authorization\/roleAssignments@/g) || []).length, 3);
  assert.equal((access.match(/scope: registry/g) || []).length, 3);
  assert.match(access, /principalId: pullIdentity.properties.principalId/);
  assert.equal((access.match(/principalId: publisherIdentity.properties.principalId/g) || []).length, 2);
  for (const role of ['7f951dda-4ed3-4680-a7ca-43fe172d538d', '8311e382-0749-4cb8-b61a-304f252e45ec', 'acdd72a7-3385-48ef-bd42-f606fba81ae7']) assert.ok(access.includes(role));
  assertApprovedRegistryReferences(bootstrap + access, registry);
  assert.doesNotMatch(bootstrap + access, /listCredentials|adminUserEnabled: true/);
});

test('approved registry contract rejects foreign registry, resource group and literal resource mutations', () => {
  const registry = JSON.parse(registrySource('infra/azure-foundation/dedicated-registry.json'));
  const apps = registrySource('infra/container-apps/apps.bicep');
  const bootstrap = registrySource('infra/azure-foundation/registry.bicep');
  const access = registrySource('infra/azure-foundation/registry-access.bicep');
  for (const [relative, expected] of Object.entries(approvedRegistrySourcePins)) {
    assert.equal(registrySourceDigest(registrySource(relative)), expected);
    assert.doesNotThrow(() => assertApprovedRegistryReferences(registrySource(relative), registry));
  }
  for (const foreign of ["'acrforeignprod01'", 'acrforeignprod01', "'foreignregistry.azurecr.io'", "'rg-foreign-prod'", 'Foreign-PROD-RG', "'shared-registry.json'"]) {
    assert.throws(() => assertApprovedRegistryReferences(apps + '\n' + foreign, registry));
  }
  assert.throws(() => assertApprovedRegistryReferences(bootstrap.replace('name: containerRegistryName', "name: 'unapprovedregistry'"), registry));
  assert.doesNotThrow(() => assertApprovedRegistryReferences(bootstrap + access, registry));
});

test('registry roles stay operator-only while read-only PR validation compiles both entry points', () => {
  const deploy = registrySource('.github/workflows/deploy-development.yml');
  const validation = registrySource('.github/workflows/validate.yml');
  for (const template of ['registry.bicep', 'registry-access.bicep']) {
    assert.ok(validation.includes(`az bicep build --file infra/azure-foundation/${template} --stdout`));
    assert.doesNotMatch(deploy, new RegExp(`--template-file infra/azure-foundation/${template.replace('.', '\\.')}`));
  }
  for (const workflow of [deploy, validation]) assert.ok(workflow.includes('node --test .github/scripts/*.test.mjs'));
  assert.ok(validation.includes('az bicep build --file infra/container-apps/foundation.bicep --stdout'));
  assert.match(deploy, /contents: read\s+id-token: write/);
  assert.match(deploy, /az acr login --name "\$REGISTRY_NAME"/);
  for (const workflow of [deploy, validation]) {
    assert.doesNotMatch(workflow, /az role assignment|roleAssignments\/write|az acr create|az acr build|az acr task|az acr purge/);
  }
  assert.doesNotMatch(validation, /id-token:|azure\/login|environment:|contents: write|pull_request_target/);
});

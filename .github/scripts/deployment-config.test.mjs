import test from 'node:test';
import assert from 'node:assert/strict';
import { mkdtempSync, readFileSync, writeFileSync, rmSync, mkdirSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { validateDeploymentConfig, validateRegistryServer, validateWebAiConfig } from './deployment-config.mjs';

const config = {
  AZURE_RESOURCE_GROUP: 'rg-example-dev-swedencentral',
  REGISTRY_NAME: 'acrdemodev01',
  API_CLIENT_ID: '11111111-2222-3333-4444-555555555555',
  EXTERNAL_ID_AUTHORITY: 'https://examplefans.ciamlogin.com/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee/v2.0',
  EXTERNAL_ID_CLIENT_ID: '66666666-7777-8888-9999-000000000000'
};
const script = fileURLToPath(new URL('./deployment-config.mjs', import.meta.url));
const registry = 'acrdemodev01.azurecr.io';
const disabledAi = {
  WEB_AI_ENABLED: 'false', MATCH_CHAT_ENDPOINT: '', MATCH_CHAT_DEPLOYMENT: '',
  DECISION_ROUTING_ENDPOINT: '', DECISION_ROUTING_DEPLOYMENT: ''
};
const enabledAi = {
  WEB_AI_ENABLED: 'true', MATCH_CHAT_ENDPOINT: 'https://example-foundry.cognitiveservices.azure.com/',
  MATCH_CHAT_DEPLOYMENT: 'matchmate-luna', DECISION_ROUTING_ENDPOINT: 'https://example-foundry.cognitiveservices.azure.com/',
  DECISION_ROUTING_DEPLOYMENT: 'matchmate-decision-1'
};
const aiParameters = ['webAiEnabled bool', 'matchChatEndpoint string', 'matchChatDeployment string', 'decisionRoutingEndpoint string', 'decisionRoutingDeployment string'];

function withEnvironmentFile(action) {
  const directory = mkdtempSync(join(tmpdir(), 'matchmate-deployment-config-'));
  const path = join(directory, 'github-env');
  writeFileSync(path, 'EXISTING=value\n', 'utf8');
  try { action(path); } finally { rmSync(directory, { recursive: true, force: true }); }
}
function cli(command, path, overrides = {}) {
  return spawnSync(process.execPath, [script, command], {
    encoding: 'utf8', env: { ...process.env, ...config, ...disabledAi, GITHUB_ENV: path, ...overrides }
  });
}

test('valid nonsecret configuration returns only the allowlisted deployment fields', () => {
  assert.deepEqual(validateDeploymentConfig({ ...config, IGNORED: 'extra' }), { ...config, ...disabledAi });
  assert.equal(validateRegistryServer(registry), registry);
  assert.equal(validateRegistryServer('acrdemodev01-ab12cd34.azurecr.io'), 'acrdemodev01-ab12cd34.azurecr.io');
});

test('every required field rejects missing, empty, whitespace and non-string values', () => {
  for (const field of Object.keys(config)) {
    for (const value of [undefined, null, '', ' ', 123]) {
      assert.throws(() => validateDeploymentConfig({ ...config, [field]: value }), undefined, `${field}: ${String(value)}`);
    }
  }
  for (const value of [undefined, null, '', ' ', 123]) assert.throws(() => validateRegistryServer(value));
});

test('both client identifiers require a canonical GUID shape', () => {
  for (const field of ['API_CLIENT_ID', 'EXTERNAL_ID_CLIENT_ID']) {
    for (const value of ['x'.repeat(36), config[field].replaceAll('-', ''), `{${config[field]}}`, `${config[field]}extra`]) {
      assert.throws(() => validateDeploymentConfig({ ...config, [field]: value }));
    }
  }
});

test('resource group and ACR names reject unsafe characters and invalid Azure lengths', () => {
  for (const value of ['-option', 'group.', 'a'.repeat(91), 'group/name', 'group;echo', 'group$(id)', 'group name', 'group"name']) {
    assert.throws(() => validateDeploymentConfig({ ...config, AZURE_RESOURCE_GROUP: value }), undefined, value);
  }
  for (const value of ['abcd', 'a'.repeat(51), 'acr-name', 'acr.name', 'acr/name', 'acr;echo', 'acr name']) {
    assert.throws(() => validateDeploymentConfig({ ...config, REGISTRY_NAME: value }), undefined, value);
  }
});

test('authority accepts only the intended HTTPS CIAM tenant authority without URL extras', () => {
  const tenant = 'aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee';
  for (const value of [
    `http://examplefans.ciamlogin.com/${tenant}/v2.0`,
    `https://user@examplefans.ciamlogin.com/${tenant}/v2.0`,
    `https://examplefans.ciamlogin.com:443/${tenant}/v2.0`,
    `${config.EXTERNAL_ID_AUTHORITY}?x=1`, `${config.EXTERNAL_ID_AUTHORITY}#fragment`, `${config.EXTERNAL_ID_AUTHORITY}/`,
    `https://examplefans.ciamlogin.com.evil.example/${tenant}/v2.0`,
    `https://nested.examplefans.ciamlogin.com/${tenant}/v2.0`,
    `https://-examplefans.ciamlogin.com/${tenant}/v2.0`,
    'https://examplefans.ciamlogin.com/not-a-guid/v2.0'
  ]) assert.throws(() => validateDeploymentConfig({ ...config, EXTERNAL_ID_AUTHORITY: value }), undefined, value);
});

test('registry hostname accepts Azure hashed labels and rejects URLs, extra labels and unsafe hosts', () => {
  assert.equal(validateRegistryServer(`${'a'.repeat(63)}.azurecr.io`), `${'a'.repeat(63)}.azurecr.io`);
  for (const value of [
    `${'a'.repeat(64)}.azurecr.io`, 'https://acr.azurecr.io', 'acr.azurecr.io:443', 'acr.azurecr.io/path',
    'acr.azurecr.io?x=1', 'acr.azurecr.io#fragment', 'acr.azurecr.io.evil.example', 'nested.acr.azurecr.io',
    '-acr.azurecr.io', 'acr-.azurecr.io', 'acr_name.azurecr.io', 'acr.azurecr.io.', 'acr;echo.azurecr.io'
  ]) assert.throws(() => validateRegistryServer(value), undefined, value);
});

test('all fields reject CR, LF, NUL and other controls before regex boundary matching', () => {
  for (const control of ['\r', '\n', '\0', '\t', '\x1b', '\x7f', '\x85', '\u2028', '\u2029']) {
    for (const field of Object.keys(config)) assert.throws(() => validateDeploymentConfig({ ...config, [field]: config[field] + control }), /control characters/);
    assert.throws(() => validateRegistryServer(registry + control), /control characters/);
  }
});

test('Web AI defaults off and exports only the five nonsecret AI fields', () => {
  assert.deepEqual(validateWebAiConfig({}), disabledAi);
  assert.deepEqual(validateWebAiConfig({ WEB_AI_ENABLED: '' }), disabledAi);
  assert.deepEqual(validateWebAiConfig({ ...enabledAi, API_KEY: 'ignored', MATCH_CHAT_API_KEY: 'ignored', AZURE_CLIENT_ID: 'ignored' }), enabledAi);
  assert.deepEqual(validateWebAiConfig({ ...enabledAi, WEB_AI_ENABLED: 'false' }), { ...enabledAi, WEB_AI_ENABLED: 'false' });
  assert.deepEqual(validateWebAiConfig({ ...enabledAi, WEB_AI_ENABLED: undefined }), { ...enabledAi, WEB_AI_ENABLED: 'false' });
});

test('Web AI flag requires a literal opt-in and enabled configuration requires every field', () => {
  for (const value of [true, false, 1, 'TRUE', 'False', '1', 'yes', ' true', 'true\n']) {
    assert.throws(() => validateWebAiConfig({ ...enabledAi, WEB_AI_ENABLED: value }), /WEB_AI_ENABLED/);
  }
  for (const flag of ['true', 'false', '']) {
    for (const field of Object.keys(enabledAi).filter(name => name !== 'WEB_AI_ENABLED')) {
      for (const value of [undefined, null, '', ' ', 123]) {
        assert.throws(() => validateWebAiConfig({ ...enabledAi, WEB_AI_ENABLED: flag, [field]: value }), undefined, `${flag}: ${field}: ${String(value)}`);
      }
    }
  }
});

test('Web AI accepts only supported public Azure HTTPS resource roots', () => {
  for (const domain of ['services.ai', 'cognitiveservices', 'openai']) {
    for (const suffix of ['', '/']) {
      const endpoint = `https://example-resource.${domain}.azure.com${suffix}`;
      assert.equal(validateWebAiConfig({ ...enabledAi, MATCH_CHAT_ENDPOINT: endpoint }).MATCH_CHAT_ENDPOINT, endpoint);
    }
  }
  for (const endpoint of [
    'http://example.openai.azure.com', 'https://localhost', 'https://127.0.0.1', 'https://169.254.169.254/',
    'https://example.openai.azure.com.evil.test/', 'https://nested.example.openai.azure.com/',
    'https://user:password@example.openai.azure.com/', 'https://example.openai.azure.com:443/',
    'https://example.openai.azure.com:8080/', 'https://example.openai.azure.com/openai/v1/',
    'https://example.openai.azure.com//', 'https://example.openai.azure.com/?key=value',
    'https://example.openai.azure.com/#fragment', 'https://-example.openai.azure.com/',
    'https://example-.openai.azure.com/', `https://${'a'.repeat(64)}.openai.azure.com/`,
    'https://example.openai.azure.com./', 'https://example.openai.azure.com\\@evil.test/',
    'https://example%2eopenai.azure.com/', 'https://example.openai.azure.com/%2e%2e'
  ]) {
    for (const field of ['MATCH_CHAT_ENDPOINT', 'DECISION_ROUTING_ENDPOINT']) {
      assert.throws(() => validateWebAiConfig({ ...enabledAi, [field]: endpoint }), undefined, `${field}: ${endpoint}`);
      assert.throws(() => validateWebAiConfig({ ...enabledAi, WEB_AI_ENABLED: 'false', [field]: endpoint }));
    }
  }
});

test('deployment names stay bounded and cannot inject URL paths, queries or shell arguments', () => {
  for (const field of ['MATCH_CHAT_DEPLOYMENT', 'DECISION_ROUTING_DEPLOYMENT']) {
    for (const value of ['a', 'Model_1.2-alpha', 'a'.repeat(128)]) {
      assert.equal(validateWebAiConfig({ ...enabledAi, [field]: value })[field], value);
    }
    for (const value of ['a'.repeat(129), '-option', 'model/name', 'model?api-version=x', 'model#fragment', 'model%2fname', 'model name', 'model;echo', 'model$(id)', 'model"name', 'mødel']) {
      assert.throws(() => validateWebAiConfig({ ...enabledAi, [field]: value }), undefined, value);
    }
  }
});

test('all AI inputs reject controls without exporting their values', () => {
  for (const control of ['\r', '\n', '\0', '\t', '\x1b', '\x7f', '\x85', '\u2028', '\u2029']) {
    for (const field of Object.keys(enabledAi)) {
      assert.throws(() => validateWebAiConfig({ ...enabledAi, [field]: enabledAi[field] + control }));
    }
  }
});

test('CLI exports the full valid configuration once and the resolved hostname separately', () => withEnvironmentFile(path => {
  assert.equal(cli('validate', path).status, 0);
  assert.equal(readFileSync(path, 'utf8'), 'EXISTING=value\n' + Object.entries({ ...config, ...disabledAi }).map(([key, value]) => `${key}=${value}\n`).join(''));
  const result = cli('registry', path, { REGISTRY_SERVER: 'acrdemodev01-ab12cd34.azurecr.io' });
  assert.equal(result.status, 0);
  assert.ok(readFileSync(path, 'utf8').endsWith('REGISTRY_SERVER=acrdemodev01-ab12cd34.azurecr.io\n'));
}));

test('CLI exports enabled AI atomically and preserves complete settings when switched off', () => {
  for (const flag of ['true', 'false']) withEnvironmentFile(path => {
    const ai = { ...enabledAi, WEB_AI_ENABLED: flag };
    const result = cli('validate', path, { ...ai, MATCH_CHAT_API_KEY: 'never-export-this' });
    assert.equal(result.status, 0, result.stderr);
    assert.equal(readFileSync(path, 'utf8'), 'EXISTING=value\n' + Object.entries({ ...config, ...ai }).map(([key, value]) => `${key}=${value}\n`).join(''));
    assert.equal(result.stdout + result.stderr, '');
  });
});

test('invalid AI never partially exports base configuration or injected variables', () => withEnvironmentFile(path => {
  for (const overrides of [
    { WEB_AI_ENABLED: 'true' }, { MATCH_CHAT_DEPLOYMENT: 'partial' },
    { ...enabledAi, MATCH_CHAT_ENDPOINT: 'https://localhost/' },
    { ...enabledAi, DECISION_ROUTING_ENDPOINT: enabledAi.DECISION_ROUTING_ENDPOINT + '\nINJECTED=yes' },
    { ...enabledAi, WEB_AI_ENABLED: 'false', DECISION_ROUTING_DEPLOYMENT: 'model?query=unsafe' }
  ]) {
    const result = cli('validate', path, overrides);
    assert.equal(result.status, 1);
    assert.equal(readFileSync(path, 'utf8'), 'EXISTING=value\n');
    assert.doesNotMatch(result.stderr, /INJECTED=yes/);
  }
}));

test('CLI validation failure leaves existing GITHUB_ENV unchanged with no partial or injected export', () => withEnvironmentFile(path => {
  for (const overrides of [{ EXTERNAL_ID_CLIENT_ID: '' }, { AZURE_RESOURCE_GROUP: `${config.AZURE_RESOURCE_GROUP}\nINJECTED=yes` }]) {
    const result = cli('validate', path, overrides);
    assert.equal(result.status, 1);
    assert.equal(readFileSync(path, 'utf8'), 'EXISTING=value\n');
    assert.doesNotMatch(result.stderr, /INJECTED=yes/);
  }
  assert.equal(cli('registry', path, { REGISTRY_SERVER: `${registry}\nINJECTED=yes` }).status, 1);
  assert.equal(cli('unknown', path).status, 1);
  assert.equal(readFileSync(path, 'utf8'), 'EXISTING=value\n');
}));

test('importing validator has no CLI side effects', () => withEnvironmentFile(path => {
  const result = spawnSync(process.execPath, ['--input-type=module', '-e', `await import(${JSON.stringify(new URL('./deployment-config.mjs', import.meta.url).href)})`], {
    encoding: 'utf8', env: { ...process.env, ...config, GITHUB_ENV: path }
  });
  assert.equal(result.status, 0);
  assert.equal(result.stdout + result.stderr, '');
  assert.equal(readFileSync(path, 'utf8'), 'EXISTING=value\n');
}));

test('Container Apps AI wiring defaults off, stays Web-only and uses the existing identity without keys', () => {
  const apps = readFileSync(new URL('../../infra/container-apps/apps.bicep', import.meta.url), 'utf8');
  assert.match(apps, /^param webAiEnabled bool = false$/m);
  for (const name of ['matchChatEndpoint', 'matchChatDeployment', 'decisionRoutingEndpoint', 'decisionRoutingDeployment']) {
    assert.match(apps, new RegExp(`^param ${name} string = ''$`, 'm'));
  }
  const branches = /var webAiEnvironment = webAiEnabled \? \[([\s\S]*?)\] : \[([\s\S]*?)\]/.exec(apps);
  assert.ok(branches, 'runtime AI settings must be gated by the explicit opt-in');
  for (const name of ['MatchChat', 'DecisionRouting']) {
    assert.ok(branches[1].includes(`name: '${name}__Enabled', value: 'true'`));
    assert.ok(branches[2].includes(`name: '${name}__Enabled', value: 'false'`));
  }
  assert.doesNotMatch(branches[2], /__Endpoint|__Deployment/);
  const web = apps.slice(apps.indexOf('resource web '), apps.indexOf('resource api '));
  const downstream = apps.slice(apps.indexOf('resource api '));
  assert.ok(web.includes('], webAiEnvironment)'));
  assert.ok(web.includes("name: 'Runtime__ManagedIdentityClientId', value: webIdentity.properties.clientId"));
  assert.doesNotMatch(downstream, /webAiEnvironment|MatchChat__|DecisionRouting__/);
  assert.doesNotMatch(apps, /ApiKey|API_KEY|AZURE_CLIENT_ID|HistoricalReplyModel/);
});

test('actual workflow forwards identical validated AI parameters to preview and deploy, with fail-closed old releases', () => {
  const workflow = readFileSync(new URL('../workflows/deploy-development.yml', import.meta.url), 'utf8').replaceAll('\r\n', '\n');
  const preludes = [...workflow.matchAll(/          # Begin app parameters[\s\S]*?          # End app parameters/g)]
    .map(match => match[0].split('\n').map(line => line.slice(10)).join('\n'));
  assert.equal(preludes.length, 2);
  assert.equal(preludes[0], preludes[1], 'preview and deploy must construct the same parameters');
  const bash = process.platform === 'win32' ? join(process.env.ProgramFiles, 'Git', 'bin', 'bash.exe') : 'bash';
  const templates = [[], ['externalIdAuthority string', 'externalIdClientId string'], ['externalIdAuthority string', 'externalIdClientId string', ...aiParameters]];
  // Every individually missing AI parameter must block an enabled release, including mixed-version templates.
  for (const missing of aiParameters) templates.push(aiParameters.filter(parameter => parameter !== missing));
  for (const definitions of templates) for (const enabled of [false, true]) withEnvironmentFile(path => {
    const templateDirectory = join(dirname(path), 'infra', 'container-apps');
    mkdirSync(templateDirectory, { recursive: true });
    writeFileSync(join(templateDirectory, 'apps.bicep'), 'param registryServer string\n' + definitions.map(definition => `param ${definition}\n`).join(''));
    const prelude = preludes[0].replaceAll('${{ inputs.pipeline_enabled }}', 'true');
    const result = spawnSync(bash, ['--noprofile', '--norc', '-c', 'set -euo pipefail\n' + prelude + '\nprintf \'%s\\n\' "${app_parameters[@]}"'], {
      encoding: 'utf8', cwd: dirname(path), env: { ...process.env, ...config, ...enabledAi, WEB_AI_ENABLED: String(enabled), REGISTRY_SERVER: registry, IMAGE_TAG: 'v0.1.0-example' }
    });
    const supportsAi = aiParameters.every(parameter => definitions.includes(parameter));
    if (enabled && !supportsAi) {
      assert.equal(result.status, 1);
      assert.match(result.stderr, /does not support the complete Web AI configuration/);
      assert.equal(result.stdout, '');
      return;
    }
    assert.equal(result.status, 0, result.stderr);
    const expected = [`registryServer=${registry}`, `apiClientId=${config.API_CLIENT_ID}`, 'imageTag=v0.1.0-example', 'pipelineEnabled=true'];
    if (definitions.includes('externalIdAuthority string')) expected.push(`externalIdAuthority=${config.EXTERNAL_ID_AUTHORITY}`, `externalIdClientId=${config.EXTERNAL_ID_CLIENT_ID}`);
    if (supportsAi) expected.push(`webAiEnabled=${enabled}`, `matchChatEndpoint=${enabledAi.MATCH_CHAT_ENDPOINT}`, `matchChatDeployment=${enabledAi.MATCH_CHAT_DEPLOYMENT}`, `decisionRoutingEndpoint=${enabledAi.DECISION_ROUTING_ENDPOINT}`, `decisionRoutingDeployment=${enabledAi.DECISION_ROUTING_DEPLOYMENT}`);
    assert.deepEqual(result.stdout.trim().split(/\r?\n/), expected);
  });
  for (const field of Object.keys(enabledAi)) assert.ok(workflow.includes(`${field}: \${{ vars.${field} }}`), `${field} must come from environment variables for validation`);
});

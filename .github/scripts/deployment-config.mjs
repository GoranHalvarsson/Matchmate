import { appendFileSync } from 'node:fs';
import { pathToFileURL } from 'node:url';

const controls = /[\u0000-\u001f\u007f-\u009f\u2028\u2029]/;
const guid = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const aiFields = ['MATCH_CHAT_ENDPOINT', 'MATCH_CHAT_DEPLOYMENT', 'DECISION_ROUTING_ENDPOINT', 'DECISION_ROUTING_DEPLOYMENT'];

function required(source, name) {
  const value = source[name];
  if (typeof value !== 'string' || value.length === 0 || controls.test(value)) {
    throw new Error(`${name} must be set and must not contain control characters.`);
  }
  return value;
}

export function validateWebAiConfig(source) {
  const enabled = source.WEB_AI_ENABLED ?? '';
  if (!['', 'false', 'true'].includes(enabled)) {
    throw new Error('WEB_AI_ENABLED must be true, false, or unset (defaults to false).');
  }
  const values = Object.fromEntries(aiFields.map(name => [name, source[name] ?? '']));
  const configured = aiFields.some(name => values[name] !== '');
  if (enabled === 'true' || configured) {
    for (const name of aiFields) {
      const value = required(values, name);
      if (name.endsWith('_ENDPOINT')) {
        // Match AzureModelEndpoint's public Azure resource roots; reject URL extras before any export.
        if (!/^https:\/\/[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.(?:services\.ai|cognitiveservices|openai)\.azure\.com\/?$/.test(value)) {
          throw new Error(`${name} must be an HTTPS Azure AI resource root without credentials, port, path, query or fragment.`);
        }
      } else if (!/^[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$/.test(value)) {
        throw new Error(`${name} must contain 1 to 128 safe ASCII deployment-name characters, starting with a letter or digit.`);
      }
    }
  }
  return { WEB_AI_ENABLED: enabled === 'true' ? 'true' : 'false', ...values };
}

export function validateDeploymentConfig(source) {
  const group = required(source, 'AZURE_RESOURCE_GROUP');
  const registry = required(source, 'REGISTRY_NAME');
  const apiClient = required(source, 'API_CLIENT_ID');
  const authority = required(source, 'EXTERNAL_ID_AUTHORITY');
  const externalClient = required(source, 'EXTERNAL_ID_CLIENT_ID');
  if (!/^[A-Za-z0-9][A-Za-z0-9_.()-]{0,89}$/.test(group) || group.endsWith('.')) {
    throw new Error('AZURE_RESOURCE_GROUP must be a safe ASCII resource-group name, at most 90 characters.');
  }
  if (!/^[a-zA-Z0-9]{5,50}$/.test(registry)) {
    throw new Error('REGISTRY_NAME must contain 5 to 50 alphanumeric characters.');
  }
  if (!guid.test(apiClient) || !guid.test(externalClient)) {
    throw new Error('API_CLIENT_ID and EXTERNAL_ID_CLIENT_ID must be GUIDs.');
  }
  const authorityMatch = /^https:\/\/[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.ciamlogin\.com\/([^/]+)\/v2\.0$/.exec(authority);
  if (!authorityMatch || !guid.test(authorityMatch[1])) {
    throw new Error('EXTERNAL_ID_AUTHORITY must be HTTPS <subdomain>.ciamlogin.com/<tenant-GUID>/v2.0 without URL extras.');
  }
  return {
    AZURE_RESOURCE_GROUP: group,
    REGISTRY_NAME: registry,
    API_CLIENT_ID: apiClient,
    EXTERNAL_ID_AUTHORITY: authority,
    EXTERNAL_ID_CLIENT_ID: externalClient,
    ...validateWebAiConfig(source)
  };
}

export function validateRegistryServer(value) {
  const server = required({ REGISTRY_SERVER: value }, 'REGISTRY_SERVER');
  if (!/^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?\.azurecr\.io$/.test(server)) {
    throw new Error('REGISTRY_SERVER must be a single-label Azure public registry hostname.');
  }
  return server;
}

function run(command, environment) {
  let values;
  if (command === 'validate') values = validateDeploymentConfig(environment);
  else if (command === 'registry') values = { REGISTRY_SERVER: validateRegistryServer(environment.REGISTRY_SERVER) };
  else throw new Error('Expected deployment-config command validate or registry.');
  // Validate the entire input before the one append; invalid input never exports a partial configuration.
  const destination = required(environment, 'GITHUB_ENV');
  appendFileSync(destination, Object.entries(values).map(([name, value]) => `${name}=${value}\n`).join(''), 'utf8');
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  try { run(process.argv[2], process.env); }
  catch (error) { console.error(error.message); process.exitCode = 1; }
}

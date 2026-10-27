targetScope = 'resourceGroup'

@allowed(['swedencentral'])
param location string = 'swedencentral'
param environmentName string = 'cae-matchmate-dev'
param imageTag string
@minLength(36)
@maxLength(36)
param apiClientId string
param homeTenantId string = subscription().tenantId
param registryServer string
param externalIdAuthority string
@minLength(36)
@maxLength(36)
param externalIdClientId string
param storageAccountName string = 'stmmdev${uniqueString(subscription().id, resourceGroup().id)}'
param keyVaultName string = 'kv-mmdev-${uniqueString(subscription().id, resourceGroup().id)}'
param webSecretName string = 'matchmate-web-client-secret'
// Operator applies pipeline-access first; opt in only after the canonical pipeline source/review gates.
param pipelineEnabled bool = false
// Explicit Web-only inference opt-in. Workflow validates the complete nonsecret configuration.
param webAiEnabled bool = false
@maxLength(128)
param matchChatEndpoint string = ''
@maxLength(128)
param matchChatDeployment string = ''
@maxLength(128)
param decisionRoutingEndpoint string = ''
@maxLength(128)
param decisionRoutingDeployment string = ''
param cosmosAccountName string = 'cosmos-matchmate-dev-${uniqueString(subscription().id, resourceGroup().id)}'
param eventHubNamespaceName string = 'evhns-matchmate-dev-${uniqueString(subscription().id, resourceGroup().id)}'
@allowed([0, 1])
param workerMinReplicas int = 0

var tags = { project: 'Matchmate', environment: 'dev' }
var webName = 'mm-web-dev'
var apiName = 'mm-api-dev'
var workerName = 'mm-worker-dev'
resource environment 'Microsoft.App/managedEnvironments@2024-03-01' existing = { name: environmentName }
resource webIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: 'id-matchmate-web-dev' }
resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: 'id-matchmate-api-dev' }
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: 'id-matchmate-worker-dev' }
resource pullIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: 'id-matchmate-registry-pull' }
var publicOrigin = 'https://${webName}.${environment.properties.defaultDomain}'
var apiOrigin = 'https://${api.properties.configuration.ingress.fqdn}'
var processProbes = [
  { type: 'Startup', httpGet: { path: '/alive', port: 8080, scheme: 'HTTP' }, periodSeconds: 5, failureThreshold: 30 }
  { type: 'Readiness', httpGet: { path: '/alive', port: 8080, scheme: 'HTTP' }, periodSeconds: 10, failureThreshold: 3 }
  { type: 'Liveness', httpGet: { path: '/alive', port: 8080, scheme: 'HTTP' }, periodSeconds: 30, failureThreshold: 3 }
]
var webAiEnvironment = webAiEnabled ? [
  { name: 'MatchChat__Enabled', value: 'true' }
  { name: 'MatchChat__Endpoint', value: matchChatEndpoint }
  { name: 'MatchChat__Deployment', value: matchChatDeployment }
  { name: 'DecisionRouting__Enabled', value: 'true' }
  { name: 'DecisionRouting__Endpoint', value: decisionRoutingEndpoint }
  { name: 'DecisionRouting__Deployment', value: decisionRoutingDeployment }
] : [
  { name: 'MatchChat__Enabled', value: 'false' }
  { name: 'DecisionRouting__Enabled', value: 'false' }
]

resource web 'Microsoft.App/containerApps@2024-03-01' = {
  name: webName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: { '${webIdentity.id}': {}, '${pullIdentity.id}': {} }
  }
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: {
        external: true
        targetPort: 8080
        transport: 'auto'
        allowInsecure: false
        stickySessions: { affinity: 'sticky' }
      }
      registries: [{ server: registryServer, identity: pullIdentity.id }]
      secrets: [{ name: 'web-oidc-client-secret', keyVaultUrl: 'https://${keyVaultName}${az.environment().suffixes.keyvaultDns}/secrets/${webSecretName}', identity: webIdentity.id }]
    }
    template: {
      containers: [{
        name: 'web'
        image: '${registryServer}/matchmate/web:${imageTag}'
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: processProbes
        env: concat([
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'Runtime__Hosted', value: 'true' }
          { name: 'Runtime__ManagedIdentityClientId', value: webIdentity.properties.clientId }
          { name: 'Runtime__PublicOrigin', value: publicOrigin }
          { name: 'Runtime__DataProtectionBlobUri', value: 'https://${storageAccountName}.blob.${az.environment().suffixes.storage}/data-protection-keys/web.xml' }
          { name: 'ReplayApi__BaseUrl', value: apiOrigin }
          { name: 'ServiceAuthentication__ApiClientId', value: apiClientId }
          { name: 'Authentication__ExternalId__Authority', value: externalIdAuthority }
          { name: 'Authentication__ExternalId__ClientId', value: externalIdClientId }
          { name: 'Authentication__ExternalId__ClientSecret', secretRef: 'web-oidc-client-secret' }
        ], webAiEnvironment)
      }]
      scale: { minReplicas: 0, maxReplicas: 1 }
    }
  }
}

resource api 'Microsoft.App/containerApps@2024-03-01' = {
  name: apiName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${apiIdentity.id}': {}, '${pullIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: false, targetPort: 8080, transport: 'auto', allowInsecure: false }
      registries: [{ server: registryServer, identity: pullIdentity.id }]
    }
    template: {
      containers: [{
        name: 'api'
        image: '${registryServer}/matchmate/api:${imageTag}'
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: processProbes
        env: [
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'Runtime__Hosted', value: 'true' }
          { name: 'Runtime__ManagedIdentityClientId', value: apiIdentity.properties.clientId }
          { name: 'Pipeline__Enabled', value: string(pipelineEnabled) }
          { name: 'Pipeline__BlobEndpoint', value: 'https://${storageAccountName}.blob.${az.environment().suffixes.storage}/' }
          { name: 'Pipeline__CosmosEndpoint', value: 'https://${cosmosAccountName}.documents.azure.com:443/' }
          { name: 'Pipeline__Database', value: 'football' }
          { name: 'Pipeline__Container', value: 'replays' }
          { name: 'ServiceAuthentication__TenantId', value: homeTenantId }
          { name: 'ServiceAuthentication__ApiClientId', value: apiClientId }
          { name: 'ServiceAuthentication__WebClientId', value: webIdentity.properties.clientId }
        ]
      }]
      scale: { minReplicas: 0, maxReplicas: 1 }
    }
  }
}

resource worker 'Microsoft.App/containerApps@2025-01-01' = {
  name: workerName
  location: location
  tags: tags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity.id}': {}, '${pullIdentity.id}': {} } }
  properties: {
    managedEnvironmentId: environment.id
    workloadProfileName: 'Consumption'
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: registryServer, identity: pullIdentity.id }]
    }
    template: {
      containers: [{
        name: 'worker'
        image: '${registryServer}/matchmate/worker:${imageTag}'
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: processProbes
        env: [
          { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
          { name: 'ASPNETCORE_HTTP_PORTS', value: '8080' }
          { name: 'Runtime__Hosted', value: 'true' }
          { name: 'RecordedImport__Enabled', value: 'false' }
          { name: 'Runtime__ManagedIdentityClientId', value: workerIdentity.properties.clientId }
          { name: 'Pipeline__Enabled', value: string(pipelineEnabled) }
          { name: 'Pipeline__BlobEndpoint', value: 'https://${storageAccountName}.blob.${az.environment().suffixes.storage}/' }
          { name: 'Pipeline__CosmosEndpoint', value: 'https://${cosmosAccountName}.documents.azure.com:443/' }
          { name: 'Pipeline__Database', value: 'football' }
          { name: 'Pipeline__Container', value: 'replays' }
          { name: 'Pipeline__EventHubHost', value: '${eventHubNamespaceName}.servicebus.windows.net' }
          { name: 'Pipeline__EventHubName', value: 'match-observations' }
          { name: 'Pipeline__ConsumerGroup', value: 'matchmate-worker' }
        ]
      }]
      scale: {
        minReplicas: workerMinReplicas
        maxReplicas: 1
        rules: pipelineEnabled ? [{
          name: 'recorded-observations'
          custom: {
            type: 'azure-eventhub'
            identity: workerIdentity.id
            metadata: {
              eventHubNamespace: eventHubNamespaceName
              eventHubName: 'match-observations'
              consumerGroup: 'matchmate-worker'
              storageAccountName: storageAccountName
              blobContainer: 'eventhub-checkpoints'
              checkpointStrategy: 'blobMetadata'
              activationUnprocessedEventThreshold: '0'
              unprocessedEventThreshold: '64'
            }
          }
        }] : []
      }
    }
  }
}

output webOrigin string = publicOrigin
output apiOrigin string = apiOrigin
output imageTag string = imageTag

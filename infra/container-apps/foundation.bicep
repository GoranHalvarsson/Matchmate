targetScope = 'resourceGroup'

@allowed(['swedencentral'])
param location string = 'swedencentral'
param environmentName string = 'cae-matchmate-dev'
param workspaceName string = 'law-matchmate-dev'
param storageAccountName string = 'stmmdev${uniqueString(subscription().id, resourceGroup().id)}'
param keyVaultName string = 'kv-mmdev-${uniqueString(subscription().id, resourceGroup().id)}'
param webSecretName string = 'matchmate-web-client-secret'

var tags = { project: 'Matchmate', environment: 'dev' }
var identityNames = ['id-matchmate-web-dev', 'id-matchmate-api-dev', 'id-matchmate-worker-dev']

resource identities 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = [for name in identityNames: {
  name: name
  location: location
  tags: tags
}]

resource logs 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: workspaceName
  location: location
  tags: tags
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource environment 'Microsoft.App/managedEnvironments@2024-03-01' = {
  name: environmentName
  location: location
  tags: tags
  properties: {
    zoneRedundant: false
    workloadProfiles: [{ name: 'Consumption', workloadProfileType: 'Consumption' }]
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logs.properties.customerId
        sharedKey: logs.listKeys().primarySharedKey
      }
    }
  }
}

resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageAccountName }
resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = {
  parent: storage
  name: 'default'
}
resource keyContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobService
  name: 'data-protection-keys'
  properties: { publicAccess: 'None' }
}

var blobContributorRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
resource webKeyStorageRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyContainer.id, identities[0].id, blobContributorRole)
  scope: keyContainer
  properties: {
    principalId: identities[0].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: blobContributorRole
  }
}

resource vault 'Microsoft.KeyVault/vaults@2023-07-01' existing = { name: keyVaultName }
resource webSecret 'Microsoft.KeyVault/vaults/secrets@2023-07-01' existing = {
  parent: vault
  name: webSecretName
}
var secretsUserRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6')
resource webSecretRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(webSecret.id, identities[0].id, secretsUserRole)
  scope: webSecret
  properties: {
    principalId: identities[0].properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: secretsUserRole
  }
}

output environmentId string = environment.id
output defaultDomain string = environment.properties.defaultDomain
output webIdentityId string = identities[0].id
output webIdentityClientId string = identities[0].properties.clientId
output webIdentityPrincipalId string = identities[0].properties.principalId
output apiIdentityId string = identities[1].id
output apiIdentityClientId string = identities[1].properties.clientId
output workerIdentityId string = identities[2].id
output workerIdentityClientId string = identities[2].properties.clientId
output dataProtectionBlobUri string = '${storage.properties.primaryEndpoints.blob}data-protection-keys/web.xml'

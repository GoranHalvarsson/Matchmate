targetScope = 'resourceGroup'

param cosmosAccountName string = 'cosmos-matchmate-dev-${uniqueString(subscription().id, resourceGroup().id)}'
param databaseName string = 'football'
param storageAccountName string = 'stmmdev${uniqueString(subscription().id, resourceGroup().id)}'
param eventHubNamespaceName string = 'evhns-matchmate-dev-${uniqueString(subscription().id, resourceGroup().id)}'
param eventHubName string = 'match-observations'

resource worker 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: 'id-matchmate-worker-dev' }
resource api 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = { name: 'id-matchmate-api-dev' }
resource cosmos 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' existing = { name: cosmosAccountName }
resource database 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' existing = {
  parent: cosmos
  name: databaseName
}
resource replays 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: database
  name: 'replays'
  properties: {
    resource: {
      id: 'replays'
      partitionKey: { paths: ['/datasetId'], kind: 'Hash', version: 2 }
      indexingPolicy: {
        automatic: true
        indexingMode: 'consistent'
        includedPaths: [{ path: '/datasetId/?' }, { path: '/discriminator/?' }, { path: '/publicationRevision/?' }, { path: '/version/?' }]
        excludedPaths: [{ path: '/*' }]
      }
    }
  }
}
var replayScope = '${cosmos.id}/dbs/${databaseName}/colls/${replays.name}'
resource workerCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, worker.id, replayScope, 'contributor')
  properties: {
    principalId: worker.properties.principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: replayScope
  }
}
resource apiCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-05-15' = {
  parent: cosmos
  name: guid(cosmos.id, api.id, replayScope, 'reader')
  properties: {
    principalId: api.properties.principalId
    roleDefinitionId: '${cosmos.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000001'
    scope: replayScope
  }
}
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' existing = { name: storageAccountName }
resource blobs 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' existing = { parent: storage, name: 'default' }
resource recordings 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { parent: blobs, name: 'recordings' }
resource checkpoints 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' existing = { parent: blobs, name: 'eventhub-checkpoints' }
var blobContributor = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
var blobReader = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '2a2b9908-6ea1-4ae2-8e65-a410df84e7d1')
resource workerRecordings 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(recordings.id, worker.id, blobContributor)
  scope: recordings
  properties: { principalId: worker.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
}
resource workerCheckpoints 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(checkpoints.id, worker.id, blobContributor)
  scope: checkpoints
  properties: { principalId: worker.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: blobContributor }
}
resource apiRecordings 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(recordings.id, api.id, blobReader)
  scope: recordings
  properties: { principalId: api.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: blobReader }
}
resource eventNamespace 'Microsoft.EventHub/namespaces@2024-01-01' existing = { name: eventHubNamespaceName }
resource observations 'Microsoft.EventHub/namespaces/eventhubs@2024-01-01' existing = { parent: eventNamespace, name: eventHubName }
var receiverRole = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a638d3c7-ab3a-418d-83e6-5f17a39d4fde')
resource workerReceiver 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(observations.id, worker.id, receiverRole)
  scope: observations
  properties: { principalId: worker.properties.principalId, principalType: 'ServicePrincipal', roleDefinitionId: receiverRole }
}

output replayContainerId string = replays.id

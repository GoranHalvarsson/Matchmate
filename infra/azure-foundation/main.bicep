targetScope = 'subscription'

@description('Sweden Central is the approved resource and deployment region for this foundation.')
@allowed(['swedencentral'])
param location string = 'swedencentral'

@description('New dedicated Matchmate resource group.')
param resourceGroupName string = 'rg-matchmate-dev-swedencentral'

@description('Microsoft Entra object ID of the local developer user; never an application/client ID.')
@minLength(36)
@maxLength(36)
param operatorObjectId string

var tags = {
  project: 'Matchmate'
  environment: 'dev'
}

module matchmateResourceGroup 'br/public:avm/res/resources/resource-group:0.4.1' = {
  name: 'matchmate-resource-group'
  params: {
    name: resourceGroupName
    location: location
    tags: tags
    enableTelemetry: false
  }
}

module foundation 'resources.bicep' = {
  name: 'matchmate-resource-foundation'
  scope: resourceGroup(resourceGroupName)
  params: {
    location: location
    operatorObjectId: operatorObjectId
    tags: tags
  }
  dependsOn: [matchmateResourceGroup]
}

output resourceGroupId string = matchmateResourceGroup.outputs.resourceId
output cosmosAccountId string = foundation.outputs.cosmosAccountId
output cosmosEndpoint string = foundation.outputs.cosmosEndpoint
output storageAccountId string = foundation.outputs.storageAccountId
output blobEndpoint string = foundation.outputs.blobEndpoint
output eventHubsNamespaceId string = foundation.outputs.eventHubsNamespaceId
output eventHubsNamespaceHost string = foundation.outputs.eventHubsNamespaceHost
output keyVaultId string = foundation.outputs.keyVaultId
output keyVaultUri string = foundation.outputs.keyVaultUri

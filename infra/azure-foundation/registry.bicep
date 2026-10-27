targetScope = 'resourceGroup'

@allowed(['swedencentral'])
param location string = 'swedencentral'

@description('Dedicated Matchmate registry; deploy only in rg-matchmate-dev-swedencentral.')
param containerRegistryName string

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' = {
  name: containerRegistryName
  location: location
  tags: { project: 'Matchmate', environment: 'dev' }
  sku: { name: 'Basic' }
  properties: {
    adminUserEnabled: false
    anonymousPullEnabled: false
    publicNetworkAccess: 'Enabled'
    roleAssignmentMode: 'LegacyRegistryPermissions'
    policies: {
      azureADAuthenticationAsArmPolicy: { status: 'enabled' }
    }
  }
}

// Operator bootstrap only: release CI cannot create registries or assign roles.
module access './registry-access.bicep' = {
  name: '${deployment().name}-access'
  params: { containerRegistryName: registry.name }
}

output registryId string = registry.id
output registryServer string = registry.properties.loginServer
output pullIdentityId string = access.outputs.identityId
output publisherIdentityId string = access.outputs.publisherIdentityId

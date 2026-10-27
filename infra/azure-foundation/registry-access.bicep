targetScope = 'resourceGroup'

@description('Deploy at rg-matchmate-dev-swedencentral; this template grants access to its existing dedicated registry.')
param containerRegistryName string

@description('Existing Matchmate pull identity, separate from application service identities.')
param pullIdentityName string = 'id-matchmate-registry-pull'

@description('Existing GitHub OIDC publisher identity; no role-write permission is granted.')
param publisherIdentityName string = 'id-matchmate-github-dev'

var acrPullRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
var acrPushRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8311e382-0749-4cb8-b61a-304f252e45ec')
var readerRoleDefinitionId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'acdd72a7-3385-48ef-bd42-f606fba81ae7')

resource pullIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: pullIdentityName
}

resource publisherIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' existing = {
  name: publisherIdentityName
}

resource registry 'Microsoft.ContainerRegistry/registries@2025-11-01' existing = {
  name: containerRegistryName
}

resource pullAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, pullIdentity.id, acrPullRoleDefinitionId)
  scope: registry
  properties: {
    roleDefinitionId: acrPullRoleDefinitionId
    principalId: pullIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource publisherPushAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, publisherIdentity.id, acrPushRoleDefinitionId)
  scope: registry
  properties: {
    roleDefinitionId: acrPushRoleDefinitionId
    principalId: publisherIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

resource publisherReaderAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(registry.id, publisherIdentity.id, readerRoleDefinitionId)
  scope: registry
  properties: {
    roleDefinitionId: readerRoleDefinitionId
    principalId: publisherIdentity.properties.principalId
    principalType: 'ServicePrincipal'
  }
}

output identityId string = pullIdentity.id
output identityClientId string = pullIdentity.properties.clientId
output identityPrincipalId string = pullIdentity.properties.principalId
output roleAssignmentId string = pullAssignment.id
output publisherIdentityId string = publisherIdentity.id
output publisherPrincipalId string = publisherIdentity.properties.principalId
output publisherRoleAssignmentIds array = [publisherPushAssignment.id, publisherReaderAssignment.id]

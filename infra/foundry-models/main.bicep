targetScope = 'resourceGroup'

@description('Use the existing Matchmate Sweden Central resource group. No app resources are deployed here.')
@allowed(['swedencentral'])
param location string = 'swedencentral'

@description('Dedicated deterministic Foundry account/custom subdomain; must be globally available.')
@minLength(2)
@maxLength(64)
param accountName string = 'mm-foundry-dev-${uniqueString(subscription().id, resourceGroup().id)}'

@description('Local operator Entra user object ID, supplied by the coordinator; never a client/application ID.')
@minLength(36)
@maxLength(36)
param operatorObjectId string

@description('Optional existing Web managed-identity principal object ID. Empty skips the assignment.')
@maxLength(36)
param webPrincipalObjectId string = ''

var inferenceRoleId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
var tags = {
  project: 'Matchmate'
  environment: 'dev'
}

resource account 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: accountName
  location: location
  kind: 'AIServices'
  identity: { type: 'SystemAssigned' }
  sku: { name: 'S0' }
  tags: tags
  properties: {
    allowProjectManagement: true
    customSubDomainName: accountName
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
    dynamicThrottlingEnabled: false
  }
}

resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: account
  name: 'matchmate'
  location: location
  identity: { type: 'SystemAssigned' }
  tags: tags
  properties: {
    displayName: 'Matchmate'
    description: 'Bounded Decision-1 routing and Luna narration for Matchmate.'
  }
}

resource decision 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: 'matchmate-decision-1'
  sku: { name: 'DataZoneStandard', capacity: 30 }
  properties: {
    model: { format: 'Microsoft', name: 'Microsoft-Decision-1', version: '1' }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [project]
}

resource narrator 'Microsoft.CognitiveServices/accounts/deployments@2025-06-01' = {
  parent: account
  name: 'matchmate-luna'
  sku: { name: 'DataZoneStandard', capacity: 30 }
  properties: {
    model: { format: 'OpenAI', name: 'gpt-6-luna', version: '2026-09-22' }
    versionUpgradeOption: 'NoAutoUpgrade'
  }
  dependsOn: [decision]
}

// Cognitive Services User is scoped only to this dedicated account. Local key authentication is disabled.
resource operatorInference 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(account.id, operatorObjectId, inferenceRoleId)
  scope: account
  properties: { principalId: operatorObjectId, principalType: 'User', roleDefinitionId: inferenceRoleId }
}

resource webInference 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (!empty(webPrincipalObjectId)) {
  name: guid(account.id, webPrincipalObjectId, inferenceRoleId)
  scope: account
  properties: { principalId: webPrincipalObjectId, principalType: 'ServicePrincipal', roleDefinitionId: inferenceRoleId }
}

output accountId string = account.id
output accountName string = account.name
output projectId string = project.id
output decisionDeploymentName string = decision.name
output narratorDeploymentName string = narrator.name

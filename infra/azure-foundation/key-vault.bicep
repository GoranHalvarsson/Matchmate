targetScope = 'resourceGroup'

@description('Approved Sweden Central region for this additive vault deployment.')
@allowed(['swedencentral'])
param location string = 'swedencentral'

@description('Microsoft Entra object ID of the local developer user; not an application/client ID.')
@minLength(36)
@maxLength(36)
param operatorObjectId string

@description('Project and environment tags.')
param tags object = {
  project: 'Matchmate'
  environment: 'dev'
}

// Matches the whole foundation: identity depends only on subscription and resource group.
var suffix = uniqueString(subscription().id, resourceGroup().id)
var keyVaultName = 'kv-mmdev-${suffix}'

module vault 'br/public:avm/res/key-vault/vault:0.12.1' = {
  name: 'key-vault-resource'
  params: {
    name: keyVaultName
    location: location
    tags: tags
    enableTelemetry: false
    sku: 'standard'
    enableRbacAuthorization: true
    accessPolicies: []
    enableSoftDelete: true
    softDeleteRetentionInDays: 7
    enablePurgeProtection: true
    enableVaultForDeployment: false
    enableVaultForDiskEncryption: false
    enableVaultForTemplateDeployment: false
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
    secrets: []
    keys: []
    roleAssignments: [
      {
        principalId: operatorObjectId
        principalType: 'User'
        roleDefinitionIdOrName: 'Key Vault Secrets Officer'
      }
    ]
  }
}

output keyVaultId string = vault.outputs.resourceId
output keyVaultUri string = vault.outputs.uri

targetScope = 'resourceGroup'

@description('Approved resource region.')
param location string

@description('Microsoft Entra object ID of the local developer user.')
param operatorObjectId string

@description('Project and environment tags.')
param tags object

// Subscription + new resource group keep global names deterministic and project-specific.
var suffix = uniqueString(subscription().id, resourceGroup().id)
var cosmosName = 'cosmos-matchmate-dev-${suffix}'
var storageName = 'stmmdev${suffix}'
var eventHubsName = 'evhns-matchmate-dev-${suffix}'

// Pins intentionally predate the AVM releases that unconditionally emit key/connection outputs.
module cosmos 'br/public:avm/res/document-db/database-account:0.13.0' = {
  name: 'cosmos-foundation'
  params: {
    name: cosmosName
    location: location
    tags: tags
    enableTelemetry: false
    capabilitiesToAdd: ['EnableServerless']
    defaultConsistencyLevel: 'Session'
    disableLocalAuth: true
    disableKeyBasedMetadataWriteAccess: true
    automaticFailover: false
    enableMultipleWriteLocations: false
    enableFreeTier: false
    enableAnalyticalStorage: false
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    minimumTlsVersion: 'Tls12'
    networkRestrictions: {
      publicNetworkAccess: 'Enabled'
      networkAclBypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
    backupPolicyType: 'Periodic'
    backupIntervalInMinutes: 240
    backupRetentionIntervalInHours: 8
    backupStorageRedundancy: 'Local'
    // No provisioned RU/s/autoscale and no containers: those require the later data model.
    sqlDatabases: [
      { name: 'football' }
      { name: 'personal' }
    ]
  }
}

// This AVM version supports custom role definitions; use the native child for the built-in role.
resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' existing = {
  name: cosmosName
}
resource cosmosOperator 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: cosmosAccount
  name: guid(cosmosAccount.id, operatorObjectId, '00000000-0000-0000-0000-000000000002')
  properties: {
    principalId: operatorObjectId
    roleDefinitionId: '${cosmosAccount.id}/sqlRoleDefinitions/00000000-0000-0000-0000-000000000002'
    scope: cosmosAccount.id
  }
  dependsOn: [cosmos]
}

module storage 'br/public:avm/res/storage/storage-account:0.19.0' = {
  name: 'storage-foundation'
  params: {
    name: storageName
    location: location
    tags: tags
    enableTelemetry: false
    kind: 'StorageV2'
    skuName: 'Standard_LRS'
    accessTier: 'Hot'
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    defaultToOAuthAuthentication: true
    publicNetworkAccess: 'Enabled'
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'None'
      ipRules: []
      virtualNetworkRules: []
    }
    blobServices: {
      containerDeleteRetentionPolicyEnabled: true
      containerDeleteRetentionPolicyDays: 7
      deleteRetentionPolicyEnabled: true
      deleteRetentionPolicyDays: 7
      isVersioningEnabled: false
      changeFeedEnabled: false
      containers: [
        { name: 'recordings', publicAccess: 'None' }
        { name: 'eventhub-checkpoints', publicAccess: 'None' }
      ]
    }
    roleAssignments: [
      {
        principalId: operatorObjectId
        principalType: 'User'
        roleDefinitionIdOrName: 'Storage Blob Data Contributor'
      }
    ]
  }
}

module eventHubs 'br/public:avm/res/event-hub/namespace:0.11.0' = {
  name: 'eventhubs-foundation'
  params: {
    name: eventHubsName
    location: location
    tags: tags
    enableTelemetry: false
    skuName: 'Standard'
    skuCapacity: 1
    isAutoInflateEnabled: false
    maximumThroughputUnits: 1
    disableLocalAuth: true
    authorizationRules: []
    kafkaEnabled: false
    minimumTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    networkRuleSets: {
      defaultAction: 'Allow'
      publicNetworkAccess: 'Enabled'
      trustedServiceAccessEnabled: false
      ipRules: []
      virtualNetworkRules: []
    }
    eventhubs: [
      {
        name: 'match-observations'
        partitionCount: 2
        messageRetentionInDays: 1
        retentionDescriptionEnabled: false
        captureDescriptionEnabled: false
        authorizationRules: []
        consumergroups: [{ name: 'matchmate-worker' }]
      }
    ]
    roleAssignments: [
      {
        principalId: operatorObjectId
        principalType: 'User'
        roleDefinitionIdOrName: 'Azure Event Hubs Data Sender'
      }
      {
        principalId: operatorObjectId
        principalType: 'User'
        roleDefinitionIdOrName: 'Azure Event Hubs Data Receiver'
      }
    ]
  }
}

module keyVault 'key-vault.bicep' = {
  name: 'key-vault-foundation'
  params: {
    location: location
    operatorObjectId: operatorObjectId
    tags: tags
  }
}

output cosmosAccountId string = cosmos.outputs.resourceId
output cosmosEndpoint string = cosmos.outputs.endpoint
output storageAccountId string = storage.outputs.resourceId
output blobEndpoint string = storage.outputs.primaryBlobEndpoint
output eventHubsNamespaceId string = eventHubs.outputs.resourceId
output eventHubsNamespaceHost string = '${eventHubsName}.servicebus.windows.net'
output keyVaultId string = keyVault.outputs.keyVaultId
output keyVaultUri string = keyVault.outputs.keyVaultUri

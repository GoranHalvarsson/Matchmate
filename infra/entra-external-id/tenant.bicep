targetScope = 'resourceGroup'

@description('Required globally available tenant subdomain, 1-26 alphanumeric characters only; no onmicrosoft.com suffix. Validate characters in the documented preflight.')
@minLength(1)
@maxLength(26)
param tenantSubdomain string

// CIAM data residency uses geography names; this is not the Sweden Central compute region.
// Standalone long-lived identity resource: never include in runtime/Event Hubs teardown.
resource customerTenant 'Microsoft.AzureActiveDirectory/ciamDirectories@2023-05-17-preview' = {
  // Live ARM validation requires the domain suffix despite the older bare-prefix reference.
  name: '${tenantSubdomain}.onmicrosoft.com'
  location: 'Europe'
  sku: {
    // Live ARM validation rejects the published Standard value and requires Base/A0.
    name: 'Base'
    tier: 'A0'
  }
  tags: {
    project: 'Matchmate'
    environment: 'dev'
    lifecycle: 'persistent'
  }
  properties: {
    createTenantProperties: {
      displayName: 'Matchmate Customers'
      countryCode: 'SE'
    }
  }
}

output tenantId string = customerTenant.properties.tenantId
output tenantDomain string = customerTenant.name
output resourceId string = customerTenant.id

# Deployment configuration and public source

The GitHub **development** environment owns nonsecret deployment configuration. Required values have no inline workflow fallback:

| Variable | Purpose |
| --- | --- |
| AZURE_RESOURCE_GROUP | Existing Matchmate resource group |
| REGISTRY_NAME | Existing dedicated registry name |
| API_CLIENT_ID | Internal API audience |
| EXTERNAL_ID_AUTHORITY | HTTPS single-subdomain CIAM authority with tenant GUID and /v2.0 |
| EXTERNAL_ID_CLIENT_ID | Customer sign-in application ID |
| AZURE_CLIENT_ID | Existing federated publisher identity |
| AZURE_TENANT_ID | Publisher's home tenant |
| AZURE_SUBSCRIPTION_ID | Target subscription |

The coordinator configured/read back the four new variables on 2026-10-24 and preserved the existing API/OIDC values. These eight identifiers are configuration, not credentials. Do not create a REGISTRY_SERVER variable: after Azure login, the workflow reads the registry's actual loginServer, validates its Azure public hostname (including hashed labels), and exports it once for all image pushes and ARM parameters. Configuration validation rejects missing/malformed values and controls before any partial export or cloud write.

The workflow retains its validator before checking out selected release source. External ID parameters are supplied only when that selected Bicep declares both; historical previews retain their historical public identity defaults. Release source, ancestry/order guards and CI permissions are unchanged.

Current app Bicep requires registryServer, externalIdAuthority and externalIdClientId alongside its existing API/image inputs. Both registry bootstrap/access entry points require containerRegistryName. Hosting account defaults use the original uniqueString(subscription().id, resourceGroup().id) formula and prefixes; home tenant defaults to subscription().tenantId. Operator ARM calls must pass the required parameters explicitly. Registry provisioning and access changes require a separately reviewed operator plan.

The reviewed scope is the **10 current Bicep templates**, which contain no embedded credential values. Resource/tenant/client identifiers and role IDs are public metadata. Actual credentials stay in Key Vault/local secret storage; GitHub OIDC and managed identities remain in use. Local Web appsettings and administrative scripts retain public identifiers to preserve Visual Studio behavior. This is not a whole-repository or Git-history secret audit, a repository-visibility change, or a declaration that publication is cleared. The repository remains private; the user controls visibility.

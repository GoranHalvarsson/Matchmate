# Matchmate customer identity tenant

**Tenant and selected-tester directory/OTP-flow configuration are live and verified; local Web OIDC integration is implemented, with email OTP/callback/Tester replay verified on the initial build; initial-build playback/player interaction and logout plus final-build anonymous sign-in smoke are verified; latest fixes have automated coverage.** Standard sign-in scopes/consent, HTTPS 7410 callbacks and a 90-day Web credential are now configured. See [current local integration](../../docs/LOCAL-DEVELOPMENT.md#email-sign-in-configuration-and-boundary). The initial `Set-MatchmateTesterIdentity.ps1` assertions predate these changes and intentionally fail closed: do not rerun it unchanged. Standalone Bicep creates the long-lived tenant/billing resource; retain it independently of disposable runtime/Event Hubs.

Required `tenantSubdomain` is a globally available **1–26 character alphanumeric prefix**, with no domain suffix and no default. The reviewed deployment `matchmate-external-id-20261018` exited 0, Succeeded/error null; the local deployment receipt is not included here. Domain `matchmatefans.onmicrosoft.com`, tenant ID `519cfcb5-8944-4028-a4ef-22d6beecccb9`, original subscription/group, display name/country and Graph organization `tenantType=CIAM` were verified. Display name is `Matchmate Customers`; country is `SE`; identity data geography is **Europe**, not the Sweden Central compute region. Outputs are tenant ID, tenant domain and Azure resource ID only; no credentials.

Use the existing Matchmate subscription and resource group with the reviewed standalone template. The initial tester identity is supplied privately; keep tester email and personal identifiers outside tracked files.

## Provider corrections and direct resource

The [published 2023 CIAM schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.azureactivedirectory/2023-05-17-preview/ciamdirectories) lists Standard/A0 and a bare-prefix resource-name constraint. Actual ARM preflight on 2026-10-18 rejected Standard and required **Base/A0**, then rejected an empty domain suffix and required **`<prefix>.onmicrosoft.com`**. The template follows those provider diagnostics, retaining API `2023-05-17-preview`. Local Bicep 0.33.13 compiles with exit 0; **BCP335** reflects the older maximum-name-length schema, not permission to skip ARM validation. The prefix remains bounded to 26; the full resource name includes the suffix. Coordinator ARM validation now succeeded/error null; what-if succeeded with 1 Create/4 Ignores/0 Modify/Delete. Independent predeployment review passed and tenant creation/live verification completed.

No CIAM directory module was found in the official [AVM catalog](https://azure.github.io/Azure-Verified-Modules/indexes/bicep/bicep-resource-modules/), so this uses the documented direct Azure resource. It is separate from `infra/azure-foundation` and does not change its services.

## Administrator browser bootstrap
**Live status 2026-10-18:** setup-app preparation and delegated consent succeeded. **Process context is not a permanent SDK fix:** one connection completed in 8.5 seconds and verified identity-provider access, but a later fresh process reopened the account selector and did not retain a usable context. The completed customer setup used the existing authorized Azure CLI token for independently reviewed directory operations and a separately reviewed flow POST, without another login. The setup service principal is now assignment-required with only the current administrator assigned; no directory role was granted.

[Connect-MatchmateAdmin.ps1](Connect-MatchmateAdmin.ps1) contains the reviewed diagnostic/bootstrap approach for Graph PowerShell 2.41.2 device-code timeouts and WAM window-handle failures; interactive connection reliability remains unresolved. It uses the existing Azure CLI token for the explicit customer tenant, verifies the CIAM organization, discovers delegated scope IDs from the Graph service principal and creates/reuses only **Matchmate Setup Administrator**. Ambiguous matches/wrong tenant/unexpected credential or app platforms stop execution. The single-tenant public client has localhost redirect, no secret, exposed app roles or implicit issuance. It is an operator setup app, not the customer application. The commands below describe that path, not a request to repeat sign-in for the completed setup.

```powershell
# Creates/updates only the setup app declaration; does not launch authentication or grant consent.
pwsh -File infra/entra-external-id/Connect-MatchmateAdmin.ps1 -PrepareOnly

# In an existing PowerShell 7 session, connect and keep that process open.
# Browser sign-in/consent opens only if needed.
. ./infra/entra-external-id/Connect-MatchmateAdmin.ps1
# Run the reviewed provisioning commands in THIS SAME PowerShell process afterward.
```

Prerequisites: Azure CLI already signed into tenant `519cfcb5-8944-4028-a4ef-22d6beecccb9` with app-registration rights, and Microsoft.Graph.Authentication **2.41.2** installed. Declared/requested delegated permissions are EventListener.ReadWrite.All, IdentityProvider.Read.All, Policy.ReadWrite.AuthenticationMethod, Application.ReadWrite.All, User.ReadWrite.All and AppRoleAssignment.ReadWrite.All. The script does not grant admin consent through Graph or create a service principal; an authorized administrator reviews the browser consent. `-PrepareOnly` performs app creation/update, not a read-only dry run.

[Official Set-MgGraphOption guidance](https://learn.microsoft.com/en-us/powershell/module/microsoft.graph.authentication/set-mggraphoption?view=graph-powershell-1.0) says disabling WAM works only with a custom ClientId. The script temporarily sets that option and restores its previous value in `finally`, connects with **Process** context, verifies tenant/client/scopes and performs a read-only identity-provider request before reporting success. It prints selected non-secret context fields only, never tokens or account emails. Process context ends with the PowerShell process: a separate `pwsh -File` invocation cannot provide an authenticated session to later commands in another process. A working SDK connection and subsequent commands must share one process. No global cache deletion or SDK downgrade was performed; reliable interactive reconnection remains unverified.

## Selected tester configuration

**Live configuration verified 2026-10-18:** eleven directory checks and four flow checks passed after the reviewed CLI operations; the local configuration receipt is not included here. Matchmate Web client ID is `cec5d1b2-a924-4e11-906e-a82823d539ed`, application object `cce55281-1566-4b6f-aa91-bbba5397c552`, service principal `1a0d4e6d-7e75-405e-9577-09a0db0e419d`. Tester email, user ID and assignment ID remain outside tracked documentation.

[Set-MatchmateTesterIdentity.ps1](Set-MatchmateTesterIdentity.ps1) received source review and simulated checks but **was not run live end to end**. The actual live path used the existing CLI token, including its `Directory.AccessAsUser.All` permission for the flow operations; absence of the narrowly named EventListener/IdentityProvider scopes did not prevent those verified operations. The script retains a separate same-process SDK path and mandatory private tester parameters. Do not attribute the CLI result to executing this script or repeat provisioning merely to exercise it.

The initial directory read-back verified the single-tenant confidential Web app, no implicit flow/public-client setting, and assignment required with exactly the selected Tester. The enabled Member customer uses federated `mail` and has no directory roles. **Matchmate Email Sign In** includes only this app/`EmailOtpSignup-OAUTH`, with public sign-up disabled; the email policy was enabled/default and unchanged. **Later integration supersedes the initial callback/credential/permission state:** HTTPS 7410 callbacks were added (HTTP 5410 retained), one 90-day credential was stored in Key Vault/local user secrets, and only standard Graph OIDC `openid/profile/email` declarations plus delegated consent were added. These permit sign-in, not Graph data access. The first real OTP step reached admin approval; the minimal consent correction was verified. The user then completed email OTP and returned to HTTPS7410 with protected replay/Tester preview. Initial-build playback/player interaction and POST logout through Microsoft account selection back to sign-in passed; final-build restart then passed anonymous redirect/public-sign-in smoke without another OTP. Coordinator independently passed 36 tests; reviewer passed 13 auth cases with no code/security findings.

Schemas follow the official [Graph flow creation](https://learn.microsoft.com/en-us/graph/api/identitycontainer-post-authenticationeventsflows?view=graph-rest-1.0), [email policy](https://learn.microsoft.com/en-us/graph/api/resources/emailauthenticationmethodconfiguration?view=graph-rest-1.0) and [user creation](https://learn.microsoft.com/en-us/graph/api/user-post-users?view=graph-rest-1.0) documentation. The selected federated `mail` identity without a password also follows the [reported CIAM OTP migration workflow](https://learn.microsoft.com/en-us/answers/questions/5592937/ciam-multiple-issues-migrating-users-using-otp-flo); that report is supporting evidence, not proof of successful Matchmate sign-in.

## Preflight and deployment

The coordinator performs live operations. Check the required prefix before using the existing subscription/group; local compilation alone proves neither availability nor tenant creation. Use incremental group deployment, not whole-group deletion or Complete mode. Compilation uses stdout so generated JSON does not enter source.

```powershell
$subscriptionId = 'f296c67c-af5b-48e6-9b42-953224b0e69f'
$resourceGroupName = 'rg-matchmate-dev-swedencentral'
$tenantSubdomain = 'matchmatefans'
if ($tenantSubdomain -cnotmatch '^[A-Za-z0-9]{1,26}$') { throw 'Invalid tenant prefix.' }
$tenantParameters = @("tenantSubdomain=$tenantSubdomain")

az bicep build --file infra/entra-external-id/tenant.bicep --stdout | Out-Null
az deployment group validate --subscription $subscriptionId --resource-group $resourceGroupName --name matchmate-customer-tenant --template-file infra/entra-external-id/tenant.bicep --parameters $tenantParameters
az deployment group what-if --subscription $subscriptionId --resource-group $resourceGroupName --name matchmate-customer-tenant --template-file infra/entra-external-id/tenant.bicep --parameters $tenantParameters

# After validation, scoped preview and independent review.
az deployment group create --subscription $subscriptionId --resource-group $resourceGroupName --name matchmate-customer-tenant --mode Incremental --template-file infra/entra-external-id/tenant.bicep --parameters $tenantParameters
```

User direction is email sign-in for selected testers. The directory/OTP-flow configuration above is now live; this Bicep itself creates only the tenant resource. It does not implement browser/API OIDC integration or prove a successful customer login. Personal tester identifiers stay outside tracked documentation. Service managed identities and end-user identity remain separate.

Bicep reproduces infrastructure configuration; it is not an identity/customer-data backup or a substitute for preserving tenant/Graph configuration and recovery access. No tenant deletion or standby action was performed here.





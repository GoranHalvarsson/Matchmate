#Requires -Version 7.2
[CmdletBinding()]
param([switch]$PrepareOnly)

$ErrorActionPreference = 'Stop'
$tenantId = '519cfcb5-8944-4028-a4ef-22d6beecccb9'
$appName = 'Matchmate Setup Administrator'
$graphAppId = '00000003-0000-0000-c000-000000000000'
$scopes = @(
    'EventListener.ReadWrite.All'
    'IdentityProvider.Read.All'
    'Policy.ReadWrite.AuthenticationMethod'
    'Application.ReadWrite.All'
    'User.ReadWrite.All'
    'AppRoleAssignment.ReadWrite.All'
)
Import-Module Microsoft.Graph.Authentication -RequiredVersion 2.41.2

function Invoke-BootstrapGraph {
    param([string]$Method = 'GET', [string]$Path, [object]$Body)
    $request = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0/$Path"; Headers = $script:bootstrapHeaders; ErrorAction = 'Stop' }
    if ($null -ne $Body) { $request.ContentType = 'application/json'; $request.Body = ConvertTo-Json -InputObject $Body -Depth 20 -Compress }
    try {
        $response = Invoke-RestMethod @request
        if ($null -ne $response) { return ($response | ConvertTo-Json -Depth 40 | ConvertFrom-Json -AsHashtable) }
    }
    catch { throw "Graph bootstrap $Method failed for the requested setup resource. Check CLI tenant, delegated permissions and administrator rights; no token or response body is printed." }
}

try {
    # Capture CLI credentials in memory only; never write the token or raw CLI response to output.
    $cliText = & az account get-access-token --tenant $tenantId --resource-type ms-graph --output json --only-show-errors 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'Azure CLI could not obtain a Graph token for the Matchmate customer tenant. Sign into that tenant first.' }
    try { $cliToken = $cliText | ConvertFrom-Json -AsHashtable } catch { throw 'CLI returned an invalid token response; its contents are not printed.' }
    if ($cliToken.tenant -ne $tenantId -or [string]::IsNullOrWhiteSpace($cliToken.accessToken)) { throw 'CLI token tenant mismatch or missing token; bootstrap stopped.' }
    $script:bootstrapHeaders = @{ Authorization = "Bearer $($cliToken.accessToken)" }
    $organization = Invoke-BootstrapGraph -Path 'organization?$select=id,displayName,tenantType'
    if (@($organization.value).Count -ne 1 -or $organization.value[0].id -ne $tenantId -or $organization.value[0].tenantType -ne 'CIAM') { throw 'The Graph organization is not the expected Matchmate CIAM tenant; bootstrap stopped.' }

    $graphFilter = [Uri]::EscapeDataString("appId eq '$graphAppId'")
    $graphResult = Invoke-BootstrapGraph -Path "servicePrincipals?`$filter=$graphFilter&`$select=id,appId,oauth2PermissionScopes&`$top=2"
    if (@($graphResult.value).Count -ne 1 -or $graphResult['@odata.nextLink']) { throw 'Microsoft Graph service principal lookup is missing or ambiguous.' }
    $resourceAccess = @(foreach ($scope in $scopes) {
        $permissionMatches = @($graphResult.value[0].oauth2PermissionScopes | Where-Object { $_.value -ceq $scope -and $_.isEnabled -eq $true })
        if ($permissionMatches.Count -ne 1 -or -not [guid]::TryParse([string]$permissionMatches[0].id, [ref]([guid]::Empty))) { throw "Required delegated Graph permission is unavailable or ambiguous: $scope" }
        @{ id = $permissionMatches[0].id; type = 'Scope' }
    })

    $appFilter = [Uri]::EscapeDataString("displayName eq '$appName'")
    $appResult = Invoke-BootstrapGraph -Path "applications?`$filter=$appFilter&`$select=id,appId,displayName,signInAudience,publicClient,web,spa,api,appRoles,keyCredentials,passwordCredentials&`$top=2"
    if (@($appResult.value).Count -gt 1 -or $appResult['@odata.nextLink']) { throw 'Multiple setup applications match the exact target name; resolve ambiguity manually.' }
    $app = if (@($appResult.value).Count -eq 1) { $appResult.value[0] } else { $null }
    if ($app) {
        if ($app.displayName -cne $appName -or $app.signInAudience -ne 'AzureADMyOrg') { throw 'Existing setup application name/audience is unexpected; no changes made.' }
        if (@(@($app.passwordCredentials; $app.keyCredentials; $app.appRoles; $app.api.oauth2PermissionScopes; $app.web.redirectUris; $app.spa.redirectUris) | Where-Object { $null -ne $_ }).Count) { throw 'Existing setup application has credentials, roles, API scopes or other redirect platforms; review it manually.' }
    }
    $configuration = @{
        displayName = $appName
        signInAudience = 'AzureADMyOrg'
        isFallbackPublicClient = $true
        publicClient = @{ redirectUris = @('http://localhost') }
        web = @{ redirectUris = @(); implicitGrantSettings = @{ enableIdTokenIssuance = $false; enableAccessTokenIssuance = $false } }
        spa = @{ redirectUris = @() }
        appRoles = @()
        requiredResourceAccess = @(@{ resourceAppId = $graphAppId; resourceAccess = $resourceAccess })
    }
    if ($app) {
        $null = Invoke-BootstrapGraph -Method PATCH -Path "applications/$($app.id)" -Body $configuration
    }
    else { $app = Invoke-BootstrapGraph -Method POST -Path 'applications' -Body $configuration }
    if (-not $app.id -or -not $app.appId) { throw 'Setup application response did not include its object/client IDs.' }
}
finally {
    if ($script:bootstrapHeaders) { $script:bootstrapHeaders.Clear(); $script:bootstrapHeaders = $null }
    $cliText = $null; $cliToken = $null
}

if ($PrepareOnly) {
    [pscustomobject]@{ Status = 'Prepared; delegated permissions declared, consent not granted'; TenantId = $tenantId; ClientId = $app.appId; AppObjectId = $app.id; Scopes = $scopes; PrepareOnly = $true }
    return
}

$previousWamSetting = [bool](Get-MgGraphOption).DisableLoginByWAM
try {
    Set-MgGraphOption -DisableLoginByWAM $true | Out-Null
    # Process scope avoids the CurrentUser-scope failure observed after successful browser consent.
    # Provisioning commands must run in this same PowerShell process.
    Connect-MgGraph -ClientId $app.appId -TenantId $tenantId -Scopes $scopes -ContextScope Process -NoWelcome
    $context = Get-MgContext
    if ($null -eq $context) { throw 'Graph returned no session context. No setup commands should run; browser completion alone does not verify the Graph connection.' }
    if ($context.TenantId -ne $tenantId -or $context.ClientId -ne $app.appId -or [string]$context.AuthType -ne 'Delegated' -or [string]$context.ContextScope -ne 'Process') {
        throw "Unexpected Graph context: tenant=$($context.TenantId); client=$($context.ClientId); auth=$($context.AuthType); scope=$($context.ContextScope). No setup commands should run."
    }
    if (@($scopes | Where-Object { $_ -notin $context.Scopes }).Count) { throw 'Graph context is missing requested delegated scopes; administrator consent may be incomplete.' }
    try {
        $providers = Invoke-MgGraphRequest -Method GET -Uri 'https://graph.microsoft.com/v1.0/identity/identityProviders' -OutputType PSObject -ErrorAction Stop
        if ($null -eq $providers -or $null -eq $providers.PSObject.Properties['value']) { throw 'Unexpected identity-provider response.' }
    }
    catch { throw 'Graph session context was present, but the read-only identity-provider request failed. No setup commands should run; verify delegated consent and administrator access.' }
    [pscustomobject]@{ Status = 'Connected and identity-provider read verified'; TenantId = $context.TenantId; ClientId = $context.ClientId; AuthType = [string]$context.AuthType; ContextScope = [string]$context.ContextScope; Scopes = $context.Scopes; IdentityProviderCount = @($providers.value).Count }
}
finally { Set-MgGraphOption -DisableLoginByWAM $previousWamSetting | Out-Null }


#Requires -Version 7.2
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$TesterEmail,
    [Parameter(Mandatory)][ValidateNotNullOrEmpty()][string]$DisplayName
)

$ErrorActionPreference = 'Stop'
$tenantId = '519cfcb5-8944-4028-a4ef-22d6beecccb9'
$setupClientId = 'c2238496-66c3-4537-a50d-d5d2569c94c9'
$tenantDomain = 'matchmatefans.onmicrosoft.com'
$webName = 'Matchmate Web'
$flowName = 'Matchmate Email Sign In'
$callback = 'http://localhost:5410/signin-oidc'
$testerRoleId = '382e8b17-3936-4d68-8967-43382b17a082'
$defaultRoleId = '00000000-0000-0000-0000-000000000000'
$TesterEmail = $TesterEmail.Trim().ToLowerInvariant()
try { $address = [System.Net.Mail.MailAddress]::new($TesterEmail) } catch { throw 'TesterEmail must be a valid email address.' }
if ($address.Address -cne $TesterEmail -or $DisplayName.Trim().Length -eq 0) { throw 'Use an email address without a display-name prefix and a nonempty DisplayName.' }

function Invoke-SetupGraph {
    param([string]$Method = 'GET', [string]$Path, [object]$Body)
    $request = @{ Method = $Method; Uri = "https://graph.microsoft.com/v1.0/$Path"; OutputType = 'PSObject'; ErrorAction = 'Stop' }
    if ($null -ne $Body) { $request.ContentType = 'application/json'; $request.Body = ConvertTo-Json -InputObject $Body -Depth 30 -Compress }
    try { Invoke-MgGraphRequest @request }
    catch { throw "Graph setup $Method failed. No request body, personal identifier or token is printed. Stop and inspect the operation before retrying; earlier completed operations are retained." }
}

function Get-SetupCollection {
    param([string]$Path)
    # Bounded enumeration; fail closed rather than overlook another assignment or conflicting flow.
    $items = @()
    for ($page = 0; $page -lt 20; $page++) {
        $result = Invoke-SetupGraph -Path $Path
        if ($null -eq $result.PSObject.Properties['value']) { throw 'Unexpected Graph collection response.' }
        $items += @($result.value)
        if (-not $result.'@odata.nextLink') { return ,$items }
        $next = [Uri]$result.'@odata.nextLink'
        if ($next.Scheme -ne 'https' -or $next.Host -ne 'graph.microsoft.com' -or -not $next.AbsolutePath.StartsWith('/v1.0/')) { throw 'Unexpected Graph pagination destination.' }
        $Path = $next.PathAndQuery.Substring('/v1.0/'.Length)
    }
    throw 'Graph collection exceeded the bounded setup enumeration; no partial result is accepted.'
}

function Find-SetupObject {
    param([string]$Collection, [string]$Filter, [string]$Select)
    $encoded = [Uri]::EscapeDataString($Filter)
    $found = Get-SetupCollection -Path "${Collection}?`$filter=$encoded&`$select=$Select&`$top=2"
    if ($found.Count -gt 1) { throw 'Target lookup is ambiguous; no duplicate or unrelated object will be changed.' }
    if ($found.Count -eq 1) { return $found[0] }
}

function Assert-WebApplication {
    param($Application)
    $roles = @($Application.appRoles)
    if ($Application.displayName -cne $webName -or $Application.signInAudience -ne 'AzureADMyOrg' -or
        @($Application.web.redirectUris).Count -ne 1 -or $Application.web.redirectUris[0] -cne $callback -or
        $Application.web.implicitGrantSettings.enableAccessTokenIssuance -or $Application.web.implicitGrantSettings.enableIdTokenIssuance -or
        $Application.isFallbackPublicClient -or @($Application.publicClient.redirectUris | Where-Object { $null -ne $_ }).Count -or @($Application.spa.redirectUris | Where-Object { $null -ne $_ }).Count -or
        @($Application.requiredResourceAccess | Where-Object { $null -ne $_ }).Count -or @($Application.api.oauth2PermissionScopes | Where-Object { $null -ne $_ }).Count -or
        @($Application.passwordCredentials | Where-Object { $null -ne $_ }).Count -or @($Application.keyCredentials | Where-Object { $null -ne $_ }).Count -or
        $roles.Count -ne 1 -or $roles[0].id -ne $testerRoleId -or $roles[0].value -cne 'Tester' -or
        -not $roles[0].isEnabled -or @($roles[0].allowedMemberTypes).Count -ne 1 -or $roles[0].allowedMemberTypes[0] -cne 'User') {
        throw 'Existing/read-back Matchmate Web configuration differs from this bounded setup; review it without overwriting.'
    }
}

function Assert-Tester {
    param($User)
    $identities = @($User.identities | Where-Object { $_.signInType -eq 'federated' -and $_.issuer -eq 'mail' -and $_.issuerAssignedId -ieq $TesterEmail })
    if ($identities.Count -ne 1 -or $User.userType -ne 'Member' -or -not $User.accountEnabled -or $User.mail -ine $TesterEmail -or $TesterEmail -notin @($User.otherMails)) {
        throw 'Existing/read-back tester identity is not the expected enabled passwordless customer. No existing user is overwritten.'
    }
    if (@($User.identities | Where-Object { $_.signInType -notin @('federated','userPrincipalName') -or ($_.signInType -eq 'federated' -and ($_.issuer -ne 'mail' -or $_.issuerAssignedId -ine $TesterEmail)) }).Count) {
        throw 'Tester has additional sign-in identities outside this setup; review manually.'
    }
}

function Assert-Flow {
    param($Flow, [string]$AppId)
    $included = @($Flow.conditions.applications.includeApplications)
    $providers = @($Flow.onAuthenticationMethodLoadStart.identityProviders)
    if ($Flow.displayName -cne $flowName -or $Flow.'@odata.type' -ne '#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow' -or
        $included.Count -ne 1 -or $included[0].appId -ne $AppId -or $Flow.conditions.applications.includeAllApplications -eq $true -or
        @($Flow.conditions.applications.excludeApplications | Where-Object { $null -ne $_ }).Count -or
        $Flow.onInteractiveAuthFlowStart.'@odata.type' -ne '#microsoft.graph.onInteractiveAuthFlowStartExternalUsersSelfServiceSignUp' -or $Flow.onInteractiveAuthFlowStart.isSignUpAllowed -ne $false -or
        $Flow.onAuthenticationMethodLoadStart.'@odata.type' -ne '#microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp' -or
        $providers.Count -ne 1 -or $providers[0].id -cne 'EmailOtpSignup-OAUTH') {
        throw 'Existing/read-back flow differs from the selected-app, OTP-only, no-public-sign-up configuration; no flow is overwritten.'
    }
}

$context = Get-MgContext
if ($null -eq $context -or $context.TenantId -ne $tenantId -or $context.ClientId -ne $setupClientId -or [string]$context.AuthType -ne 'Delegated' -or [string]$context.ContextScope -ne 'Process') {
    throw 'Dot-source Connect-MatchmateAdmin.ps1 and run setup in that same expected tenant/client Delegated Process session.'
}
$requiredScopes = @('EventListener.ReadWrite.All','IdentityProvider.Read.All','Policy.ReadWrite.AuthenticationMethod','Application.ReadWrite.All','User.ReadWrite.All','AppRoleAssignment.ReadWrite.All')
if (@($requiredScopes | Where-Object { $_ -notin $context.Scopes }).Count) { throw 'The current Graph session lacks required setup scopes.' }
$providers = Get-SetupCollection -Path 'identity/identityProviders'
if (@($providers | Where-Object id -CEQ 'EmailOtpSignup-OAUTH').Count -ne 1) { throw 'The expected email OTP identity provider is unavailable.' }
$me = Invoke-SetupGraph -Path 'me?$select=id'
if (-not $me.id) { throw 'Could not verify the current administrator identity.' }
$adminSp = Find-SetupObject -Collection 'servicePrincipals' -Filter "appId eq '$setupClientId'" -Select 'id,appId,appRoleAssignmentRequired'
if (-not $adminSp) { throw 'The consented setup service principal is missing.' }
$adminAssignments = Get-SetupCollection -Path "servicePrincipals/$($adminSp.id)/appRoleAssignedTo"
if (@($adminAssignments | Where-Object { $_.principalId -ne $me.id -or $_.resourceId -ne $adminSp.id -or $_.appRoleId -ne $defaultRoleId }).Count -or $adminAssignments.Count -gt 1) { throw 'Setup application has unexpected assignments; no assignments are removed.' }
if (-not $adminAssignments.Count) { $null = Invoke-SetupGraph -Method POST -Path "servicePrincipals/$($adminSp.id)/appRoleAssignedTo" -Body @{principalId=$me.id;resourceId=$adminSp.id;appRoleId=$defaultRoleId} }
if (-not $adminSp.appRoleAssignmentRequired) { $null = Invoke-SetupGraph -Method PATCH -Path "servicePrincipals/$($adminSp.id)" -Body @{appRoleAssignmentRequired=$true} }

$appSelect = 'id,appId,displayName,signInAudience,web,publicClient,spa,isFallbackPublicClient,requiredResourceAccess,api,appRoles,passwordCredentials,keyCredentials'
$application = Find-SetupObject -Collection 'applications' -Filter "displayName eq '$webName'" -Select $appSelect
if ($application) { Assert-WebApplication $application }
else {
    $application = Invoke-SetupGraph -Method POST -Path 'applications' -Body @{
        displayName=$webName;signInAudience='AzureADMyOrg';isFallbackPublicClient=$false
        web=@{redirectUris=@($callback);implicitGrantSettings=@{enableIdTokenIssuance=$false;enableAccessTokenIssuance=$false}}
        publicClient=@{redirectUris=@()};spa=@{redirectUris=@()};requiredResourceAccess=@()
        appRoles=@(@{id=$testerRoleId;allowedMemberTypes=@('User');displayName='Tester';description='Selected Matchmate tester';value='Tester';isEnabled=$true})
    }
}
if (-not $application.id -or -not $application.appId) { throw 'Application response is missing IDs; stop and read back before retrying.' }
$webSp = Find-SetupObject -Collection 'servicePrincipals' -Filter "appId eq '$($application.appId)'" -Select 'id,appId,appRoleAssignmentRequired'
if (-not $webSp) { $webSp = Invoke-SetupGraph -Method POST -Path 'servicePrincipals' -Body @{appId=$application.appId;appRoleAssignmentRequired=$true} }
elseif (-not $webSp.appRoleAssignmentRequired) { $null = Invoke-SetupGraph -Method PATCH -Path "servicePrincipals/$($webSp.id)" -Body @{appRoleAssignmentRequired=$true} }
if (-not $webSp.id) { throw 'Customer service principal response is missing its ID.' }

$emailLiteral = $TesterEmail.Replace("'", "''")
$userSelect = 'id,userType,accountEnabled,identities,mail,otherMails'
$user = Find-SetupObject -Collection 'users' -Filter "identities/any(i:i/issuerAssignedId eq '$emailLiteral' and i/issuer eq 'mail')" -Select $userSelect
if ($user) { Assert-Tester $user }
else {
    $alias = [Guid]::NewGuid().ToString('N')
    $user = Invoke-SetupGraph -Method POST -Path 'users' -Body @{
        accountEnabled=$true;displayName=$DisplayName.Trim();userType='Member';mail=$TesterEmail;otherMails=@($TesterEmail)
        mailNickname=$alias;userPrincipalName="$alias@$tenantDomain"
        identities=@(@{signInType='federated';issuer='mail';issuerAssignedId=$TesterEmail})
    }
}
if (-not $user.id) { throw 'Tester response is missing its ID; stop and read back before retrying.' }
$assignments = Get-SetupCollection -Path "servicePrincipals/$($webSp.id)/appRoleAssignedTo"
if (@($assignments | Where-Object { $_.principalId -ne $user.id -or $_.resourceId -ne $webSp.id -or $_.appRoleId -ne $testerRoleId }).Count -or $assignments.Count -gt 1) { throw 'Customer app has unexpected assignments; this script never removes or broadens them.' }
if (-not $assignments.Count) { $null = Invoke-SetupGraph -Method POST -Path "servicePrincipals/$($webSp.id)/appRoleAssignedTo" -Body @{principalId=$user.id;resourceId=$webSp.id;appRoleId=$testerRoleId} }

$policyPath = 'policies/authenticationMethodsPolicy/authenticationMethodConfigurations/Email'
$policy = Invoke-SetupGraph -Path $policyPath
if ($policy.state -notin @('enabled','disabled') -or $policy.allowExternalIdToUseEmailOtp -notin @('default','enabled','disabled')) { throw 'Unknown email OTP policy state; no policy change made.' }
if ($policy.state -ne 'enabled' -or $policy.allowExternalIdToUseEmailOtp -eq 'disabled') {
    # PATCH only these two properties, retaining all existing include/exclude targets.
    $null = Invoke-SetupGraph -Method PATCH -Path $policyPath -Body @{'@odata.type'='#microsoft.graph.emailAuthenticationMethodConfiguration';state='enabled';allowExternalIdToUseEmailOtp='enabled'}
}
$flows = Get-SetupCollection -Path 'identity/authenticationEventsFlows'
$matchingFlows = @($flows | Where-Object displayName -EQ $flowName)
if ($matchingFlows.Count -gt 1) { throw 'Multiple exact-name customer flows found.' }
if (@($flows | Where-Object { $_.displayName -cne $flowName -and (@($_.conditions.applications.includeApplications | Where-Object appId -EQ $application.appId).Count -or $_.conditions.applications.includeAllApplications -eq $true) }).Count) { throw 'Another flow already includes this application; no other flow is changed.' }
if ($matchingFlows.Count) { $flow=$matchingFlows[0]; Assert-Flow $flow $application.appId }
else {
    $flow = Invoke-SetupGraph -Method POST -Path 'identity/authenticationEventsFlows' -Body @{
        '@odata.type'='#microsoft.graph.externalUsersSelfServiceSignUpEventsFlow';displayName=$flowName
        conditions=@{applications=@{includeApplications=@(@{appId=$application.appId})}}
        onAuthenticationMethodLoadStart=@{'@odata.type'='#microsoft.graph.onAuthenticationMethodLoadStartExternalUsersSelfServiceSignUp';identityProviders=@(@{id='EmailOtpSignup-OAUTH'})}
        onInteractiveAuthFlowStart=@{'@odata.type'='#microsoft.graph.onInteractiveAuthFlowStartExternalUsersSelfServiceSignUp';isSignUpAllowed=$false}
    }
}

# Verify durable configuration without emitting emails, display names, tokens or response bodies.
$application = Invoke-SetupGraph -Path "applications/$($application.id)?`$select=$appSelect"
Assert-WebApplication $application
$webSp = Invoke-SetupGraph -Path "servicePrincipals/$($webSp.id)?`$select=id,appId,appRoleAssignmentRequired"
if (-not $webSp.appRoleAssignmentRequired -or $webSp.appId -ne $application.appId) { throw 'Customer assignment-required read-back failed.' }
$user = Invoke-SetupGraph -Path "users/$($user.id)?`$select=$userSelect"
Assert-Tester $user
$verifiedAssignments = Get-SetupCollection -Path "servicePrincipals/$($webSp.id)/appRoleAssignedTo"
if ($verifiedAssignments.Count -ne 1 -or $verifiedAssignments[0].principalId -ne $user.id -or $verifiedAssignments[0].appRoleId -ne $testerRoleId -or $verifiedAssignments[0].resourceId -ne $webSp.id) { throw 'Selected tester assignment read-back failed.' }
$flow = Invoke-SetupGraph -Path "identity/authenticationEventsFlows/$($flow.id)"
Assert-Flow $flow $application.appId
$policy = Invoke-SetupGraph -Path $policyPath
if ($policy.state -ne 'enabled' -or $policy.allowExternalIdToUseEmailOtp -notin @('default','enabled')) { throw 'Email OTP policy read-back failed.' }
$adminSp = Invoke-SetupGraph -Path "servicePrincipals/$($adminSp.id)?`$select=id,appRoleAssignmentRequired"
$verifiedAdminAssignments = Get-SetupCollection -Path "servicePrincipals/$($adminSp.id)/appRoleAssignedTo"
if (-not $adminSp.appRoleAssignmentRequired -or $verifiedAdminAssignments.Count -ne 1 -or $verifiedAdminAssignments[0].principalId -ne $me.id -or $verifiedAdminAssignments[0].appRoleId -ne $defaultRoleId) { throw 'Setup application administrator-only assignment read-back failed.' }
[pscustomobject]@{Status='Configuration read-back verified; real tester OTP sign-in unverified';TenantId=$tenantId;WebClientId=$application.appId;WebApplicationObjectId=$application.id;WebServicePrincipalId=$webSp.id;FlowId=$flow.id;TesterRoleId=$testerRoleId;PublicSignUpAllowed=$false;AssignmentRequired=$true}

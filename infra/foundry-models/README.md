# Matchmate Foundry models

This resource-group-scoped template creates one dedicated `AIServices` S0 account and Matchmate project in the existing Sweden Central resource group. It deploys Decision-1 version 1 and Luna version 2026-09-22, each EU `DataZoneStandard` capacity 30, with no automatic model upgrade. Service-default responsible-AI policy applies. EU processing is not Sweden-only.

The account has a deterministic custom subdomain, system-assigned identity, public HTTPS access, disabled local/key authentication and disabled dynamic quota. It adds no private endpoints, dependent stores, Container Apps resources or deployment workflows. Quota/capacity availability still requires live validation.

The coordinator supplies `operatorObjectId` (Entra user object ID) and optionally `webPrincipalObjectId` (existing Web managed-identity principal object ID). Both receive **Cognitive Services User** (`a97b65f3-24c7-4388-baec-2e87135dc908`) scoped only to the new account, as selected after live role-definition review. The role includes key-read permission, but local authentication is disabled and this template creates no secret connections. OpenAI User alone lacks Decision inference; Foundry User includes broader permissions. No subscription-level assignment or custom role is created.

Prepare/review the compiled template before the coordinator performs Azure validation, what-if, deployment and readback. Local compilation:

```powershell
& "$env:USERPROFILE/.azure/bin/bicep.exe" build infra/foundry-models/main.bicep --outfile .azure/foundry-models.compiled.json
```

Creating these model resources does not deploy the application or activate local model settings. The coordinator owns credentials/configuration and explicitly starts bounded inference only after review. Existing application deployment remains stopped.

References: [account schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts), [project schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts/projects), [deployment schema](https://learn.microsoft.com/en-us/azure/templates/microsoft.cognitiveservices/2025-06-01/accounts/deployments), and [role definitions](https://learn.microsoft.com/en-us/azure/role-based-access-control/built-in-roles/ai-machine-learning). See [getting started](../../public-docs/GETTING-STARTED.md#connect-ai-when-available) for local model connection prerequisites.
